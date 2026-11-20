using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Mine;
using Mafi.Core.Entities;
using Mafi.Core.Maintenance;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;
using Mafi.Core.Vehicles;
using Mafi.Core.Vehicles.Jobs;
using Mafi.Core.Vehicles.TreeHarvesters;
using Mafi.Core.Vehicles.Trucks;

namespace VehicleRouteStats;

/// <summary>
/// Mirrors the split the vanilla truck stats tab uses for its three filter toggles.
/// </summary>
public enum HaulCategory
{
    General = 0,
    Mining = 1,
    Refueling = 2,
}

public sealed class ProductHaulStats
{
    /// <summary>Burn while actually carrying this product.</summary>
    public double Fuel;

    public double Maintenance;
    public double Distance;

    /// <summary>Burn on the empty run that ended in loading this product, billed retroactively.</summary>
    public double PickupFuel;

    public double PickupMaintenance;
    public double PickupDistance;

    public int Deliveries;
    public double Delivered;

    public void Add(ProductHaulStats other)
    {
        Fuel += other.Fuel;
        Maintenance += other.Maintenance;
        Distance += other.Distance;
        PickupFuel += other.PickupFuel;
        PickupMaintenance += other.PickupMaintenance;
        PickupDistance += other.PickupDistance;
        Deliveries += other.Deliveries;
        Delivered += other.Delivered;
    }
}

/// <summary>
/// Attributes each truck's fuel burn, maintenance drain and distance driven to the products it is
/// carrying, bucketed by month so the UI can scope to a trailing window.
///
/// Cost is only accrued while a truck is actually consuming: fuel follows the game's own
/// <see cref="Mafi.Core.Vehicles.VehicleFuelConsumption"/> state and engine-on check, maintenance
/// follows the same idle gate the maintenance system uses. A loaded truck parked with its engine
/// off therefore adds nothing, and a paused game advances no sim ticks at all.
/// </summary>
public sealed class HaulStatsTracker
{
    public const int MonthsTracked = 12;
    private const int CategoryCount = 3;
    private const string StateSchemaVersion = "v3";

    /// <summary>Written in place of a group id for the catch-all slot; not a valid proto id.</summary>
    private const string UnknownGroupId = "?";

    private sealed class TruckState
    {
        public Fix64 LastOdometer;
        public readonly Dict<ProductProto, double> LastCargo = new();

        // Burn on the current empty run, held until the truck loads something and we learn what the
        // run was for. Only accumulated while moving, so a truck idling for months cannot dump a
        // year of fuel onto the first unit it happens to pick up.
        public double PendingFuel;
        public double PendingMaintenance;
        public double PendingDistance;

        public bool HasPending => PendingFuel != 0 || PendingMaintenance != 0 || PendingDistance != 0;

        public void ClearPending()
        {
            PendingFuel = 0;
            PendingMaintenance = 0;
            PendingDistance = 0;
        }
    }

    private readonly IEntitiesManager m_entities;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly Lyst<ProductQuantity> m_cargoBuffer = new(4);
    private readonly Dict<int, TruckState> m_truckStates = new();

    // Vehicle groups are prototypes, so the set is whatever this install has registered — four in the
    // base game, more if another mod adds vehicles. One extra slot sits past the end as a catch-all
    // for a truck whose group somehow is not in the db, so its burn is never silently discarded.
    private readonly VehicleGroupProto[] m_groups;
    private readonly Dict<VehicleGroupProto, int> m_groupSlots = new();

    // [category, group slot, month]. Category and group are filtered independently in the UI, so the
    // buckets have to be the cross product: per-category and per-group totals cannot be intersected
    // after the fact.
    private readonly Dict<ProductProto, ProductHaulStats>[,,] m_months;

    // Burn we cannot pin to a product, split by the job that was running purely so it can be eyeballed.
    // Per group slot, so the figure beside the total still agrees with the group filter.
    private sealed class UnattributedBucket
    {
        public readonly double[] Fuel = new double[MonthsTracked];
        public readonly double[] Maintenance = new double[MonthsTracked];
    }

