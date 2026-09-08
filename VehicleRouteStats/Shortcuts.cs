using Mafi.Unity.InputControl;
using UnityEngine;

namespace VehicleRouteStats;

public sealed class Shortcuts
{
    public static Shortcuts Instance { get; } = new();

    [Kb(KbCategory.Tools, "vehicleRouteStats_toggleWindow", "Vehicle Route Stats", "Toggle the vehicle route stats window", true, false)]
    public KeyBindings ToggleWindow { get; set; } = KeyBindings.FromKey(KbCategory.Tools, ShortcutMode.Game, KeyCode.F7);
}
