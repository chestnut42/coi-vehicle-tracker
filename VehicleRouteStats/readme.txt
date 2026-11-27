Vehicle Route Stats
===================

Shows the fuel and maintenance your truck fleet spends hauling each resource, so
a long thin haul stops hiding behind a short fat one.

Press F8 in game to open the window. The shortcut is rebindable under its own
heading in Controls.


Installing
----------

Extract the downloaded zip into:

    %APPDATA%\Captain of Industry\Mods\

You should end up with:

    ...\Mods\VehicleRouteStats\VehicleRouteStats.dll

Then enable "Vehicle Route Stats" in the game's mod list.

Requires Captain of Industry 0.8.8 or newer. Safe to add to an existing save,
and safe to remove again.


What the cards show
-------------------

One compact card per resource, each with two rows of figures:

  top row     - fuel and maintenance burned while carrying that resource
  bottom row  - the empty run out to collect it, which burns at the same full
                rate and is about half the real cost of a round trip

Distance, delivery count and quantity delivered are in each card's tooltip.

Above the grid there is a totals row, plus an "Unattributed" figure for burn
that cannot be pinned to any resource, such as queue idling or refuelling.

Two independent columns of tickboxes filter the view:

  by job     - general operations, mining, refuelling
  by vehicle - pickup trucks, trucks, haul trucks, amphibious trucks

The totals and the Unattributed figure always reflect whatever is ticked. The
dropdown scopes everything to the last 1, 3, 6 or 12 months.

Stats persist across save and load, and are stored per save.


Notes and limitations
---------------------

- Fuel is reported as one figure regardless of which fuel it was. Every truck
  tier ships in diesel and hydrogen variants and both belong to the same vehicle
  class, so a fleet mid-transition adds the two together. In practice a fleet is
  usually on one fuel and the changeover is short.
- Trucks only. Excavators, tree harvesters, planters and rocket transporters
  burn fuel too but are not counted.
- Fuel burn is not proportional to speed; the game models it as a flat rate per
  job state. Distance is reported separately rather than used to scale anything.


Source, issues and full documentation
-------------------------------------

    https://github.com/chestnut42/coi-vehicle-tracker

If something misbehaves, the game logs are in:

    %APPDATA%\Captain of Industry\Logs
