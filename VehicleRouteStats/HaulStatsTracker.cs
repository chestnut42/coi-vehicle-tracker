using System.Collections.Generic;
using System.Globalization;
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
    private const string StateSchemaVersion = "v2";

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
    private readonly Dict<ProductProto, ProductHaulStats>[,] m_months =
        new Dict<ProductProto, ProductHaulStats>[CategoryCount, MonthsTracked];

    // Burn we cannot pin to a product, split by the job that was running purely so it can be eyeballed.
    // Not split by category or product; the UI shows bare figures next to the total.
    private sealed class UnattributedBucket
    {
        public readonly double[] Fuel = new double[MonthsTracked];
        public readonly double[] Maintenance = new double[MonthsTracked];
    }

    private readonly Dict<string, UnattributedBucket> m_unattributed = new();

    private int m_monthIndex;
    private int m_lastStep = -1;

    /// <summary>Fuel and maintenance products observed on the fleet, for the UI to draw icons with.</summary>
    public ProductProto FuelProduct { get; private set; }

    public ProductProto MaintenanceProduct { get; private set; }

    public HaulStatsTracker(IEntitiesManager entities, ISimLoopEvents simLoopEvents)
    {
        m_entities = entities;
        m_simLoopEvents = simLoopEvents;
        for (int category = 0; category < CategoryCount; category++)
        {
            for (int month = 0; month < MonthsTracked; month++)
            {
                m_months[category, month] = new Dict<ProductProto, ProductHaulStats>();
            }
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
            m_months[category, m_monthIndex].Clear();
        }

        foreach (var bucket in m_unattributed)
        {
            bucket.Value.Fuel[m_monthIndex] = 0;
            bucket.Value.Maintenance[m_monthIndex] = 0;
        }
    }

    public void OnEntityRemoved(IEntity entity) => m_truckStates.Remove(entity.Id.Value);

    public void Reset()
    {
        foreach (var bucket in m_months)
        {
            bucket.Clear();
        }

        m_unattributed.Clear();
        m_truckStates.Clear();
        m_monthIndex = 0;
    }

    /// <summary>
    /// Fuel and maintenance burned by running that cannot be pinned to a product, broken down by the
    /// job that was running. Scoped by month like <see cref="Snapshot"/>, but not by category — it
    /// has no meaningful category. Ordered by fuel, descending.
    /// </summary>
    public List<(string Job, double Fuel, double Maintenance)> UnattributedBreakdown(int months)
    {
        int count = months < MonthsTracked ? months : MonthsTracked;
        var result = new List<(string Job, double Fuel, double Maintenance)>();

        foreach (var entry in m_unattributed)
        {
            double fuel = 0;
            double maintenance = 0;

            for (int i = 0; i < count; i++)
            {
                int index = ((m_monthIndex - i) % MonthsTracked + MonthsTracked) % MonthsTracked;
                fuel += entry.Value.Fuel[index];
                maintenance += entry.Value.Maintenance[index];
            }

            if (fuel != 0 || maintenance != 0)
            {
                result.Add((entry.Key, fuel, maintenance));
            }
        }

        result.Sort((a, b) => b.Fuel.CompareTo(a.Fuel));
        return result;
    }

    /// <summary>
    /// Totals over the last <paramref name="months"/> buckets, most recent (partial) month included,
    /// restricted to the categories whose flag is set.
    /// </summary>
    public IEnumerable<KeyValuePair<ProductProto, ProductHaulStats>> Snapshot(
        int months,
        bool general,
        bool mining,
        bool refueling)
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

            for (int i = 0; i < count; i++)
            {
                int index = ((m_monthIndex - i) % MonthsTracked + MonthsTracked) % MonthsTracked;
                foreach (var entry in m_months[category, index])
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

        return totals;
    }

    /// <summary>
    /// Flattens every bucket to one line per (category, month, product). Months are written as an
    /// offset back from the current one so the ring's rotation does not have to survive the round
    /// trip. Numbers are invariant-culture; a decimal comma would otherwise split a record in half.
    /// </summary>
    public string SerializeState(int maxLength)
    {
        var sb = new StringBuilder();
        sb.Append(StateSchemaVersion);

        for (int offset = 0; offset < MonthsTracked; offset++)
        {
            int index = ((m_monthIndex - offset) % MonthsTracked + MonthsTracked) % MonthsTracked;

            foreach (var entry in m_unattributed)
            {
                if (entry.Value.Fuel[index] == 0 && entry.Value.Maintenance[index] == 0)
                {
                    continue;
                }

                sb.Append(";j,").Append(offset).Append(',').Append(entry.Key).Append(',')
                    .Append(Num(entry.Value.Fuel[index])).Append(',')
                    .Append(Num(entry.Value.Maintenance[index]));
            }

            for (int category = 0; category < CategoryCount; category++)
            {
                foreach (var entry in m_months[category, index])
                {
                    ProductHaulStats s = entry.Value;
                    int lengthBeforeMonth = sb.Length;

                    sb.Append(';').Append(category).Append(',').Append(offset).Append(',')
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
                        sb.Length = lengthBeforeMonth;
                        return sb.ToString();
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Restores what <see cref="SerializeState"/> wrote. Anything unparseable is skipped rather than
    /// thrown, so a blob from an older mod version costs history but never blocks a load.
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

            // "u" is the earlier single-figure form; fold it into one bucket rather than lose it.
            if ((f.Length == 4 && f[0] == "u") || (f.Length == 5 && f[0] == "j"))
            {
                if (int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int unattributedOffset)
                    && unattributedOffset >= 0 && unattributedOffset < MonthsTracked)
                {
                    bool isNamed = f[0] == "j";
                    UnattributedBucket bucket = GetOrAddUnattributed(isNamed ? f[2] : "Earlier");
                    int slot = (MonthsTracked - unattributedOffset) % MonthsTracked;
                    bucket.Fuel[slot] = Parse(isNamed ? f[3] : f[2]);
                    bucket.Maintenance[slot] = Parse(isNamed ? f[4] : f[3]);
                }

                continue;
            }

            // 8 fields is the earlier form, before pickup burn was tracked separately.
            if ((f.Length != 8 && f.Length != 11)
                || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int category)
                || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset)
                || category < 0 || category >= CategoryCount
                || offset < 0 || offset >= MonthsTracked
                || !protosDb.TryGetProto(new Proto.ID(f[2]), out ProductProto product))
            {
                continue;
            }

            ProductHaulStats stats = GetOrAdd(m_months[category, (MonthsTracked - offset) % MonthsTracked], product);
            stats.Fuel = Parse(f[3]);
            stats.Maintenance = Parse(f[4]);
            stats.Distance = Parse(f[5]);
            stats.Deliveries = (int)Parse(f[6]);
            stats.Delivered = Parse(f[7]);

            if (f.Length == 11)
            {
                stats.PickupFuel = Parse(f[8]);
                stats.PickupMaintenance = Parse(f[9]);
                stats.PickupDistance = Parse(f[10]);
            }
        }
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
                ProductHaulStats stats = GetOrAdd(BucketFor(truck, pq.Product), pq.Product);
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
                truck.CurrentJob.ValueOrNull?.GetType().Name ?? "NoJob");
            bucket.Fuel[m_monthIndex] += fuel;
            bucket.Maintenance[m_monthIndex] += maintenance;
        }

        RecordDeliveries(truck, cargo, state);
    }

    private Dict<ProductProto, ProductHaulStats> BucketFor(Truck truck, ProductProto product) =>
        m_months[(int)Classify(truck, product), m_monthIndex];

    private void RecordDeliveries(Truck truck, Lyst<ProductQuantity> cargo, TruckState state)
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
                ProductHaulStats stats = GetOrAdd(BucketFor(truck, product), product);
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

    private UnattributedBucket GetOrAddUnattributed(string job)
    {
        if (!m_unattributed.TryGetValue(job, out UnattributedBucket bucket))
        {
            bucket = new UnattributedBucket();
            m_unattributed.Add(job, bucket);
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
