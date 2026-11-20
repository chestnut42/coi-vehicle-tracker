# coi-vehicle-tracker

A [Captain of Industry](https://captain-of-industry.com) mod that adds a **Vehicle Route Stats**
window: a per-resource breakdown of the **fuel, maintenance and distance** your truck fleet spends
hauling each resource, not just quantity moved and route count.

Requires **Captain of Industry 0.8.8** or newer.

## Installing

1. Download `VehicleRouteStats-<version>.zip` from the
   [latest release](https://github.com/chestnut42/coi-vehicle-tracker/releases/latest).
2. Extract it into `%APPDATA%\Captain of Industry\Mods\` so that you end up with
   `…\Mods\VehicleRouteStats\VehicleRouteStats.dll`.
3. Start the game and enable **Vehicle Route Stats** in the mod list.
4. Press `F8` in game to open the window. The shortcut is rebindable under its own heading in
   Controls.

Safe to add to an existing save, and safe to remove again. The keybinding system was reworked in
0.8.8, so this build will not load on older versions.

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
- Adds a window (default shortcut `F8`) showing one compact card per resource in a 4-column grid,
  sorted by fuel burned descending, with a dropdown to scope to the last 1, 3, 6 or 12 months and
  two independent columns of filters: one for general / mining / refuelling hauls, one for vehicle
  class. The class filters come from the game's own `VehicleGroupProto` — pickup trucks, trucks,
  haul trucks and amphibious trucks in the base game — and are enumerated from the protos db rather
  than hardcoded, so a mod that registers its own vehicle group gets a tickbox for free. A group
  already aggregates its tier's fuel variants (diesel and hydrogen) and attachment variants (dump,
  tank, flatbed), which is exactly the granularity the filter wants. Groups are ordered by the
  smallest truck each one holds, so pickups come before trucks before haul trucks without relying
  on the base game's ids.
  The two filter dimensions are genuinely independent, which means the buckets have to be stored as
  the cross product of (category, group, month, product): per-category and per-group totals cannot
  be intersected after the fact.
  The job toggles use the same categories,
  localized labels and tooltips as the vanilla truck stats tab: a truck hauling loose product for a
  `MineTower`, or anything for a `TreeHarvester`, counts as mining; a truck running a
  `RefuelOtherVehicleJob` counts as refuelling; everything else is general. That split is taken from
  `VehicleJobStatsManager.RecordJobStatsFor`.
  Each card shows two rows of fuel and maintenance against the actual product icons: the top row is
  the cost of **carrying** that resource, the bottom row the cost of the empty run out to
  **collect** it. Distance, delivery count and quantity delivered sit in the card's tooltip. A
  totals row above the grid sums whatever is currently shown, with an **Unattributed** figure beside
  it for burn that cannot be pinned to a resource.
- Nothing accrues while the game is paused: sampling is skipped unless the simulation step has
  actually advanced (`ISimLoopEvents.CurrentStep`) and the sim is not paused.
- Stats are bucketed by calendar month via `Calendar.NewMonth` and **persist across sessions**.

### How persistence works

The game has no save-data API for mods, but it does serialize each mod's `ModJsonConfig` into the
save file. So the stats are flattened to a compact string and stored in a `stats_state` config
parameter declared in `config.json` — written on `ISimLoopEvents.BeforeSave`, read back in
`Initialize`. This is the same approach several community mods use (see
[CoI_AutoHelpers](https://github.com/Kayser1444/CoI_AutoHelpers)).

The upshot is that the data is per-save automatically, and travels with a save that is copied or
renamed, without this mod defining any save format of its own. Some deliberate choices:

- One record per (category, group, month, product); months are stored as an offset back from the
  current one, so the ring buffer's rotation does not have to survive the round trip, and the group
  is stored by proto id, so the slot ordering does not either. A group id that no longer resolves —
  another mod removed, say — lands in a catch-all slot instead of being folded into a live group's
  numbers.
- Numbers are written with `CultureInfo.InvariantCulture` — a locale that formats decimals with a
  comma would otherwise split a record in half.
- A schema version prefix; a blob written by a different version is ignored rather than misread.
  The vehicle-class split bumped this to `v3`, and because older blobs carry no class at all there
  is nothing to migrate: upgrading to 0.3.0 starts the 12-month window over on an existing save.
- Anything unparseable is skipped, so a bad blob costs history but never blocks loading a save.
- If the data would exceed the parameter's length limit, the oldest months are dropped instead of
  failing the save. That limit is entirely self-imposed: it is the `max_length` this mod declares
  for its own parameter, and `ModJsonConfigParam.Validate` only ever compares against the declared
  value, so there is no game-side ceiling to work around. Splitting buckets by vehicle class
  multiplies the worst-case record count, so the declared limit was raised to leave headroom rather
  than let months start falling off a long-running save.

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

### What the empty running costs

A truck driving empty is not free, and it turns out to be about half the bill. Nearly every moving
truck reports its current job as `VehicleQueueJob`, which is a *wrapper*: jobs that have to travel
somewhere and queue up (`CargoPickUpJob`, `CargoDeliveryJob`, `RefuelSelfJob` and friends) build one
in their constructor, register themselves as its queue-tip job, and enqueue the wrapper. The real
job only acts once the truck reaches the head of the queue, and it is reachable from the wrapper
only through a private field. Its consumption is `IsMoving ? Full : Idle`, so it covers both driving
and waiting in line.

Rather than reflect into that private field, attribution is **deferred**:

- While a truck is **empty and moving**, its burn is held as pending against that truck.
- When the **loaded truck departs**, the pending amount is billed to what it is carrying, split by
  quantity share, and appears as the second row on the card — the *collecting* cost, separate from
  the *carrying* cost. It waits for departure rather than releasing when the first crate lands,
  because a mixed load arrives over several ticks and flushing early would hand the whole pickup
  cost to whichever product loaded first.
- While a truck is **empty and stationary** — queue idling, refuelling, waiting — the burn goes to
  an **Unattributed** figure beside the total. Sitting still is not the cost of hauling anything.

This needs no private API and is arguably more honest than reading the job's intent: a pickup can
fail, re-route, or end up collecting something else, and deferred attribution charges whatever the
truck genuinely ended up carrying.

Deliberate limitations: an empty leg spanning a month boundary is billed to the month of the pickup;
pending burn is per-truck transient state that is not persisted, so a save/load drops whatever was
in flight; and a truck's final empty leg, if it never loads again, stays unattributed.

Fuel is reported as a single figure regardless of which fuel it actually was. Every truck tier
ships in a diesel and a hydrogen variant, and both land in the same vehicle group, so a fleet
mid-transition adds diesel to hydrogen and labels the sum with whichever fuel product was observed
first. This is a deliberate trade: in a given save a fleet is usually on one fuel, and the
changeover is short. Splitting fuel by product would be the fix if that stops being true.

Still not counted at all: vehicles other than trucks. `Excavator`, `TreeHarvester`, `TreePlanter`
and `RocketTransporter` are all `Vehicle` subclasses with fuel tanks and maintenance, but only
`Truck` is sampled. Excavators in particular burn a lot and appear nowhere.

Scope: trucks only. Ships and trains expose cargo differently in the game's API and are
candidates for a follow-up.

## Project layout

```
VehicleRouteStats/
  manifest.json              mod metadata
  config.json                declares the stats_state param the save persists
  VehicleRouteStats.csproj    references $(COI_ROOT) game assemblies
  VehicleRouteStatsMod.cs      IMod entry point, wires the tracker to sim/calendar events
  HaulStatsTracker.cs          per-resource fuel / maintenance / distance, bucketed by month
  Shortcuts.cs                 F8 keybinding (KbHandleBuilder; see note below)
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
4. Launch the game, enable the mod for a new (or existing) game, press `F8` in-game.

## Keybindings

The keybinding system was reworked in the 0.8.8 era: the old `[Kb(KbCategory…)]` attribute is gone,
and `RegisterGlobalShortcut` takes a `KbHandle` instead of a `Func<ShortcutsManager, KeyBindings>`.
Three things are easy to get wrong, each failing quietly rather than loudly:

- The handle id must be `<modId>.<Name>`, or `ShortcutsRegistrationSession.Add` rejects it and
  `KbHandleBuilder.Build` skips registering the label and tooltip.
- The handle must be added to `IShortcutsRegistry` (resolve it, a `[GlobalDependency]`) **during
  `IMod.Initialize`** — the registration window closes once the game is initialized. Skip this and
  `KbHandle.IsRegistered` stays false, `ShortcutsManager.matches` short-circuits on it, and the key
  simply does nothing with no error anywhere.
- Omitting `SetGroup` is deliberate: `KbHandle`'s constructor falls back to
  `KbGroup.DefaultOf(ownerId)`, giving the mod its own section in the controls settings.
  `KbGroup.DefaultOf` is not public, so it cannot be called directly.

## Notes on how this was built

This game's mod support has no public API reference for UI/gameplay hooks beyond the
[official content-registration example](https://github.com/MaFi-Games/Captain-of-industry-modding).
The class and member names this mod relies on (`Truck.GetCargoProducts`, `IEntitiesManager`,
`ISimLoopEvents.UpdateAfterCmdProc`, `Calendar.NewMonth`, the `Window`/`WindowController` UI
framework, `[GlobalDependency]` DI registration, etc.) were cross-checked against usage in several
independent open-source community mods rather than guessed from scratch. The code itself is
original, not copied.

The first draft was written without access to the actual game assemblies, and three namespace
guesses were wrong: `IConfig` is in `Mafi.Core.Game`, `ProductQuantity` in `Mafi.Core`, and
`Window`/`WindowController<>` live in `Mafi.Unity.UiToolkit.Library` and `Mafi.Unity.InputControl`
respectively (not a single `UiToolkit` namespace). `WindowController<T>` also requires a
`CreateWindow()` override. Those are fixed and it now compiles clean.

If it loads but misbehaves, check `%APPDATA%/Captain of Industry/Logs`.

## License

[MIT](LICENSE).
