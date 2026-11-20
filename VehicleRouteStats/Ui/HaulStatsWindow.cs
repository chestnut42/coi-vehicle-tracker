using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mafi;
using Mafi.Core;
using Mafi.Core.Products;
using Mafi.Core.Vehicles;
using Mafi.Localization;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using VehicleRouteStats;

namespace VehicleRouteStats.Ui;

public sealed class HaulStatsWindow : Window
{
    private static readonly int[] RangeMonths = { 1, 3, 6, 12 };

    private const int GridColumns = 4;
    private const int CardWidth = 150;
    private const int CardGap = 6;

    private readonly Grid m_grid;
    private readonly Row m_totals;
    private readonly Column m_groupFilters;
    private HaulStatsTracker m_tracker;
    private bool[] m_includeSlot = EmptySlots;
    private int m_months = 12;
    private bool m_includeGeneral = true;
    private bool m_includeMining = true;
    private bool m_includeRefueling = true;

    // Stands in until a tracker arrives; Snapshot indexes it, so it must never be null.
    private static readonly bool[] EmptySlots = new bool[0];

    public HaulStatsWindow() : base(new LocStrFormatted("Vehicle Route Stats"), true)
    {
        WindowSize((GridColumns * (CardWidth + CardGap) + 30).px(), Px.Auto);
        MakeMovable();
        EnablePinning();

        var rangeDropdown = DropdownFor.LocStr();
        rangeDropdown.SetOptions(RangeMonths.Select(RangeLabel).ToArray());
        rangeDropdown.SetValueIndex(RangeMonths.Length - 1, false);
        rangeDropdown.OnValueChanged((_, index) =>
        {
            m_months = RangeMonths[index];
            Refresh();
        });

        var resetButton = new ButtonText("Reset stats".AsLoc(), () =>
        {
            m_tracker?.Reset();
            Refresh();
        });

        // Vanilla stacks the three category toggles in a column; a row of them overflows the window.
        var categories = new Column(1.pt());
        categories.Add(FilterToggle(
            Tr.TrucksStats__OptionGeneral,
            Tr.TrucksStats__OptionGeneralTooltip,
            () => m_includeGeneral,
            isOn => m_includeGeneral = isOn));
        categories.Add(FilterToggle(
            Tr.TrucksStats__OptionMining,
            Tr.TrucksStats__OptionMiningTooltip,
            () => m_includeMining,
            isOn => m_includeMining = isOn));
        categories.Add(FilterToggle(
            Tr.TrucksStats__OptionRefueling,
            Tr.TrucksStats__OptionRefuelingTooltip,
            () => m_includeRefueling,
            isOn => m_includeRefueling = isOn));

        // Filled once a tracker is attached: the groups are prototypes, so the set is not known until
        // the game's protos are loaded.
        m_groupFilters = new Column(1.pt());

        // Two rows rather than one: a second column of toggles beside the first would overflow a
        // window sized to the card grid, and height is Px.Auto so growing downwards is free.
        var topRow = new Row(10.px()).AlignItemsCenter();
        topRow.Add(rangeDropdown);
        topRow.Add(resetButton);

        var filterRow = new Row(16.px());
        filterRow.Add(categories);
        filterRow.Add(m_groupFilters);

        var toolbar = new Column(6.px());
        toolbar.Add(topRow);
        toolbar.Add(filterRow);

        m_totals = new Row(4.px()).AlignItemsCenter();

        m_grid = new Grid(GridColumns, CardGap.px(), CardGap.px());

        var scroll = new ScrollColumn();
        scroll.AlignSelfStretch().FlexGrow(1);
        scroll.Add(m_grid.Component);

        var body = new Column(6.px());
        body.Add(toolbar);
        body.Add(m_totals);
        body.Add(scroll);
        Body.Add(body);

        RootElement.schedule.Execute(Refresh).Every(1000L);
    }

    public void SetTracker(HaulStatsTracker tracker)
    {
        m_tracker = tracker;
        BuildGroupFilters();
        Refresh();
    }

