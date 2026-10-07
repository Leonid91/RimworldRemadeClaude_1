# CLAUDE.md: RimworldRemadeClaude_1

RimWorld-like colony sim, 2.5D (overhead 3D), Godot 4.7.2 .NET (C#), Forward+. Everything (meshes, textures, sounds,
UI) is generated in code. Requirements: `docs/SPEC_RimworldLike1.txt`. Player docs: `README.md`. Change log: `DEVLOG.md`.

## Rules from the spec (do not drop)
- Every change = one DEVLOG entry ("Month DDth, YYYY - Title (≤5 words) - Commit N", Added / Changed / Bug fixed with a
  one-phrase cause under each bug) = one git commit.
- Before handing over: unit tests (`tools/test.sh`) → integration part 1 + part 2 (`tools/integration.sh`), look at
  the screenshots. Never launch integration runs if unit tests fail.
- Logging: persistent, rotating (`logs/`), errors flushed immediately with breadcrumbs. No silent catches, no
  default-value fallbacks after failures, invariants (`Invariant.Check`) instead of repairs.
- Performance is a requirement (target: half of an RTX 3060 laptop). Profile before optimising (`--perf`, `--gfx-off`).

## Toolchain (portable, `D:\Experiments\ExperimentTools`)
- `source tools/env.sh` (Git Bash) or `. .\tools\env.ps1` first: .NET SDK 10 (system .NET is 7), `$GODOT` console exe.
- `tools/build.sh` builds sim + tests + game. The sim and game assemblies are always JIT-optimised (`<Optimize>`).
- Unit tests target net10.0 (only runtime installed); the sim library and game target net8.0.
- Long patch scripts: write a .py file in the scratchpad, not an inline heredoc (quoting breaks).

## Layout
```
src/Remade.Sim           engine-independent simulation (unit-testable)
  Diagnostics/           Log (rotating file, breadcrumbs, correlation ids, timings), Invariant
  Core/                  Rng (xoshiro), Noise (simplex), GameTime (60 ticks/s, 60000 ticks/day, 60-day year)
  World/                 HexSphere (geodesic hex grid), Planet (elevation, climate normals, rivers, biomes), Climate
                         (dynamic, hourly, double-buffered: seasons, weather systems, soil moisture, wind)
  Map/                   LocalMap (SoA layers + change list), MapGen (from a planet tile), Pathfinder (A*, components)
  Things/                ThingDefs (all item defs and real-world masses), Item, InventoryGrid
  Pawns/                 Pawn, Needs, Traits, Skills, Body (RimWorld part tree), Health, PawnGenerator
  Sim/                   GameSim (tick loop, items), PawnAI (jobs, direct control, doorway assist), AnimalAI,
                         Combat (arrows, melee, hunting), Interactions (E key / right-click / AI), LocalWeather
  Save/                  SaveGame (versioned, Brotli, validated, exact state incl. jobs and RNG)
tests/Remade.Sim.Tests   xUnit unit tests (incl. the goal scenario and save determinism)
tests/Remade.Integration integration part 1: full-scale systems with performance budgets → test-results/*.md
game/scripts/App         Main (screens, boot), GameView (in-game controller + input), Settings (keys, AZERTY),
                         Args, AutoPilot (scripted runs), EngineLogBridge (Godot errors → log)
game/scripts/Planet      PlanetBaker (cubemaps on CPU), PlanetView (globe, hover hexes, selection, overlays)
game/scripts/Render      MapRenderer (terrain/rock/water chunks + map textures), FloraRenderer (oaks, bushes, GPU
                         grass), EntityRenderer (deer MultiMesh, pawns, items, arrows, aim line), Lighting (solar
                         geometry, sky, fog, shader globals), WeatherFx, CameraRig, Models, PawnModel, ProcTextures
game/scripts/UI          UiKit (theme), Screens (menu, scenario, worldgen, loading), ColonistScreen, PlanetScreen,
                         LoadOptions, Hud, InspectPanel (Bio/Equipment/Needs/Health), BodyFigure, InventoryView
game/scripts/Audio       Audio3D (procedural synthesis, environment-reactive ambience)
game/shaders             world_common.gdshaderinc (globals), terrain, rock, water, foliage, bark, grass, deer, planet_*
```

