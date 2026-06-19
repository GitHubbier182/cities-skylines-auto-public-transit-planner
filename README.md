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
- Delayed bus dispatch health checks for newly created lines, with warnings when depot dispatch or vehicle spawning still looks blocked after settling.
- One-time v2 welcome screen with a short summary of the major changes for returning players.
- Unified launcher toolbar support shared with the ScratchyBald Cities: Skylines mod family.

## In Development

- Bus-lane road upgrade recommendations and application are visible as in development and are disabled for this release.
- Metro, train, tram, water, air, monorail, and cable car automation are visible as future mode tabs but are not active yet.

## Usage

Open the Auto Public Transit panel from the launcher toolbar, then run `Scan, Build & Apply` from the Overview tab.

The planner focuses on complete, usable bus routes. After a scan, review the Overview cards for built lines, utilisation changes, depot guidance, and next actions.

## Compatibility Notes

Add `DND` as a separate word in a line name, such as `[DND] Downtown Loop`, to mark that line as player-protected. APT keeps DND-marked bus stops as coverage but skips those lines during scans, economics/depot guidance, bus-lane counts, spawn-health checks, and Line Tools deletion.

Transit Vehicle Spawn Delay can slow first vehicle dispatch on newly created APT bus lines. If APT warns about it after creating lines, set that mod's Bus spawning delay to 1 or lower, or temporarily disable it, until the new services have spawned their first buses and are running normally.

Existing bus lines can keep running with buses that are already on the road even when new line dispatch is blocked. If APT warns that new lines still have no assigned or only path-waiting buses after settling, check powered and connected bus depots, vehicle limits, traffic near depots, and transport or vehicle-spawn mods.

Improved Public Transport Essentials/IPTE is different: its spawn delay separates new lines from established lines, so it should not need to be turned off for APT-created services to start.

APT-generated normal bus lines try to select ordinary city bus models and avoid coach/intercity-style vehicle models. If no compatible ordinary city bus model is available, APT warns in the Overview guidance and `Player.log`.

## Release Notes

Version 2.0.0 is a major planning, coverage, UI, and compatibility update.

- School bus routes created by the SchoolBuses mod are treated as external coverage-only service. APT can reuse their stops for coverage but should not delete, compare, amend, or retire those routes.
- Players can add `DND` to a line name to keep that line out of APT scans, economics/depot guidance, bus-lane counts, spawn-health checks, and delete-all actions while still letting its stops count as coverage.
- Generated normal bus lines avoid coach/intercity-style vehicle models where an ordinary compatible city bus model is available, with a specific warning if no ordinary city bus model can be selected.
- A one-time v2 welcome screen appears after updating so returning players see the main workflow and compatibility changes in-game.
- Delete Bus is not guaranteed to recognise every bus line created by older v1 builds. After upgrading, players may need to remove old v1-generated bus lines once with the vanilla or transport-mod line tools before using the v2 cleanup tools for ongoing networks.

Version 1.0.3 is a bug-fix release for the bus planner.

This release adds delayed bus dispatch health checks after APT creates new bus
lines. If vanilla dispatch still has no assigned buses, only path-waiting buses,
depot warnings, or a risky Transit Vehicle Spawn Delay setup after the lines
settle, APT now shows a player-facing advisory with practical next steps.

Version 1.0.2 was a bug-fix release for the bus planner.

That release added safer bus-planning defaults, a Bus Options reset button, clearer scan-mode wording, initial-network scan warnings, generated stop editability fixes, and Transit Vehicle Spawn Delay compatibility guidance. Bus-lane road upgrades remain parked for a later release.

## Credits

Public transport profitability and depot guidance concepts are informed by Galeocerdo7's Steam guide Guide to Profitable Public Transit.
