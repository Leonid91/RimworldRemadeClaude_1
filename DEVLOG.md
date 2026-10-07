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

## October 6th, 2026 - Godot game and menus - Commit 3

Added:
* Godot 4.7 .NET game project referencing the simulation library; boot sequence with persistent logging, Godot engine errors and unhandled exceptions routed into the log, and an on-screen error banner
* Settings file with rebindable keys, one-click AZERTY (ZQSD) remap with automatic detection of French/Belgian layouts, graphics, audio and gameplay options
* Main menu over an orbital dawn: the planet's limb with atmospheric glow and the sun rising behind it
* Set-up flow: scenario screen (prepared, empty), colonist creation sheet (names, sex, ages, traits, 12 skills with passions, health, worn gear, carrying capacity, 3D portrait, randomize, team skills), world generation screen with Generate, landing-site globe, map sizes from 300 to 1500, Play
* Globe: smooth sphere shaded from baked cubemaps (soft biome blending from interpolated climate, sea depth, sea ice, snow caps, relief normals, rivers drawn as smoothed lines), clouds, atmosphere, space sky; hexagon outlines only around the hovered tile, pulsing outline and beacon on the selected tile; live temperature, elevation and precipitation overlays with legends
* Colony renderer: lazily streamed 64×64 terrain chunks with procedural terrain texture splatting, granite massifs with jagged cliffs, water with refraction, foam and flow, log-cabin walls and an animated door, oak trees (12 variants, detail + low LOD) and berry bushes as region MultiMeshes, a camera-centred GPU grass field, vertex-animated deer (one draw call per sex), animated colonist puppets, ground items, flying arrows, selection ring, interaction highlight and red aim line
* Lighting from real solar geometry (latitude, season, hour), moonlight, physical sky with stars, fog and volumetric haze driven by the planet's weather, rain and snow particles, cloud shadows, wetness and snow cover in the shaders
* Procedurally synthesised sound: wind, rain, water, leaves, birds, crickets reacting to the environment; bow, arrow hits, door, pick-up, drinking, footsteps
* HUD: colonist bar, time/weather panel with speed controls, messages, inspect panel with Bio / Equipment (layered body figure, grid inventory with drag and drop and load bar) / Needs / Health (per-part conditions, operations with an empty Add bill), Draft and Direct control buttons, right-click order menu, direct-control interaction list (E + mouse wheel), aim readout, pause menu with saving, in-game planet view
* AutoPilot for automated runs through the real UI and input, with screenshots and a PASS/FAIL result

Changed:
* The simulation and game assemblies are always JIT-optimised
* Climate interpolation uses dot-product weights and one pass for several fields (no acos per sample)

Bug fixed
* The globe looked like streaky plastic
  * Relief normals were about 25 times too strong.
* Two colonists could share the same nickname
  * Uniqueness was only checked on first names.
* Engine error "Node not inside tree" in the colonist portrait
  * The portrait camera used LookAt before being added to the scene tree.

## October 6th, 2026 - Goal scenario playable, faster - Commit 4

Added:
* The full first-version goal is playable and verified in the real game through real inputs: take direct control, open the cabin door with E, pick up the bow and arrows, aim with the right mouse button, shoot a deer with left clicks, the deer drops venison, pick the meat up
* Doorway assist for direct control: when a move is blocked by a corner, the colonist sidesteps into the nearest opening (doors, gaps) instead of sticking
* Analog movement input (Input.GetVector): gamepad sticks work and automation steers precisely through the same input actions
* Debug/profiling switches: --weather= (pins local weather), --gfx-off= (shadows, ssao, fog, glow, grass, trees, water, taa, hud), --perf readout; AutoPilot commands camfind and cam, and a re-planning walker with diagnostics when blocked
* Unit test for the doorway assist

Changed:
* Oak meshes rebuilt for an overhead camera (about 450 triangles detailed, 100 for the low LOD instead of about 1500); shadows are cast by the low LOD mesh only; flora regions of 128 cells; the ground no longer casts shadows; 2 shadow cascades on Medium, 4 on High → frame time 13.6 ms → 8.3 ms, triangles 5.7 M → 1.2 M in the test view
* Cloud shadows evaluated per vertex for foliage, bark and grass
* Larger clearing around the cabin and a wider canopy cut-away around the controlled colonist so both stay visible from above
* Leaf cards tilted toward the sky; river beds and pond floors meet the banks continuously; thinner foam

Bug fixed
* Foliage, bushes and grass blades were far too dark
  * The leaf texture (linear) was normalised by sRGB reference values, and Godot flips normals on back faces of two-sided cards.
* Deer, colonists and items looked washed out
  * Vertex colours authored in sRGB were used as linear colours.
* Square artefacts on rivers
  * Each water cell had its own flow vector, so the scrolling normal maps broke at cell edges; flow is now per shared corner with two-phase flow mapping.
* The cabin and colonists were hidden under the forest canopy
  * The cabin could be placed inside the forest with only a 2-cell clearing.

## October 6th, 2026 - Mining, tests, atmosphere - Commit 5

