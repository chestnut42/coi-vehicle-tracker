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
    // Config params are declared in config.json; the game persists their values into the save, which
    // is what lets the stats survive a reload without the mod owning any save format of its own.
    private const string StateParam = "stats_state";
    private const int StateMaxLength = 190_000;

    private readonly ModManifest m_manifest;

    private HaulStatsTracker m_tracker;
    private IEntitiesManager m_entities;
    private ISimLoopEvents m_simLoopEvents;
    private Calendar m_calendar;
    private ProtosDb m_protosDb;

    public VehicleRouteStatsMod(ModManifest manifest)
    {
        m_manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
    }

    public string Name => "VehicleRouteStats";
    public int Version => 1;
    public bool IsUiOnly => false;
    public Option<IConfig> ModConfig { get; }
    public ModManifest Manifest => m_manifest;

    // Must be one stable instance: GameBuilder calls ReplaceWith on it to hand over the merged config.
    public ModJsonConfig JsonConfig { get; }

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
        m_protosDb = resolver.Resolve<ProtosDb>();

        m_tracker = new HaulStatsTracker(m_entities, m_simLoopEvents);
        m_tracker.RestoreState(JsonConfig.GetString(StateParam, string.Empty), m_protosDb);

        m_simLoopEvents.UpdateAfterCmdProc.AddNonSaveable(this, OnSimUpdate);
        m_simLoopEvents.BeforeSave.AddNonSaveable(this, OnBeforeSave);
        m_calendar.NewMonth.AddNonSaveable(this, m_tracker.OnNewMonth);
        m_entities.EntityRemoved.AddNonSaveable(this, m_tracker.OnEntityRemoved);

        resolver.Resolve<HaulStatsWindow.Controller>().SetTracker(m_tracker);
    }

    private void OnBeforeSave()
    {
        string state = m_tracker.SerializeState(StateMaxLength);
        if (!JsonConfig.TrySetValue(StateParam, state, out string error))
        {
            Log.Warning($"VehicleRouteStats could not persist its stats: {error}");
        }
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues)
    {
    }

    public void Dispose()
    {
        m_simLoopEvents?.UpdateAfterCmdProc.RemoveNonSaveable(this, OnSimUpdate);
        m_simLoopEvents?.BeforeSave.RemoveNonSaveable(this, OnBeforeSave);
        if (m_tracker != null)
        {
            m_calendar?.NewMonth.RemoveNonSaveable(this, m_tracker.OnNewMonth);
            m_entities?.EntityRemoved.RemoveNonSaveable(this, m_tracker.OnEntityRemoved);
        }
    }

    private void OnSimUpdate() => m_tracker.OnSimTick();
}
