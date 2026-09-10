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
    public double Fuel;
    public double Maintenance;
    public double Distance;
    public int Deliveries;
    public double Delivered;

    public void Add(ProductHaulStats other)
    {
        Fuel += other.Fuel;
        Maintenance += other.Maintenance;
        Distance += other.Distance;
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
    private const string StateSchemaVersion = "v1";

    private sealed class TruckState
    {
        public Fix64 LastOdometer;
        public readonly Dict<ProductProto, double> LastCargo = new();
    }

    private readonly IEntitiesManager m_entities;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly Lyst<ProductQuantity> m_cargoBuffer = new(4);
    private readonly Dict<int, TruckState> m_truckStates = new();
    private readonly Dict<ProductProto, ProductHaulStats>[,] m_months =
        new Dict<ProductProto, ProductHaulStats>[CategoryCount, MonthsTracked];

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
    }

    public void OnEntityRemoved(IEntity entity) => m_truckStates.Remove(entity.Id.Value);

    public void Reset()
    {
        foreach (var bucket in m_months)
        {
            bucket.Clear();
        }

        m_truckStates.Clear();
        m_monthIndex = 0;
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
                        .Append(Num(s.Delivered));

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
            if (f.Length != 8
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
        truck.Cargo.GetCargoProducts(cargo);

        double totalQuantity = 0;
        foreach (ProductQuantity pq in cargo)
        {
            totalQuantity += pq.Quantity.Value;
        }

        if (totalQuantity > 0)
        {
            foreach (ProductQuantity pq in cargo)
            {
                // A mixed load splits the trip's cost by how much of it each product occupies.
                double share = pq.Quantity.Value / totalQuantity;
                ProductHaulStats stats = GetOrAdd(BucketFor(truck, pq.Product), pq.Product);
                stats.Fuel += fuel * share;
                stats.Maintenance += maintenance * share;
                stats.Distance += distance * share;
            }
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