Added:
* Mining: granite can be mined with a right-click order or E in direct control; work time depends on the Mining skill, the cell becomes rough granite floor, paths open up, sometimes a 25 kg granite chunk drops; pick and break sounds
* Integration tests part 1 (`tests/Remade.Integration`): planets up to 163 842 tiles, a year of climate, maps up to 1500×1500, a full day of simulation with all deer, pathfinding stress, save/load of a 1000×1000 colony with identical continuation, and the goal scenario on a 600×600 map — each with a performance budget, Markdown report in test-results/
* Integration tests part 2 (`tools/integration.sh`): unit tests as a gate, then the real game driven through its UI and input in scenarios (UI flow with globe overlays and options, goal, mining, save → load, 1000 and 1500 maps with performance readouts, dawn/noon/dusk/night, rain, snow, fog), screenshots per scenario, any logged error fails the run
* Build, test and run scripts (`tools/build.sh`, `tools/test.sh`, `tools/run.ps1`, `Play.bat`), project notes (CLAUDE.md) and player guide (README.md)
* Unit tests: mining, shared planet/colony clock

Changed:
* Twilight and night: blue-violet ambient light and exposure that adapts in the dark, so dawn and dusk are readable
* Fog is pale and neutral and lighter; the low sun loses its orange in mist and overcast
* Clouds fade away on the globe while a climate overlay is shown; overlay legends follow the active overlay
* Map size panel fits the screen; the selected size is highlighted
* A colony cannot be started before the planet's current time (the planet and its maps share one clock)

Bug fixed
* Blocky squares on water in the rain
  * Rain ripples were cut at the edge of their 0.5 m cells.
* Fog turned the whole scene brown-red at sunrise
  * The volumetric fog scattered the strongly orange low sun at a too high density.
* "Mine" was never offered by the E key next to a rock
  * The reach test for rock faces was stricter than for other interactions.

## October 6th, 2026 - Saturated planet overlays - Commit 6

Changed:
* Final integration run on the committed build: unit tests 66/66, part 1 13/13, part 2 10/10 scenarios (no logged errors)

Bug fixed
* Planet temperature and precipitation overlays looked pale and washed out
  * The colour ramps are authored in sRGB but were used as linear colours in the globe shader.

## October 7th, 2026 - Feedback fixes, prototype look - Commit 7

Added:
* Traits change real stats (move/work/gather speed, shot spread, aim time, melee damage and accuracy, damage taken, hunger/thirst/fatigue rates, carrying capacity); hovering a trait lists its effects ("Move speed +15 %") instead of a description
* One inventory grid per colonist whose size follows their carrying capacity (about one slot per kg of march load, 8 columns), plus the hands: drag between hands and grid, Equip / Take in hands / Put away / Eat / Drop
* Arrows and melee blows hit colonists (body part, bleeding, death, messages)
* Lakes: small enclosed water bodies are fresh lakes (own biome and colour), their shores get a lake beach on the local map like sea coasts
* Estuaries: narrow sea inlets fed by a river become settleable river-mouth tiles instead of open ocean
* Globe legend with every biome colour; region panel rows Water (Estuary, River/Creek, Coast, Lake shore, None), Ground moisture and average wind with explanations
* Day/night bar under the clock (night, dawn, day, dusk from the real sun height for the latitude and season, sun/moon marker)
* Autopilot steps: hover, hovertext, inv=putaway|equip:id, aim=on|off, expect=bowaway; integration scenarios check the overlay hover values, the lake, put away / re-equip, and the aim line at close and far zoom
* Unit tests: inventory size and carrying capacity, trait effects, put away / re-equip, arrows hitting colonists, lakes, estuaries, doors left open

Changed:
* Colonists wear a white cotton T-shirt and jeans only (no satchel, pockets, jacket, cap, boots, glasses)
* World look ported from the first prototype (RimworldTest): procedural terrain colours with organic cell blending and caustics, its water (depth colour, foam, continuous scrolling), grass clumps with flowers and its grass shading, small round oaks of painted leaf cards tinted per tree, bushes, bark, lighting (procedural sky, rosy dawn and blue night ambient, ACES, saturation, moonlight), camera field of view, pitch and tilt-shift depth of field
* Colonists rebuilt in the prototype's readable style (big head, blinking eyes, rounded torso), 15 % larger
* Globe: one flat colour per hexagonal region with crisp anti-aliased borders and no shading inside a biome, no clouds (kept on the menu backdrop), matte map shading, smoother and thinner rivers, 1024 px faces
* Hovering the globe shows the active overlay's value (°C, m, mm/day) instead of always the temperature
* Bow release sound: a sharp twang, limb knock and arrow whoosh, always audible (not attenuated by the camera height)
* Autumn colours arrive during autumn instead of on its first day; crowns thin out in winter
* Main menu: subtitle removed
* Saves are version 2 (inventory and hands)
* Autopilot presses E on closed doors in its way and never clicks through HUD panels while shooting; the integration goal scenario keeps the deer on the colonist's side of any water
* Final integration run: unit tests 75/75, part 1 13/13, part 2 10/10 scenarios (no logged errors); 1000×1000 at 81 fps, 1500×1500 at 66 fps (RTX 3060 laptop, 1080p)

