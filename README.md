# coi-vehicle-tracker

A [Captain of Industry](https://captain-of-industry.com) mod that adds a **Vehicle Route Stats**
window: a per-resource breakdown of total **vehicle-hours**, not just quantity moved and route count.

## Why

The vanilla vehicle monitor shows quantity moved and route count per resource. A resource hauled
in bulk over a short distance can look identical in "importance" to one hauled in small amounts
over a long haul, even though the second one is quietly eating far more truck-time (and therefore
fuel and maintenance). Vehicle-hours makes that cost visible.

## What it does

- Tracks every truck's cargo each simulation tick and accumulates **vehicle-hours per resource**
  (total simulated time trucks spent carrying that resource).
- Also tracks delivery count and total quantity delivered per resource, computed from cargo
  transitions rather than duplicating whatever internal counters the vanilla monitor uses.
- Adds a window (default shortcut `F7`) listing resources sorted by vehicle-hours, descending.
- Stats are session-only (reset on load) and self-calibrate the tick-to-hour conversion from the
  observed in-game day length, so they don't depend on a hardcoded tick rate.

Scope: trucks only for v1. Ships and trains expose cargo differently in the game's API and are
candidates for a follow-up.

## Project layout

```
VehicleRouteStats/
  manifest.json              mod metadata
  VehicleRouteStats.csproj    references $(COI_ROOT) game assemblies
  VehicleRouteStatsMod.cs      IMod entry point, wires the tracker to sim/calendar events
  HaulStatsTracker.cs          per-resource vehicle-hours / delivery tracking
  Shortcuts.cs                 F7 keybinding
  Ui/HaulStatsWindow.cs        the stats window
```

## Building

1. Own Captain of Industry on Steam, and install the [.NET SDK](https://dotnet.microsoft.com/download)
   (8.0 or newer). Visual Studio is optional — the project pulls in the .NET Framework 4.8
   reference assemblies from NuGet, so the SDK alone can build it.
2. A stock Steam install is found automatically. For a non-standard location, set a `COI_ROOT`
   environment variable pointing at the game's install directory.
3. `dotnet build VehicleRouteStats\VehicleRouteStats.csproj -c Release`. It deploys to
   `%APPDATA%\Captain of Industry\Mods\VehicleRouteStats` automatically.
4. Launch the game, enable the mod for a new (or existing) game, press `F7` in-game.

## Notes on how this was built

This game's mod support has no public API reference for UI/gameplay hooks beyond the
[official content-registration example](https://github.com/MaFi-Games/Captain-of-industry-modding).
The class and member names this mod relies on (`Truck.Cargo`, `IEntitiesManager`,
`ISimLoopEvents.UpdateAfterCmdProc`, `Calendar.NewDay`, the `Window`/`WindowController` UI
framework, `[GlobalDependency]` DI registration, etc.) were cross-checked against usage in several
independent open-source community mods rather than guessed from scratch. The code itself is
original, not copied.

The first draft was written without access to the actual game assemblies, and three namespace
guesses were wrong: `IConfig` is in `Mafi.Core.Game`, `ProductQuantity` in `Mafi.Core`, and
`Window`/`WindowController<>` live in `Mafi.Unity.UiToolkit.Library` and `Mafi.Unity.InputControl`
respectively (not a single `UiToolkit` namespace). `WindowController<T>` also requires a
`CreateWindow()` override. Those are fixed and it now compiles clean.

It has been compiled but not yet run in-game. If it loads but misbehaves, check
`%APPDATA%/Captain of Industry/Logs`.
