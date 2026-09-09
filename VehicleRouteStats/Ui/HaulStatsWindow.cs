using System.Collections.Generic;
using System.Linq;
using Mafi;
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

    private const int GridColumns = 3;
    private const int CardWidth = 220;
    private const int CardGap = 6;

    private readonly Grid m_grid;
    private HaulStatsTracker m_tracker;
    private int m_months = 12;

    public HaulStatsWindow() : base(new LocStrFormatted("Vehicle Route Stats"), true)
    {
        WindowSize((GridColumns * (CardWidth + CardGap) + 40).px(), Px.Auto);
        MakeMovable();

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

        var toolbar = new Row(10.px()).AlignItemsCenter();
        toolbar.Add(rangeDropdown);
        toolbar.Add(resetButton);

        m_grid = new Grid(GridColumns, CardGap.px(), CardGap.px());

        var scroll = new ScrollColumn();
        scroll.AlignSelfStretch().FlexGrow(1);
        scroll.Add(m_grid.Component);

        var body = new Column(6.px());
        body.Add(toolbar);
        body.Add(scroll);
        Body.Add(body);

        RootElement.schedule.Execute(Refresh).Every(1000L);
    }

    public void SetTracker(HaulStatsTracker tracker) => m_tracker = tracker;

    private static LocStrFormatted RangeLabel(int months) =>
        (months == 1 ? "Last 1 month" : $"Last {months} months").AsLoc();

    private void Refresh()
    {
        if (m_tracker == null || !IsOpen)
        {
            return;
        }

        m_grid.Clear();

        IEnumerable<KeyValuePair<ProductProto, ProductHaulStats>> ordered =
            m_tracker.Snapshot(m_months).OrderByDescending(e => e.Value.Fuel);

        foreach (var entry in ordered)
        {
            m_grid.Add(BuildCard(entry.Key, entry.Value));
        }
    }

    private static UiComponent BuildCard(ProductProto product, ProductHaulStats stats)
    {
        var title = new Row(6.px()).AlignItemsCenter();
        title.Add(new Icon(product).Size(ProductQuantityUi.ICON_HEIGHT).Tooltip(product.Strings.Name));
        title.Add(new Label(product.Strings.Name).FontBold());

        var costs = new Row(8.px());
        costs.Add(Stat("Fuel", stats.Fuel.ToString("F1")));
        costs.Add(Stat("Maintenance", stats.Maintenance.ToString("F1")));

        var hauling = new Row(8.px());
        hauling.Add(Stat("Distance", stats.Distance.ToString("F0")));
        hauling.Add(Stat("Deliveries", stats.Deliveries.ToString()));

        var moved = new Row(8.px());
        moved.Add(Stat("Delivered", stats.Delivered.ToString("F0")));

        var card = new Panel(true);
        card.Width(CardWidth.px());
        card.Body.Add(title);
        card.Body.Add(costs);
        card.Body.Add(hauling);
        card.Body.Add(moved);
        return card;
    }

    private static UiComponent Stat(string caption, string value)
    {
        var cell = new Column(0.px()).Width(((CardWidth - 24) / 2).px());
        cell.Add(new Label(caption.AsLoc()).FontSize(10));
        cell.Add(new Label(value.ToDoLoc()).FontBold());
        return cell;
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
