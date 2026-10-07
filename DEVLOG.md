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
