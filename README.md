# Auto Public Transit Planner

Auto Public Transit Planner is a Cities: Skylines helper for bus-first public transport planning.

The mod scans city demand, links useful destinations, builds complete bus lines through the vanilla transport-line system, keeps existing lines stable during later scans, and gives advisory depot/economics guidance.

## Current Features

- Demand scanning from eligible city buildings.
- Bus stop placement on valid surface roads only.
- Transit hub and tourist anchor weighting.
- Complete generated bus lines that are visible to the vanilla public transport UI.
- Existing-line maintenance that avoids disruptive deletion of usable live service.
- Depot count guidance after scans.
- Bus economics guidance informed by ridership, vehicles, depots, useful lines, and free-transport policy.
- Unified launcher toolbar support shared with the ScratchyBald Cities: Skylines mod family.

## In Development

- Bus-lane road upgrade recommendations and application are visible as in development and are disabled for this release.
- Metro, train, tram, water, air, monorail, and cable car automation are visible as future mode tabs but are not active yet.

## Usage

Open the Auto Public Transit panel from the launcher toolbar, then run `Scan, Build & Apply` from the Overview tab.

The planner focuses on complete, usable bus routes. After a scan, review the Overview cards for built lines, utilisation changes, depot guidance, and next actions.

## Compatibility Notes

Transit Vehicle Spawn Delay can slow first vehicle dispatch on newly created APT bus lines. If APT warns about it after creating lines, set that mod's Bus spawning delay to 1 or lower, or temporarily disable it, until the new services have spawned their first buses and are running normally.

Improved Public Transport Essentials/IPTE is different: its spawn delay separates new lines from established lines, so it should not need to be turned off for APT-created services to start.

## Release Notes

Version 1.0.2 is a bug-fix release for the bus planner.

This release adds safer bus-planning defaults, a Bus Options reset button, clearer scan-mode wording, initial-network scan warnings, generated stop editability fixes, and Transit Vehicle Spawn Delay compatibility guidance. Bus-lane road upgrades remain parked for a later release.

## Credits

Public transport profitability and depot guidance concepts are informed by Galeocerdo7's Steam guide Guide to Profitable Public Transit.