    private readonly Dict<string, UnattributedBucket>[] m_unattributed;

    private int m_monthIndex;
    private int m_lastStep = -1;

    /// <summary>Fuel and maintenance products observed on the fleet, for the UI to draw icons with.</summary>
    public ProductProto FuelProduct { get; private set; }

    public ProductProto MaintenanceProduct { get; private set; }

    /// <summary>The vehicle groups to offer as filters, in display order.</summary>
    public IReadOnlyList<VehicleGroupProto> Groups => m_groups;

    /// <summary>Group filter slots, including the trailing catch-all. Indexes every filter array.</summary>
    public int SlotCount => m_groups.Length + 1;

    /// <summary>Slot that collects trucks whose group is not registered; normally stays empty.</summary>
    public int UnknownSlot => m_groups.Length;

    public HaulStatsTracker(IEntitiesManager entities, ISimLoopEvents simLoopEvents, ProtosDb protosDb)
    {
        m_entities = entities;
        m_simLoopEvents = simLoopEvents;

        // Ordered by the smallest truck each group holds, which puts pickups before trucks before haul
        // trucks without hardcoding the base game's four ids, and gives a mod-added group a sensible
        // place to land. Id breaks ties so the order is stable across runs.
        m_groups = protosDb.All<VehicleGroupProto>()
            .OrderBy(g => g.Trucks.Count == 0 ? int.MaxValue : g.Trucks.Min(t => t.CapacityBase.Value))
            .ThenBy(g => g.Id.Value)
            .ToArray();

        for (int i = 0; i < m_groups.Length; i++)
        {
            m_groupSlots.Add(m_groups[i], i);
        }

        m_months = new Dict<ProductProto, ProductHaulStats>[CategoryCount, SlotCount, MonthsTracked];
        for (int category = 0; category < CategoryCount; category++)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                for (int month = 0; month < MonthsTracked; month++)
                {
                    m_months[category, slot, month] = new Dict<ProductProto, ProductHaulStats>();
                }
            }
        }

        m_unattributed = new Dict<string, UnattributedBucket>[SlotCount];
        for (int slot = 0; slot < SlotCount; slot++)
        {
            m_unattributed[slot] = new Dict<string, UnattributedBucket>();
        }
    }

    public void OnSimTick()
    {
        // UpdateAfterCmdProc still fires while the game is paused, and fuel/maintenance are read as
        // rates rather than deltas, so they would keep accruing. Only bill a step the sim actually ran.
        int step = m_simLoopEvents.CurrentStep.Value;
        if (m_simLoopEvents.IsSimPaused || step == m_lastStep)
        {
            return;
        }

        m_lastStep = step;

        foreach (Truck truck in m_entities.GetAllEntitiesOfType<Truck>())
        {
            if (!truck.IsDestroyed)
            {
                SampleTruck(truck);
            }
        }
    }

    public void OnNewMonth()
    {
        m_monthIndex = (m_monthIndex + 1) % MonthsTracked;
        for (int category = 0; category < CategoryCount; category++)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                m_months[category, slot, m_monthIndex].Clear();
            }
        }

        foreach (var slotBuckets in m_unattributed)
        {
            foreach (var bucket in slotBuckets)
            {
                bucket.Value.Fuel[m_monthIndex] = 0;
                bucket.Value.Maintenance[m_monthIndex] = 0;
            }
        }
    }

    public void OnEntityRemoved(IEntity entity) => m_truckStates.Remove(entity.Id.Value);

    public void Reset()
    {
        foreach (var bucket in m_months)
        {
            bucket.Clear();
        }

        foreach (var slotBuckets in m_unattributed)
        {
            slotBuckets.Clear();
        }

        m_truckStates.Clear();
        m_monthIndex = 0;
    }

    /// <summary>Whether a group slot holds anything over the whole ring, to hide empty filters.</summary>
    public bool HasAnyDataIn(int slot)
    {
        for (int category = 0; category < CategoryCount; category++)
        {
            for (int month = 0; month < MonthsTracked; month++)
            {
                if (m_months[category, slot, month].Count > 0)
                {
                    return true;
                }
            }
        }

        return m_unattributed[slot].Count > 0;
    }

    /// <summary>
    /// Fuel and maintenance burned by running that cannot be pinned to a product, broken down by the
    /// job that was running. Scoped by month and group like <see cref="Snapshot"/>, but not by
    /// category — it has no meaningful category. Ordered by fuel, descending.
    /// </summary>
    public List<(string Job, double Fuel, double Maintenance)> UnattributedBreakdown(
        int months,
        bool[] includeSlot)
    {
        int count = months < MonthsTracked ? months : MonthsTracked;
        var merged = new Dict<string, (double Fuel, double Maintenance)>();

        for (int slot = 0; slot < SlotCount; slot++)
        {
            if (!includeSlot[slot])
            {
                continue;
            }

            foreach (var entry in m_unattributed[slot])
            {
                double fuel = 0;
                double maintenance = 0;

                for (int i = 0; i < count; i++)
                {
                    int index = ((m_monthIndex - i) % MonthsTracked + MonthsTracked) % MonthsTracked;
                    fuel += entry.Value.Fuel[index];
                    maintenance += entry.Value.Maintenance[index];
                }

                merged.TryGetValue(entry.Key, out var running);
                merged[entry.Key] = (running.Fuel + fuel, running.Maintenance + maintenance);
            }
        }

        var result = new List<(string Job, double Fuel, double Maintenance)>();
        foreach (var entry in merged)
        {
            if (entry.Value.Fuel != 0 || entry.Value.Maintenance != 0)
            {
                result.Add((entry.Key, entry.Value.Fuel, entry.Value.Maintenance));
            }
        }

        result.Sort((a, b) => b.Fuel.CompareTo(a.Fuel));
        return result;
    }

    /// <summary>
    /// Totals over the last <paramref name="months"/> buckets, most recent (partial) month included,
    /// restricted to the categories whose flag is set and the group slots whose flag is set.
    /// </summary>
    public IEnumerable<KeyValuePair<ProductProto, ProductHaulStats>> Snapshot(
        int months,
        bool general,
        bool mining,
        bool refueling,
        bool[] includeSlot)
    {
        var totals = new Dict<ProductProto, ProductHaulStats>();
        int count = months < MonthsTracked ? months : MonthsTracked;

        for (int category = 0; category < CategoryCount; category++)
        {
            bool include = (HaulCategory)category switch
            {
                HaulCategory.General => general,
                HaulCategory.Mining => mining,
                _ => refueling,
            };

            if (!include)
            {
                continue;
            }

            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (!includeSlot[slot])
                {
                    continue;
                }

                for (int i = 0; i < count; i++)
                {
                    int index = ((m_monthIndex - i) % MonthsTracked + MonthsTracked) % MonthsTracked;
                    foreach (var entry in m_months[category, slot, index])
                    {
                        if (!totals.TryGetValue(entry.Key, out ProductHaulStats sum))
                        {
                            sum = new ProductHaulStats();
                            totals.Add(entry.Key, sum);
                        }

                        sum.Add(entry.Value);
                    }
                }
            }
        }

        return totals;
    }

    /// <summary>
    /// Flattens every bucket to one line per (category, group, month, product). Months are written as
    /// an offset back from the current one so the ring's rotation does not have to survive the round
    /// trip, and groups by proto id so the slot ordering does not either. Numbers are
    /// invariant-culture; a decimal comma would otherwise split a record in half.
    /// </summary>
    public string SerializeState(int maxLength)
    {
        var sb = new StringBuilder();
        sb.Append(StateSchemaVersion);

        for (int offset = 0; offset < MonthsTracked; offset++)
        {
            int index = ((m_monthIndex - offset) % MonthsTracked + MonthsTracked) % MonthsTracked;

            for (int slot = 0; slot < SlotCount; slot++)
            {
                string groupId = GroupIdOf(slot);

                foreach (var entry in m_unattributed[slot])
                {
                    if (entry.Value.Fuel[index] == 0 && entry.Value.Maintenance[index] == 0)
                    {
                        continue;
                    }

                    sb.Append(";j,").Append(groupId).Append(',').Append(offset).Append(',')
                        .Append(entry.Key).Append(',')
                        .Append(Num(entry.Value.Fuel[index])).Append(',')
                        .Append(Num(entry.Value.Maintenance[index]));
                }

                for (int category = 0; category < CategoryCount; category++)
                {
                    foreach (var entry in m_months[category, slot, index])
                    {
                        ProductHaulStats s = entry.Value;
                        int lengthBeforeRecord = sb.Length;

                        sb.Append(';').Append(category).Append(',').Append(groupId).Append(',')
                            .Append(offset).Append(',')
                            .Append(entry.Key.Id.Value).Append(',')
                            .Append(Num(s.Fuel)).Append(',')
                            .Append(Num(s.Maintenance)).Append(',')
                            .Append(Num(s.Distance)).Append(',')
                            .Append(s.Deliveries.ToString(CultureInfo.InvariantCulture)).Append(',')
                            .Append(Num(s.Delivered)).Append(',')
                            .Append(Num(s.PickupFuel)).Append(',')
                            .Append(Num(s.PickupMaintenance)).Append(',')
                            .Append(Num(s.PickupDistance));

                        // Rather than fail the save, drop the oldest months that no longer fit.
                        if (sb.Length > maxLength)
                        {
                            sb.Length = lengthBeforeRecord;
                            return sb.ToString();
                        }
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Restores what <see cref="SerializeState"/> wrote. Anything unparseable is skipped rather than
    /// thrown, so a blob from an older mod version costs history but never blocks a load. Blobs from
    /// before the vehicle-group split carry no group and are dropped wholesale by the version check.
    /// </summary>
    public void RestoreState(string data, ProtosDb protosDb)
    {
        Reset();

        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        string[] records = data.Split(';');
        if (records.Length == 0 || records[0] != StateSchemaVersion)
        {
            return;
        }

        for (int i = 1; i < records.Length; i++)
        {
            string[] f = records[i].Split(',');

            if (f.Length == 6 && f[0] == "j")
            {
                if (TryParseSlot(f[1], protosDb, out int jobSlot)
                    && int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int jobOffset)
                    && jobOffset >= 0 && jobOffset < MonthsTracked)
                {
                    UnattributedBucket bucket = GetOrAddUnattributed(jobSlot, f[3]);
                    int monthSlot = (MonthsTracked - jobOffset) % MonthsTracked;
                    bucket.Fuel[monthSlot] = Parse(f[4]);
                    bucket.Maintenance[monthSlot] = Parse(f[5]);
                }

                continue;
            }

            if (f.Length != 12
                || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int category)
                || !TryParseSlot(f[1], protosDb, out int slot)
                || !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset)
                || category < 0 || category >= CategoryCount
                || offset < 0 || offset >= MonthsTracked
                || !protosDb.TryGetProto(new Proto.ID(f[3]), out ProductProto product))
            {
                continue;
            }

            ProductHaulStats stats = GetOrAdd(
                m_months[category, slot, (MonthsTracked - offset) % MonthsTracked],
                product);

            stats.Fuel = Parse(f[4]);
            stats.Maintenance = Parse(f[5]);
            stats.Distance = Parse(f[6]);
            stats.Deliveries = (int)Parse(f[7]);
            stats.Delivered = Parse(f[8]);
            stats.PickupFuel = Parse(f[9]);
            stats.PickupMaintenance = Parse(f[10]);
            stats.PickupDistance = Parse(f[11]);
        }
    }

    private string GroupIdOf(int slot) => slot == UnknownSlot ? UnknownGroupId : m_groups[slot].Id.Value;

    /// <summary>
    /// Resolves a serialized group id back to its slot. A group that no longer exists — another mod
    /// removed, say — lands in the catch-all rather than taking a live group's numbers with it.
    /// </summary>
    private bool TryParseSlot(string groupId, ProtosDb protosDb, out int slot)
    {
        if (groupId == UnknownGroupId)
        {
            slot = UnknownSlot;
            return true;
        }

        if (protosDb.TryGetProto(new Proto.ID(groupId), out VehicleGroupProto group)
            && m_groupSlots.TryGetValue(group, out slot))
        {
            return true;
        }

        slot = UnknownSlot;
        return true;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Parse(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

    /// <summary>
    /// Same split as <c>VehicleJobStatsManager.RecordJobStatsFor</c>: a truck hauling loose product
    /// for a mine tower, or anything for a tree harvester, counts as mining; a truck currently
    /// running a refuelling job counts as refuelling; everything else is general.
    /// </summary>
    private static HaulCategory Classify(Truck truck, ProductProto product)
    {
        if (truck.CurrentJob.ValueOrNull is RefuelOtherVehicleJob)
        {
            return HaulCategory.Refueling;
        }

        var assignedTo = truck.AssignedTo.ValueOrNull;
        if (assignedTo is TreeHarvester)
        {
            return HaulCategory.Mining;
        }

        if (assignedTo is MineTower && product.Type.Matches(LooseProductProto.ProductType))
        {
            return HaulCategory.Mining;
        }

        return HaulCategory.General;
    }

    /// <summary>
    /// Which filter slot a truck's numbers belong in. The group aggregates a tier's fuel and
    /// attachment variants already, so a diesel and a hydrogen haul truck land together.
    /// </summary>
    private int SlotFor(Truck truck)
    {
        VehicleGroupProto group = truck.Prototype.VehicleGroup;
        if (group != null && m_groupSlots.TryGetValue(group, out int slot))
        {
            return slot;
        }

        return UnknownSlot;
    }

    private void SampleTruck(Truck truck)
    {
        if (!m_truckStates.TryGetValue(truck.Id.Value, out TruckState state))
        {
            // Seed the odometer so a truck that already exists doesn't book its whole history now.
            state = new TruckState { LastOdometer = truck.LifetimeDistanceTraveled };
            m_truckStates.Add(truck.Id.Value, state);
        }

        double distance = (truck.LifetimeDistanceTraveled - state.LastOdometer).ToDouble();
        state.LastOdometer = truck.LifetimeDistanceTraveled;
        if (distance < 0)
        {
            distance = 0;
        }

        double fuel = truck.IsEngineOn
            ? truck.GetCurrentFuelUsagePer60().Value.ToDouble() / Duration.OneMinute.Ticks
            : 0;

        MaintenanceCosts costs = truck.Maintenance.Costs;
        double maintenance = ((IMaintainedEntity)truck).IsIdleForMaintenance
            ? 0
            : costs.MaintenancePerMonth.Value.ToDouble() / Mafi.Core.Simulation.Calendar.TICKS_PER_MONTH;

        MaintenanceProduct ??= costs.Product;
        if (FuelProduct == null && truck.FuelTank.HasValue)
        {
            FuelProduct = truck.FuelTank.Value.Proto.Product;
        }

        int slot = SlotFor(truck);

        Lyst<ProductQuantity> cargo = m_cargoBuffer;
        cargo.Clear();
        truck.GetCargoProducts(cargo);

        double totalQuantity = 0;
        foreach (ProductQuantity pq in cargo)
        {
            totalQuantity += pq.Quantity.Value;
        }

        if (totalQuantity > 0)
        {
            // The empty run that got the truck here is billed to whatever it actually loaded, which
            // is more honest than reading the job's intent: a pickup can fail, re-route, or end up
            // collecting something else entirely.
            //
            // Held until the loaded truck departs, not released the moment the first crate lands: a
            // mixed load arrives over several ticks, and flushing early would hand the whole pickup
            // cost to whichever product happened to be loaded first.
            bool flushPending = truck.IsMoving && state.HasPending;

            foreach (ProductQuantity pq in cargo)
            {
                // A mixed load splits the trip's cost by how much of it each product occupies.
                double share = pq.Quantity.Value / totalQuantity;
                ProductHaulStats stats = GetOrAdd(BucketFor(truck, slot, pq.Product), pq.Product);
                stats.Fuel += fuel * share;
                stats.Maintenance += maintenance * share;
                stats.Distance += distance * share;

                if (flushPending)
                {
                    stats.PickupFuel += state.PendingFuel * share;
                    stats.PickupMaintenance += state.PendingMaintenance * share;
                    stats.PickupDistance += state.PendingDistance * share;
                }
            }

            if (flushPending)
            {
                state.ClearPending();
            }
        }
        else if (truck.IsMoving)
        {
            // Empty and moving: hold the cost until the truck loads and reveals what the run was for.
            state.PendingFuel += fuel;
            state.PendingMaintenance += maintenance;
            state.PendingDistance += distance;
        }
        else if (fuel != 0 || maintenance != 0)
        {
            // Empty and stationary — queue idling, refuelling, waiting. Not the cost of hauling
            // anything, so it stays out of the product rows.
            UnattributedBucket bucket = GetOrAddUnattributed(
                slot,
                truck.CurrentJob.ValueOrNull?.GetType().Name ?? "NoJob");
            bucket.Fuel[m_monthIndex] += fuel;
            bucket.Maintenance[m_monthIndex] += maintenance;
        }

        RecordDeliveries(truck, slot, cargo, state);
    }

    private Dict<ProductProto, ProductHaulStats> BucketFor(Truck truck, int slot, ProductProto product) =>
        m_months[(int)Classify(truck, product), slot, m_monthIndex];

    private void RecordDeliveries(Truck truck, int slot, Lyst<ProductQuantity> cargo, TruckState state)
    {
        List<ProductProto> delivered = null;
        foreach (var entry in state.LastCargo)
        {
            bool stillAboard = false;
            foreach (ProductQuantity pq in cargo)
            {
                if (pq.Product == entry.Key)
                {
                    stillAboard = true;
                    break;
                }
            }

            if (!stillAboard)
            {
                delivered ??= new List<ProductProto>();
                delivered.Add(entry.Key);
            }
        }

        if (delivered != null)
        {
            foreach (ProductProto product in delivered)
            {
                ProductHaulStats stats = GetOrAdd(BucketFor(truck, slot, product), product);
                stats.Deliveries++;
                stats.Delivered += state.LastCargo[product];
                state.LastCargo.Remove(product);
            }
        }

        foreach (ProductQuantity pq in cargo)
        {
            state.LastCargo[pq.Product] = pq.Quantity.Value;
        }
    }

    private UnattributedBucket GetOrAddUnattributed(int slot, string job)
    {
        Dict<string, UnattributedBucket> buckets = m_unattributed[slot];
        if (!buckets.TryGetValue(job, out UnattributedBucket bucket))
        {
            bucket = new UnattributedBucket();
            buckets.Add(job, bucket);
        }

        return bucket;
    }

    private static ProductHaulStats GetOrAdd(Dict<ProductProto, ProductHaulStats> bucket, ProductProto product)
    {
        if (!bucket.TryGetValue(product, out ProductHaulStats stats))
        {
            stats = new ProductHaulStats();
            bucket.Add(product, stats);
        }

        return stats;
    }
}
