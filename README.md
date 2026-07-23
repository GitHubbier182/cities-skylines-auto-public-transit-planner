# Auto Public Transit Planner

Auto Public Transit Planner now plans three kinds of Cities: Skylines public transport: Bus, Metro, and passenger Train.

Version 2.3.1 prevents Bus overview health monitoring from interrupting a scan while generated lines are still settling and bounds live line-vehicle traversal against malformed chains. The Metros and Trains pages continue to turn player-built stations and connected tracks into working passenger lines, while the separate Bus page retains APT's citywide demand and coverage planning.

Each mode creates complete lines through the normal game transport system. Existing passenger services and their settings are preserved, Metro and Train vehicle operation remains vanilla-owned, and cargo rail is excluded.

## Current Features

- Demand scanning from eligible city buildings.
- Bus stop placement on valid surface roads only.
- Transit hub and tourist anchor weighting.
- Complete generated bus lines that are visible to the vanilla public transport UI.
- Existing-line maintenance that avoids disruptive deletion of usable live service.
- Depot count guidance after scans.
- Bus economics guidance informed by ridership, vehicles, depots, useful lines, and free-transport policy.
- Delayed bus dispatch health checks for newly created lines, with warnings when depot dispatch or vehicle spawning still looks blocked after settling.
- Harmony-backed depot-aware bus spawn and return steering, including jammed-depot fallback for new dispatch.
- One-time v2 welcome screen with a short summary of the major changes for returning players.
- Unified launcher toolbar support shared with the ScratchyBald Cities: Skylines mod family.
- A separate Metros page that scans only player-built Metro stations and tracks, without running the citywide bus demand scan.
- Complete Metro line creation across topology-derived through-corridors, pairing distant termini through junction stations before hidden vanilla validation and publication.
- Non-destructive Metro repeat scans that retain existing lines/settings, suppress only already-served planned corridors, and can add service for new branches or missing through-corridors.
- A separate Trains page that applies the same topology-first planning and hidden vanilla validation to passenger stations and player-built rail.
- Passenger-only Train planning: cargo stations, cargo service and cargo lines are excluded and left untouched.
- An isolated connected component containing exactly two Metro or passenger Train stations can publish a back-and-forth shuttle; larger connected station networks suppress two-station fragments in favour of trunk or longer through-services.

## Requirements

- Harmony 2.2.2-0 (Mod Dependency), Steam Workshop item `2040656402`, is required for depot-aware bus spawn and return steering.

## Current Scope

- Bus, Metro, and passenger Train line planning are the active supported modes.
- Road Upgrades is parked and remains hidden/disabled unless development is explicitly resumed.
- Metro and passenger Train planning is topology-based and passenger-only. It does not construct infrastructure or plan cargo service.

## Usage

Open the Auto Public Transit panel from the launcher toolbar, then run `Scan & Build Bus Lines` from the Bus tab.

The separate `Metros` tab scans only Metro infrastructure and can create lines over connected stations and tracks already built by the player.

The separate `Trains` tab scans only passenger Train stations and rail tracks. Cargo stations and cargo operations are never planned or changed.

The bus planner focuses on complete, usable routes. After a scan, review the Bus cards for built lines, utilisation changes, depot guidance, and next actions.

## Compatibility Notes

Version 2.3.1 retains the version 2.3.0 compatibility foundation tested with Improved Public Transport 3, Improved Public Transport Essentials, Transport Lines Manager, and Traffic Manager: President Edition (TM:PE).

Add `DND` as a separate word in a line name, such as `[DND] Downtown Loop`, to mark that line as player-protected. APT keeps DND-marked bus stops as coverage but skips those lines during scans, economics/depot guidance, bus-lane counts, spawn-health checks, and Line Tools deletion.

Transit Vehicle Spawn Delay can slow first vehicle dispatch on newly created APT bus lines. If APT warns about it after creating lines, set that mod's Bus spawning delay to 1 or lower, or temporarily disable it, until the new services have spawned their first buses and are running normally.

Existing bus lines can keep running with buses that are already on the road even when new line dispatch is blocked. If APT warns that new lines still have no assigned or only path-waiting buses after settling, check powered and connected bus depots, vehicle limits, traffic near depots, and transport or vehicle-spawn mods.

Improved Public Transport Essentials/IPTE is different: its spawn delay separates new lines from established lines, so it should not need to be turned off for APT-created services to start.

APT-generated normal bus lines try to select and keep ordinary city bus models, avoiding coach/intercity/school-service vehicle models unless the route is owned by the SchoolBuses mod. If no compatible ordinary city bus model is available, APT skips those generated routes and warns in the Bus guidance and `Player.log` / `output_log.txt`.

## Credits

Public transport profitability and depot guidance concepts are informed by Galeocerdo7's Steam guide Guide to Profitable Public Transit.
