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

1. Own Captain of Industry on Steam, .NET Framework 4.8 installed.
2. Set an environment variable `COI_ROOT` pointing at the game's install directory.
3. Build `VehicleRouteStats.csproj` in Release. It deploys to
   `%APPDATA%/Captain of Industry/Mods/VehicleRouteStats` automatically.
4. Launch the game, enable the mod for a new (or existing) game, press `F7` in-game.

## Notes on how this was built

This game's mod support has no public API reference for UI/gameplay hooks beyond the
[official content-registration example](https://github.com/MaFi-Games/Captain-of-industry-modding).
The class and member names this mod relies on (`Truck.Cargo`, `IEntitiesManager`,
`ISimLoopEvents.UpdateAfterCmdProc`, `Calendar.NewDay`, the `Window`/`WindowController` UI
framework, `[GlobalDependency]` DI registration, etc.) were cross-checked against usage in several
independent open-source community mods rather than guessed from scratch. The code itself is
original, not copied.

This was written without access to the actual game or its assemblies, so it has not been
compiled or run. If a member name has drifted in your installed game version, the build error
will point at exactly which one — check `%APPDATA%/Captain of Industry/Logs` if it loads but
misbehaves.
