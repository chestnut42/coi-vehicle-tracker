using System.Collections.Generic;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Maintenance;
using Mafi.Core.Products;
using Mafi.Core.Simulation;
using Mafi.Core.Vehicles.Trucks;

namespace VehicleRouteStats;

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

    private sealed class TruckState
    {
        public Fix64 LastOdometer;
        public readonly Dict<ProductProto, double> LastCargo = new();
    }

    private readonly IEntitiesManager m_entities;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly Lyst<ProductQuantity> m_cargoBuffer = new(4);
    private readonly Dict<int, TruckState> m_truckStates = new();
    private readonly Dict<ProductProto, ProductHaulStats>[] m_months =
        new Dict<ProductProto, ProductHaulStats>[MonthsTracked];

    private int m_monthIndex;
    private int m_lastStep = -1;

    /// <summary>Fuel and maintenance products observed on the fleet, for the UI to draw icons with.</summary>
    public ProductProto FuelProduct { get; private set; }

    public ProductProto MaintenanceProduct { get; private set; }

    public HaulStatsTracker(IEntitiesManager entities, ISimLoopEvents simLoopEvents)
    {
        m_entities = entities;
        m_simLoopEvents = simLoopEvents;
        for (int i = 0; i < m_months.Length; i++)
        {
            m_months[i] = new Dict<ProductProto, ProductHaulStats>();
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
        m_months[m_monthIndex].Clear();
    }

    public void OnEntityRemoved(IEntity entity) => m_truckStates.Remove(entity.Id.Value);

    public void Reset()
    {
        foreach (var month in m_months)
        {
            month.Clear();
        }

        m_truckStates.Clear();
        m_monthIndex = 0;
    }

    /// <summary>
    /// Totals over the last <paramref name="months"/> buckets, most recent (partial) month included.
    /// </summary>
    public IEnumerable<KeyValuePair<ProductProto, ProductHaulStats>> Snapshot(int months)
    {
        var totals = new Dict<ProductProto, ProductHaulStats>();
        int count = months < MonthsTracked ? months : MonthsTracked;

        for (int i = 0; i < count; i++)
        {
            int index = ((m_monthIndex - i) % MonthsTracked + MonthsTracked) % MonthsTracked;
            foreach (var entry in m_months[index])
            {
                if (!totals.TryGetValue(entry.Key, out ProductHaulStats sum))
                {
                    sum = new ProductHaulStats();
                    totals.Add(entry.Key, sum);
                }

                sum.Add(entry.Value);
            }
        }

        return totals;
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
            : costs.MaintenancePerMonth.Value.ToDouble() / Calendar.TICKS_PER_MONTH;

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

        Dict<ProductProto, ProductHaulStats> bucket = m_months[m_monthIndex];

        if (totalQuantity > 0)
        {
            foreach (ProductQuantity pq in cargo)
            {
                // A mixed load splits the trip's cost by how much of it each product occupies.
                double share = pq.Quantity.Value / totalQuantity;
                ProductHaulStats stats = GetOrAdd(bucket, pq.Product);
                stats.Fuel += fuel * share;
                stats.Maintenance += maintenance * share;
                stats.Distance += distance * share;
            }
        }

        RecordDeliveries(cargo, state, bucket);
    }

    private void RecordDeliveries(
        Lyst<ProductQuantity> cargo,
        TruckState state,
        Dict<ProductProto, ProductHaulStats> bucket)
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
                ProductHaulStats stats = GetOrAdd(bucket, product);
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
