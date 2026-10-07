# Development log

One entry per git commit, newest at the bottom.

## October 6th, 2026 - Initial commit - Commit 1

Added:
* Project layout: engine-independent simulation library (`src/Remade.Sim`), xUnit test suite (`tests/`), portable toolchain scripts (`tools/env.*`)
* Persistent rotating logger: millisecond timestamps, severity, build + git commit, session id, tick, map id, correlation ids, caller file/line/method, thread id; errors flushed synchronously with the recent breadcrumb trail and last player action; size and age based rotation
* Invariant checks that log and throw instead of silently repairing state
* Deterministic RNG (xoshiro256**) and seeded simplex noise (2D/3D, fBm, ridged)
* Game calendar (60 ticks/s, 60 000 ticks/day, 60-day year with four seasons)
* Geodesic hex-sphere planet grid (12 pentagons + hexagons, ordered adjacency, polygon corners, nearest-tile walk)
* Planet generation: domain-warped continents, ridged mountain ranges, sea level from ocean coverage, latitude/elevation/continentality temperature, zonal + orographic precipitation, priority-flood drainage, river flow accumulation and river classes, coasts, biomes, hilliness
* Dynamic planetary climate: seasonal cycle, travelling low/high pressure systems, natural anomalies, soil moisture budget, prevailing winds; double-buffered parallel hourly update; save/load of the climate state
* 35 unit tests (grid topology, determinism, drainage, biome rules, seasons, precipitation balance, logger flush/rotation, invariants)

Bug fixed
* Temperature was NaN on the pole tile
  * cos(90°) is slightly negative in float and `Pow` of a negative number returns NaN.

## October 6th, 2026 - Local map simulation - Commit 2

Added:
* Local map layers (terrain, granite rock, buildings, doors, oaks with 12 shape variants, berry bushes, grass, corner heightfield, rock massif heights) with change tracking for the renderer and path grid
* Map generation from the planet tile: relief by hilliness, granite massifs rising toward their cores (distance transform), rivers entering from the upstream planet neighbours and leaving toward the downstream one, sea along coastal sides, ponds and small lakes, soils, oak groves and clearings, berry bushes, grass density, an abandoned cabin with a door
* A* pathfinding (8 directions, no corner cutting, generation-stamped workspaces, line-of-sight smoothing) and connected components for instant unreachable rejection
* Items and a Stalker/Project Zomboid style grid inventory (stack merging, rotation, drag moves) provided by worn containers (trouser pockets, jacket pockets, satchel)
* Apparel on six layers (skin, middle, outer, headgear, eyes, belt) with per-region conflicts
* RimWorld human body part tree (brain, skull, eyes, jaw, tongue, ribs, organs, clavicles, fingers, toes…) and a deer body; injuries, bleeding, death rules
* Pawns: names, sex, biological/chronological age, traits, 12 skills with passions, body mass, encumbrance from real load guidance (25/45/70 % of body mass)
* Needs (food, thirst, rest) with real-life decay rates; heat increases thirst
* Simulation loop (60 ticks/s, speeds 1/3/6/15x with a per-frame budget), autonomous AI (sleep, drink, gather and eat berries, wander), drafted orders, direct control with collisions
* Interactions shared by the E key, the right-click menu and the AI: pick up, gather, drink, open/close door, hunt, attack, eat, go
* Combat: bow with arrows (projectiles, spread by skill and movement, trees and walls block), melee swings, deer flee and alert their herd, venison and recoverable arrows on death
* Local weather derived from the planet tile (temperature with diurnal curve, rain/snow, cloud, wind, fog, wetness, snow cover)
* Versioned, compressed, validated save files that restore the exact simulation state (jobs, paths, projectiles, RNG)
* 28 new unit tests including the full goal scenario (bow → deer → meat) and deterministic continuation after save/load

Changed:
* Rock coverage is now a quantile of the rock field, so each hilliness class gets a predictable share of granite

Bug fixed
* Maps without a coast were generated entirely as shoreline
  * The sea-distance field defaulted to 0, which means "at the shore", instead of "far inland".
* Pawns could get stuck on tree trunks
  * Tree cells were passable for paths while their trunks are solid for movement.
* Colonists on dry tiles died of thirst
  * Maps without a river or coast had no fresh water; ponds and lakes are now generated.
* Games diverged after loading a save
  * Jobs, paths and think timers were not saved, and loading items consumed the random generator.