    /// <summary>
    /// One toggle per registered vehicle group, so a mod that adds vehicles gets a filter without
    /// this window knowing about it. The catch-all slot only earns a toggle once something lands in
    /// it, which normally never happens.
    /// </summary>
    private void BuildGroupFilters()
    {
        m_groupFilters.Clear();
        if (m_tracker == null)
        {
            m_includeSlot = EmptySlots;
            return;
        }

        m_includeSlot = new bool[m_tracker.SlotCount];
        for (int i = 0; i < m_includeSlot.Length; i++)
        {
            m_includeSlot[i] = true;
        }

        IReadOnlyList<VehicleGroupProto> groups = m_tracker.Groups;
        for (int i = 0; i < groups.Count; i++)
        {
            int slot = i;
            m_groupFilters.Add(SlotToggle(groups[i].Strings.Name, slot));
        }

        if (m_tracker.HasAnyDataIn(m_tracker.UnknownSlot))
        {
            m_groupFilters.Add(SlotToggle(
                new LocStrFormatted("Other"),
                m_tracker.UnknownSlot));
        }
    }

    private Toggle SlotToggle(LocStrFormatted label, int slot) =>
        FilterToggle(label, label, () => m_includeSlot[slot], isOn => m_includeSlot[slot] = isOn);

    private Toggle FilterToggle(LocStrFormatted label, LocStrFormatted tooltip, Func<bool> get, Action<bool> set) =>
        new Toggle(true)
            .Label(label)
            .Tooltip(tooltip)
            .Value(get())
            .ObserveValue(get)
            .OnValueChanged(isOn =>
            {
                set(isOn);
                Refresh();
            });

    private static LocStrFormatted RangeLabel(int months) =>
        (months == 1 ? "Last 1 month" : $"Last {months} months").AsLoc();

    private void Refresh()
    {
        if (m_tracker == null || !IsOpen)
        {
            return;
        }

        m_grid.Clear();
        m_totals.Clear();

        IEnumerable<KeyValuePair<ProductProto, ProductHaulStats>> ordered =
            m_tracker.Snapshot(m_months, m_includeGeneral, m_includeMining, m_includeRefueling, m_includeSlot)
                .OrderByDescending(e => e.Value.Fuel + e.Value.PickupFuel);

        double totalFuel = 0;
        double totalMaintenance = 0;

        foreach (var entry in ordered)
        {
            // Summed here rather than from the tracker so the header can only ever show what is drawn.
            ProductHaulStats stats = entry.Value;
            totalFuel += stats.Fuel + stats.PickupFuel;
            totalMaintenance += stats.Maintenance + stats.PickupMaintenance;
            m_grid.Add(BuildCard(entry.Key, stats));
        }

        m_totals.Add(new Label("Total".AsLoc()).FontBold());
        m_totals.Add(Cell(m_tracker.FuelProduct, "Fuel", Compact(totalFuel)));
        m_totals.Add(Cell(m_tracker.MaintenanceProduct, "Maint", Compact(totalMaintenance)));

        List<(string Job, double Fuel, double Maintenance)> unattributed =
            m_tracker.UnattributedBreakdown(m_months, m_includeSlot);

        double unattributedFuel = 0;
        double unattributedMaintenance = 0;
        foreach (var bucket in unattributed)
        {
            unattributedFuel += bucket.Fuel;
            unattributedMaintenance += bucket.Maintenance;
        }

        m_totals.Add(new Label("Unattributed".AsLoc()).FontBold());
        m_totals.Add(Cell(m_tracker.FuelProduct, "Fuel", Compact(unattributedFuel)));
        m_totals.Add(Cell(m_tracker.MaintenanceProduct, "Maint", Compact(unattributedMaintenance)));

        var breakdown = new StringBuilder();
        breakdown.Append($"Total, carrying plus collecting\nFuel: {totalFuel:F1}\nMaintenance: {totalMaintenance:F1}\n\n");
        breakdown.Append($"Unattributed — burn with no cargo and not moving,\nsuch as queue idling or refuelling.\n");
        breakdown.Append($"Fuel: {unattributedFuel:F1}\nMaintenance: {unattributedMaintenance:F1}\n");
        foreach (var bucket in unattributed)
        {
            breakdown.Append($"  {bucket.Job}: {bucket.Fuel:F1} / {bucket.Maintenance:F1}\n");
        }

        m_totals.Tooltip(breakdown.ToString().ToDoLoc());
    }

