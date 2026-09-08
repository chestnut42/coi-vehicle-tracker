using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Entities;
using Mafi.Core.Products;
using Mafi.Core.Vehicles.Trucks;

namespace VehicleRouteStats;

public sealed class ProductHaulStats
{
    public long ActiveTicks;
    public int Deliveries;
    public Fix32 TotalDelivered;
}

/// <summary>
/// Samples truck cargo every sim tick and turns it into a per-product vehicle-hours total,
/// instead of the vanilla monitor's plain "quantity + route count" (which can't tell a
/// short high-volume route from a long low-volume one apart).
/// </summary>
public sealed class HaulStatsTracker
{
    private readonly IEntitiesManager m_entities;
    private readonly Dict<int, Dict<ProductProto, Fix32>> m_lastCargoByTruck = new();
    private readonly Dict<ProductProto, ProductHaulStats> m_stats = new();

    private long m_totalTicks;
    private int m_daysElapsed;

    public HaulStatsTracker(IEntitiesManager entities)
    {
        m_entities = entities;
    }

    public void OnSimTick()
    {
        m_totalTicks++;

        foreach (Truck truck in m_entities.GetAllEntitiesOfType<Truck>())
        {
            if (truck.IsDestroyed)
            {
                continue;
            }

            SampleTruck(truck);
        }
    }

    public void OnNewDay()
    {
        m_daysElapsed++;
    }

    public void OnEntityRemoved(IEntity entity)
    {
        m_lastCargoByTruck.Remove(entity.Id.Value);
    }

    public void Reset()
    {
        m_stats.Clear();
        m_lastCargoByTruck.Clear();
        m_totalTicks = 0;
        m_daysElapsed = 0;
    }

    public IEnumerable<(ProductProto Product, ProductHaulStats Stats)> Snapshot() =>
        m_stats.Select(entry => (entry.Key, entry.Value));

    public double VehicleHoursFor(ProductProto product) =>
        m_stats.TryGetValue(product, out ProductHaulStats stats) ? stats.ActiveTicks / TicksPerHour : 0d;

    // Calibrated from observed ticks-per-day instead of assuming a fixed tick rate, so it
    // stays correct regardless of game speed or version-specific simulation frequency.
    private double TicksPerHour =>
        m_daysElapsed > 0 ? m_totalTicks / (double)m_daysElapsed / 24d : FallbackTicksPerHour;

    private const double FallbackTicksPerHour = 3600d;

    private void SampleTruck(Truck truck)
    {
        var cargo = new Lyst<ProductQuantity>(4);
        truck.Cargo.GetCargoProducts(cargo);

        if (!m_lastCargoByTruck.TryGetValue(truck.Id.Value, out Dict<ProductProto, Fix32> previous))
        {
            previous = new Dict<ProductProto, Fix32>();
            m_lastCargoByTruck.Add(truck.Id.Value, previous);
        }

        var seen = new HashSet<ProductProto>();
        foreach (ProductQuantity productQuantity in cargo)
        {
            seen.Add(productQuantity.Product);
            GetOrAddStats(productQuantity.Product).ActiveTicks++;
            previous[productQuantity.Product] = productQuantity.Quantity.Value;
        }

        List<ProductProto> delivered = null;
        foreach (var entry in previous)
        {
            if (!seen.Contains(entry.Key))
            {
                delivered ??= new List<ProductProto>();
                delivered.Add(entry.Key);
            }
        }

        if (delivered == null)
        {
            return;
        }

        foreach (ProductProto product in delivered)
        {
            ProductHaulStats stats = GetOrAddStats(product);
            stats.Deliveries++;
            stats.TotalDelivered += previous[product];
            previous.Remove(product);
        }
    }

    private ProductHaulStats GetOrAddStats(ProductProto product)
    {
        if (!m_stats.TryGetValue(product, out ProductHaulStats stats))
        {
            stats = new ProductHaulStats();
            m_stats.Add(product, stats);
        }

        return stats;
    }
}
