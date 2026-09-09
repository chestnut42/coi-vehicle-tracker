using System;
using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Core;
using Mafi.Core.Products;
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
    private HaulStatsTracker m_tracker;
    private int m_months = 12;
    private bool m_includeGeneral = true;
    private bool m_includeMining = true;
    private bool m_includeRefueling = true;

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
        categories.Add(CategoryToggle(
            Tr.TrucksStats__OptionGeneral,
            Tr.TrucksStats__OptionGeneralTooltip,
            () => m_includeGeneral,
            isOn => m_includeGeneral = isOn));
        categories.Add(CategoryToggle(
            Tr.TrucksStats__OptionMining,
            Tr.TrucksStats__OptionMiningTooltip,
            () => m_includeMining,
            isOn => m_includeMining = isOn));
        categories.Add(CategoryToggle(
            Tr.TrucksStats__OptionRefueling,
            Tr.TrucksStats__OptionRefuelingTooltip,
            () => m_includeRefueling,
            isOn => m_includeRefueling = isOn));

        var toolbar = new Row(10.px()).AlignItemsCenter();
        toolbar.Add(rangeDropdown);
        toolbar.Add(categories);
        toolbar.Add(resetButton);

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

    public void SetTracker(HaulStatsTracker tracker) => m_tracker = tracker;

    private Toggle CategoryToggle(LocStr label, LocStr tooltip, Func<bool> get, Action<bool> set) =>
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
            m_tracker.Snapshot(m_months, m_includeGeneral, m_includeMining, m_includeRefueling)
                .OrderByDescending(e => e.Value.Fuel);

        double totalFuel = 0;
        double totalMaintenance = 0;

        foreach (var entry in ordered)
        {
            // Summed here rather than from the tracker so the header can only ever show what is drawn.
            totalFuel += entry.Value.Fuel;
            totalMaintenance += entry.Value.Maintenance;
            m_grid.Add(BuildCard(entry.Key, entry.Value));
        }

        m_totals.Add(new Label("Total".AsLoc()).FontBold());
        m_totals.Add(Cell(m_tracker.FuelProduct, "Fuel", Compact(totalFuel)));
        m_totals.Add(Cell(m_tracker.MaintenanceProduct, "Maint", Compact(totalMaintenance)));
        m_totals.Tooltip(
            ($"Total across the resources shown\n" +
             $"Fuel: {totalFuel:F1}\n" +
             $"Maintenance: {totalMaintenance:F1}").ToDoLoc());
    }

    private UiComponent BuildCard(ProductProto product, ProductHaulStats stats)
    {
        var title = new Row(4.px()).AlignItemsCenter();
        title.Add(new Icon(product).Small().Tooltip(product.Strings.Name));
        title.Add(new Label(product.Strings.Name)
            .FontSize(11)
            .TextOverflow(TextOverflow.Ellipsis));

        var costs = new Row(4.px()).AlignItemsCenter();
        costs.Add(Cell(m_tracker.FuelProduct, "Fuel", Compact(stats.Fuel)));
        costs.Add(Cell(m_tracker.MaintenanceProduct, "Maint", Compact(stats.Maintenance)));

        var card = new Panel(true);
        card.Width(CardWidth.px());
        card.Body.Add(title);
        card.Body.Add(costs);

        // The card shows rounded figures, so the tooltip carries the exact ones plus what didn't fit.
        card.Tooltip(
            ($"{product.Strings.Name}\n" +
             $"Fuel: {stats.Fuel:F1}\n" +
             $"Maintenance: {stats.Maintenance:F1}\n" +
             $"Distance: {stats.Distance:F0}\n" +
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
            controllerContext.InputManager.RegisterGlobalShortcut(_ => Shortcuts.Instance.ToggleWindow, this);
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
