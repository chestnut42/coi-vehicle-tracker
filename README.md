# coi-vehicle-tracker

A [Captain of Industry](https://captain-of-industry.com) mod that adds a **Vehicle Route Stats**
window: a per-resource breakdown of the **fuel, maintenance and distance** your truck fleet spends
hauling each resource, not just quantity moved and route count.

## Why

The vanilla vehicle monitor shows quantity moved and route count per resource. A resource hauled
in bulk over a short distance can look identical in "importance" to one hauled in small amounts
over a long haul, even though the second one is quietly eating far more fuel and maintenance.
This makes that cost visible, and directly in the units you pay it in.

## What it does

- Each simulation tick, attributes every truck's **fuel burn**, **maintenance drain** and
  **distance driven** to whatever it is currently carrying. A mixed load splits the tick's cost
  between its products in proportion to how much of the load each one occupies.
- Also tracks delivery count and total quantity delivered per resource, computed from cargo
  transitions rather than duplicating whatever internal counters the vanilla monitor uses.
- Adds a window (default shortcut `F7`) showing one compact card per resource in a 4-column grid,
  sorted by fuel burned descending, with a dropdown to scope to the last 1, 3, 6 or 12 months.
  Each card shows fuel and maintenance against the actual product icons; distance, delivery count
  and quantity delivered are still tracked and sit in the card's tooltip.
- Nothing accrues while the game is paused: sampling is skipped unless the simulation step has
  actually advanced (`ISimLoopEvents.CurrentStep`) and the sim is not paused.
- Stats are session-only (reset on load) and bucketed by calendar month via `Calendar.NewMonth`.

### Why fuel and not vehicle-hours

Time spent carrying is a bad proxy for cost: a loaded truck parked overnight would score the same
as one driving all night. Fuel and maintenance are read from the game's own model instead:

- Fuel comes from `IEntityWithFuelTank.GetCurrentFuelUsagePer60()`, divided by
  `Duration.OneMinute.Ticks`, and is only accrued when `IsEngineOn` — matching the engine-on check
  in `Vehicle.ConsumeFuelPerUpdate`.
- Maintenance is `MaintenanceCosts.MaintenancePerMonth` prorated over `Calendar.TICKS_PER_MONTH`,
  accrued only when the vehicle is not `IsIdleForMaintenance`.

Note that fuel burn is **not** proportional to speed. `VehicleFuelConsumption` is a three-state
enum — `None`, `Idle`, `Full` — chosen by the vehicle's current job, so a working truck burns a
flat rate regardless of how fast it happens to be moving. Distance is reported separately, from
the `LifetimeDistanceTraveled` odometer, because it is still the best measure of route length.

Known limitation: cost is only attributed while a truck is loaded, so empty return legs are not
charged to anything. Absolute numbers are therefore an undercount of a full round trip, though
resources remain comparable against each other.

Scope: trucks only for v1. Ships and trains expose cargo differently in the game's API and are
candidates for a follow-up.

## Project layout

```
VehicleRouteStats/
  manifest.json              mod metadata
  VehicleRouteStats.csproj    references $(COI_ROOT) game assemblies
  VehicleRouteStatsMod.cs      IMod entry point, wires the tracker to sim/calendar events
  HaulStatsTracker.cs          per-resource fuel / maintenance / distance, bucketed by month
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