    private UiComponent BuildCard(ProductProto product, ProductHaulStats stats)
    {
        var title = new Row(4.px()).AlignItemsCenter();
        title.Add(new Icon(product).Small().Tooltip(product.Strings.Name));
        title.Add(new Label(product.Strings.Name)
            .FontSize(11)
            .TextOverflow(TextOverflow.Ellipsis));

        // Top row is the laden haul, bottom row is the empty run out to collect it.
        var laden = new Row(4.px()).AlignItemsCenter();
        laden.Add(Cell(m_tracker.FuelProduct, "Fuel", Compact(stats.Fuel)));
        laden.Add(Cell(m_tracker.MaintenanceProduct, "Maint", Compact(stats.Maintenance)));

        var pickup = new Row(4.px()).AlignItemsCenter();
        pickup.Add(Cell(m_tracker.FuelProduct, "Fuel", Compact(stats.PickupFuel)));
        pickup.Add(Cell(m_tracker.MaintenanceProduct, "Maint", Compact(stats.PickupMaintenance)));

        var card = new Panel(true);
        card.Width(CardWidth.px());
        card.Body.Add(title);
        card.Body.Add(laden);
        card.Body.Add(pickup);

        // The card shows rounded figures, so the tooltip carries the exact ones plus what didn't fit.
        card.Tooltip(
            ($"{product.Strings.Name}\n" +
             $"Carrying — fuel {stats.Fuel:F1}, maintenance {stats.Maintenance:F1}, distance {stats.Distance:F0}\n" +
             $"Collecting — fuel {stats.PickupFuel:F1}, maintenance {stats.PickupMaintenance:F1}, distance {stats.PickupDistance:F0}\n" +
             $"Deliveries: {stats.Deliveries}\n" +
             $"Delivered: {stats.Delivered:F0}").ToDoLoc());

        return card;
    }

    // Tiles don't shrink, so a year's worth of fuel has to stay short or it overflows the card.
    private static string Compact(double value) =>
        value >= 1_000_000 ? (value / 1_000_000).ToString("0.#") + "M"
        : value >= 1_000 ? (value / 1_000).ToString("0.#") + "k"
        : value.ToString("0.#");

    /// <summary>The vanilla inspector's compact icon-plus-value tile; it sizes to its content.</summary>
    private static UiComponent Cell(ProductProto icon, string fallbackCaption, string value)
    {
        var tile = new ReadonlyTile(null, "".AsLoc());
        tile.SmallIcon();

        if (icon != null)
        {
            tile.Icon(icon, false);
            tile.Value(value.ToDoLoc());
        }
        else
        {
            tile.Value($"{fallbackCaption} {value}".ToDoLoc());
        }

        tile.SetTextOverflow(TextOverflow.Clip);
        return tile;
    }

    [GlobalDependency(RegistrationMode.AsEverything)]
    public sealed class Controller : WindowController<HaulStatsWindow>
    {
        private HaulStatsTracker m_tracker;

        public Controller(ControllerContext controllerContext) : base(controllerContext)
        {
            controllerContext.UiRoot.AddDependency(this);
            controllerContext.InputManager.RegisterGlobalShortcut(Shortcuts.Instance.ToggleWindow, this);
        }

        protected override HaulStatsWindow CreateWindow()
        {
            var window = new HaulStatsWindow();
            window.SetTracker(m_tracker);
            return window;
        }

        public void SetTracker(HaulStatsTracker tracker)
        {
            m_tracker = tracker;
            if (HasWindow)
            {
                Window.SetTracker(tracker);
            }
        }
    }
}
