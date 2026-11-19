using Mafi.Unity.InputControl;
using UnityEngine;

namespace VehicleRouteStats;

public sealed class Shortcuts
{
    public const string ModId = "VehicleRouteStats";

    public static Shortcuts Instance { get; } = new();

    // The id must start with "<modId>." or ShortcutsRegistrationSession.Add rejects it, and Build()
    // silently skips registering the label and tooltip if it does not parse.
    //
    // No SetGroup: KbHandle's constructor falls back to KbGroup.DefaultOf(ownerId), which gives the
    // mod its own section in the controls settings. KbGroup.DefaultOf is not public to call directly.
    public KbHandle ToggleWindow { get; } = KbHandleBuilder
        .Start(ModId + ".ToggleWindow", "Vehicle Route Stats")
        .SetPrimary(KeyCode.F8)
        .SetMode(ShortcutMode.Game)
        .SetTooltip("Toggle the vehicle route stats window")
        .Build();
}