Bug fixed
* The bow did not appear in the hands after putting it away and equipping it again
  * The inventory panel's change callback did nothing, so the hands section was never rebuilt.
* Shooting or hitting another colonist did nothing
  * Projectiles and melee only tested deer.
* The aim line disappeared when zoomed far out
  * Its fixed 7 cm width fell under one pixel; it now keeps its on-screen width and draws over trees.
* Grass glittered and shimmered while the camera moved
  * Clump positions and randomness were hashed from the window slot, so the whole field re-shuffled each time the window moved one cell.
* Water moved stop-go while everything else was fluid
  * The two-phase flow map restarted its phase over the whole river at once every 1.4 s.
* Colonists held the bow the wrong way and aimed with their arms behind them
  * Limb rotation signs were inverted and the bow was turned +90° instead of −90°; the bow now follows the body's facing.
* Lakes were treated as open ocean and estuaries as sea
  * Every tile below sea level was one kind of water; river mouths were left as ocean tiles.
* A loud bubbling sound covered every other sound near water
  * The water loop contained random rising "gurgle" blips.
* Colonists closed a door the player had left open
  * Walking through any open door scheduled it to swing shut.
* Stack counts were cut off on narrow inventory items ("x2" for 24 arrows)
  * The count was right-aligned and clipped to the item's width.
* The time panel slid off the right edge of the screen with a long weather line
  * It grew to the right from its anchor; it is now pinned by its bottom-right corner and grows left.
* The integration goal scenario started on a meaningless tile
  * It used another planet's start-tile index on a different planet.

## October 7th, 2026 - 3D archery, hex heatmaps - Commit 8

Added:
* Arrows fly in 3D: launched from the shoulder on the arc that reaches the aimed point, pulled by gravity and drifted by the wind; they collide with the ground, water, walls, rock, tree trunks and crowns and with the real body volumes of colonists and deer, and the impact point decides the body part (legs, arms, torso, neck, head, eyes; a deer's legs, body, neck or head)
* Arrows stick where they hit: in the ground, in trunks and wooden walls (recoverable), and in colonists and deer (they move with them; part of them are recovered from a killed deer)
* Aiming at the 3D point under the cursor (the body of a creature, or the ground) with a red arc showing the predicted flight and a ring at the impact, "blocked" when something is in the way
* Terrain blocks line of sight and shots: from the bottom of a hollow you cannot see or shoot over its rim
* First-person view (Tab while controlling a colonist): mouse look, walking relative to the view, crosshair, drawing and shooting the bow at the crosshair; Tab or Esc returns
* Clothes can be taken off into the inventory and put back on: drag between the new "Worn" row and the grid, or right-click (Take off, Take off and drop, Wear)
* Globe: temperature, elevation and precipitation maps coloured hexagon by hexagon with thin outlines, seas and lakes in grey; painted icons for hills, large hills, mountains and impassable mountains on their hexagons
* Play-settings corner above the clock (RimWorld style): a grid button cycling off / always / around the cursor, and a steady readout of what lies under the cursor
* Yellow star on the card of the colonist under direct control
* Instant trait tooltips: effects listed with helpful ones in green and harmful ones in red
* Inventory size shown with its slot count (8×4 = 32 slots)
* Unit tests: launch solution, impact height → body part, arrows stuck in the ground and in trunks, terrain line of sight, taking clothes off and on

Changed:
* Look inspired by 8th Wonder of the World (reworked, not copied): lush saturated meadows with lighter brushed patches, warm reddish dirt with pebbles, gravel as small rounded stones in earth, dark faceted charcoal rock with pale lit tops and deep cracks, oak crowns as distinct round clumps with warm tops and deep undersides, richer daylight colour
* Ponds lie in the natural low ground with gentle shores and a narrow mud band
* Shot spread recalibrated for 3D aiming (novice 3.2°, master 0.6°, +2.5° when moving)
* Passions removed (not in the specification): no flames next to skills
* Messages appear under the colonist bar
* Saves are version 3 (3D arrows, stuck arrows)
* Final integration run: unit tests 79/79, part 1 13/13, part 2 10/10 scenarios (no logged errors); 1000×1000 at 95 fps, 1500×1500 at 76 fps

Bug fixed
* The terrain tooltip under the cursor blinked unreadably
  * It was cleared on every frame the cursor stayed on the same cell and only shown on the frame it changed cell.
* Lakes sat at the bottom of round craters
  * The relief generator flattened the hills in a ring several pond-radii wide around every pond.
* The aim line clung to the ground and dived into hollows
  * It was drawn along the terrain height instead of along the arrow's flight.
* Long messages slid under the colonist cards
  * Both were anchored to the top of the screen.
* Colonists could land on ground cut off from the cabin (and the bow) by water
  * They were placed around the map centre without checking that it connects to the cabin door; they now land on the nearest connected ground.
