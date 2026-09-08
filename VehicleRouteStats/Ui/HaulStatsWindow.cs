using System.Linq;
using Mafi;
using Mafi.Localization;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using VehicleRouteStats;

namespace VehicleRouteStats.Ui;

[GlobalDependency(RegistrationMode.AsEverything)]
public sealed class HaulStatsWindow : Window
{
    private readonly Column m_rows;
    private HaulStatsTracker m_tracker;

    public HaulStatsWindow() : base(new LocStrFormatted("Vehicle Route Stats"), true)
    {
        WindowSize(680.px(), Px.Auto);
        MakeMovable();
        EnablePinning();

        var header = new Row(10.px()).AlignItemsCenter();
        header.Add(new Label("Resource".AsLoc()).Width(200.px()).FontBold());
        header.Add(new Label("Vehicle-hours".AsLoc()).Width(120.px()).FontBold());
        header.Add(new Label("Deliveries".AsLoc()).Width(100.px()).FontBold());
        header.Add(new Label("Total moved".AsLoc()).Width(120.px()).FontBold());

        m_rows = new Column(2.px());
        m_rows.AlignItemsStretch();

        var resetButton = new ButtonText(Button.General, "Reset stats".AsLoc(), () => m_tracker?.Reset());

        var scroll = new ScrollColumn();
        scroll.AlignSelfStretch().FlexGrow(1);
        scroll.Add(header);
        scroll.Add(m_rows);

        var body = new Column(6.px());
        body.Add(resetButton);
        body.Add(scroll);
        Body.Add(body);

        RootElement.schedule.Execute(Refresh).Every(1000L);
    }

    public void SetTracker(HaulStatsTracker tracker) => m_tracker = tracker;

    private void Refresh()
    {
        m_rows.Clear();

        if (m_tracker == null || !IsOpen)
        {
            return;
        }

        foreach (var entry in m_tracker.Snapshot().OrderByDescending(e => e.Stats.ActiveTicks))
        {
            var row = new Row(10.px()).AlignItemsCenter();

            var resourceCell = new Row().Width(200.px()).AlignItemsCenter();
            resourceCell.Add(new Icon(entry.Product).Size(ProductQuantityUi.ICON_HEIGHT).Tooltip(entry.Product.Strings.Name));
            row.Add(resourceCell);

            row.Add(new Label(m_tracker.VehicleHoursFor(entry.Product).ToString("F1").ToDoLoc()).Width(120.px()));
            row.Add(new Label(entry.Stats.Deliveries.ToString().ToDoLoc()).Width(100.px()));
            row.Add(new Label(entry.Stats.TotalDelivered.ToString().ToDoLoc()).Width(120.px()));

            m_rows.Add(row);
        }
    }

    [GlobalDependency(RegistrationMode.AsEverything)]
    public sealed class Controller : WindowController<HaulStatsWindow>
    {
        public Controller(ControllerContext controllerContext) : base(controllerContext)
        {
            controllerContext.UiRoot.AddDependency(this);
            controllerContext.InputManager.RegisterGlobalShortcut(_ => Shortcuts.Instance.ToggleWindow, this);
        }

        public void SetTracker(HaulStatsTracker tracker) => Window.SetTracker(tracker);
    }
}