## Conventions
- 1 cell = 1 m. Cell (x,y) covers world X [x,x+1], Z [y,y+1]; map north = −Z (screen up). Models face −Z.
  Sim facing angle 0 = +x; model yaw = −facing − π/2. Godot front faces are clockwise.
- Vertex colours are sRGB (`VertexColorIsSrgb`, or `pow(COLOR, 2.2)` in custom shaders). Shader colour constants are linear.
- Two-sided cards (leaves, grass) undo Godot's back-face normal flip (`if (!FRONT_FACING) NORMAL = -NORMAL;`).
- World look follows the first prototype (D:\Experiments\RimworldTest): shared RGBA noise texture
  (`ProcTextures.Noise`: R fbm, G cellular, B value, A low-freq), terrain/grass/water/foliage/bark shaders, lighting.
- Plant MultiMesh custom data = leaf tint (sRGB rgb) + sway phase × 0.5 (a). Grass field slots carry only a slot index;
  every random property is hashed from the WORLD cell (never from the window slot, or the field re-shuffles = shimmer).
- PawnModel joints: +X rotation swings a hanging limb forward (−Z); knees bend with −X, elbows with +X. Held bow is
  oriented from the body yaw (not the arm chain).
- Planet water: `Planet.Water[t]` (None/Ocean/Lake); lakes = small enclosed water bodies (Biome.Lake), estuaries are
  land river tiles (`Estuary[t]`) draining to the sea. Coast = next to ocean, LakeShore = next to a lake.
- Map mutations go through LocalMap setters (they record CellChange); GameSim applies them to the path grid each tick
  and hands them to the renderers once per frame (`FrameChanges`).
- The planet and all its maps share one clock: a colony cannot start before the planet's climate time.
- Local outdoor temperature = planet tile daily mean + diurnal curve (`ClimateSample.TemperatureAtHour`); rain/snow,
  cloud, wind, fog, wetness and snow cover all come from the planet tile (`LocalWeather`).
- Globe cubemap face orientation: `PlanetBaker.FaceDir/DirFace` (+X, −X, +Y, −Y, +Z, −Z, OpenGL convention).

## Running and testing
- `Play.bat` (player), or `"$GODOT" --path game -- [args]`. First run after shader changes compiles pipelines (slow).
- Args: `--play` (skip menus) `--map=N --seed=N --tile=N --hour=H --weather=clear|cloudy|rain|storm|fog|snow`
  `--windowed --novsync --perf --gfx-off=shadows,ssao,fog,glow,grass,trees,water,taa,hud --verbose --globe-debug`.
- AutoPilot `--auto="cmd=arg; ..."`: wait, waitfor=game|globe|planet|menu, click=Text (click==Exact), key=action,
  hold=action,s, shot=name, select_tile=start|N, overlay=…, mapsize=N, pawn=i, control=i, speed=N, tab=…, layer=…,
  walkto=item:bow|door|deer|outside|x,y, interact[=label], deer_near=dist, aimshoot=n, hour=H, world, cam=dist[,yaw],
  camfind=water|rock|tree|bush|cabin, expect=bow|arrows|meat|deerdead|dooropen, log=…, quit.
  Any ERROR logged during a run fails it. Screenshots go to `screenshots/`.
- `tools/integration.sh [quick]`: build → unit tests (gate) → part 1 → game scenarios (ui_flow, goal, saveload,
  perf_1000, perf_1500, visuals, rain, snow, fog); report in `test-results/`, screenshots per scenario.

## Performance notes (RTX 3060 laptop, 1080p)
- Test view (250 map, forest + river, noon): ~8 ms/frame, ~1.2 M triangles, ~480 draws.
- Costs measured with `--gfx-off`: shadows ≈ 2 ms, trees ≈ 3 ms. Trees cast shadows with their low-LOD mesh only.
- Sim: 9 µs/tick with 245 deer on 1000×1000 (Ultra needs 900 ticks/s). Cross-map A* ≈ 5 ms avg on 1000×1000.
  C++ has not been needed: profiling shows no CPU hot spot near the budgets (revisit pathfinding with HPA* first).
- Chunks/regions stream in by view and are freed when long unseen; grass is a camera-centred GPU field.
