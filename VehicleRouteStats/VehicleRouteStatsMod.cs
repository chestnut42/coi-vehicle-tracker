using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;
using VehicleRouteStats.Ui;

namespace VehicleRouteStats;

public sealed class VehicleRouteStatsMod : IMod
{
    private readonly ModManifest m_manifest;

    private HaulStatsTracker m_tracker;
    private IEntitiesManager m_entities;
    private ISimLoopEvents m_simLoopEvents;
    private Calendar m_calendar;

    public VehicleRouteStatsMod(ModManifest manifest)
    {
        m_manifest = manifest;
    }

    public string Name => "VehicleRouteStats";
    public int Version => 1;
    public bool IsUiOnly => false;
    public Option<IConfig> ModConfig { get; }
    public ModManifest Manifest => m_manifest;
    public ModJsonConfig JsonConfig => new ModJsonConfig(this);

    public void ChangeConfigs(Lyst<IConfig> configs)
    {
    }

    public void RegisterPrototypes(ProtoRegistrator registrator)
    {
    }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded)
    {
    }

    public void EarlyInit(DependencyResolver resolver)
    {
    }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        m_entities = resolver.Resolve<IEntitiesManager>();
        m_simLoopEvents = resolver.Resolve<ISimLoopEvents>();
        m_calendar = resolver.Resolve<Calendar>();

        m_tracker = new HaulStatsTracker(m_entities, m_simLoopEvents);
        m_simLoopEvents.UpdateAfterCmdProc.AddNonSaveable(this, OnSimUpdate);
        m_calendar.NewMonth.AddNonSaveable(this, m_tracker.OnNewMonth);
        m_entities.EntityRemoved.AddNonSaveable(this, m_tracker.OnEntityRemoved);

        resolver.Resolve<HaulStatsWindow.Controller>().SetTracker(m_tracker);
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues)
    {
    }

    public void Dispose()
    {
        m_simLoopEvents?.UpdateAfterCmdProc.RemoveNonSaveable(this, OnSimUpdate);
        if (m_tracker != null)
        {
            m_calendar?.NewMonth.RemoveNonSaveable(this, m_tracker.OnNewMonth);
            m_entities?.EntityRemoved.RemoveNonSaveable(this, m_tracker.OnEntityRemoved);
        }
    }

    private void OnSimUpdate() => m_tracker.OnSimTick();
}
