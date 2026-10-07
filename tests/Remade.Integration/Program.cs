using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Pawns;
using Remade.Save;
using Remade.Sim;
using Remade.Things;
using Remade.World;

// Integration tests, part 1: exercises the big systems at full scale and checks performance budgets.
// Usage: dotnet run -c Release --project tests/Remade.Integration [-- --quick]
// Writes a Markdown report to test-results/ and the full log to logs/. Exit code 0 = all passed.

bool quick = args.Contains("--quick");
string root = FindRoot();
Directory.CreateDirectory(Path.Combine(root, "test-results"));
Log.Init(new LogConfig { Directory = Path.Combine(root, "logs"), FilePrefix = "integration", Verbose = true, MirrorToConsole = false });
var results = new List<(string name, bool ok, string detail)>();
var proc = Process.GetCurrentProcess();

void Check(string name, bool ok, string detail)
{
    results.Add((name, ok, detail));
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  —  {detail}");
    if (ok) Log.Info($"PASS {name}: {detail}"); else Log.Error($"FAIL {name}: {detail}");
}

double Ms(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
long WorkingSetMb() { proc.Refresh(); return proc.WorkingSet64 / 1048576; }

Console.WriteLine($"Remade integration tests ({(quick ? "quick" : "full")}), build {Log.BuildId}, {Environment.ProcessorCount} cpus");

// ---------------------------------------------------------------- planet
Planet planet = null;
foreach (int f in quick ? new[] { 64 } : new[] { 64, 128 })
{
    double ms = Ms(() => planet = new Planet(new WorldParams { Seed = 777, Frequency = f }));
    int start = planet.FindStartTile();
    Check($"planet f={f} ({planet.TileCount} tiles)", ms < (f <= 64 ? 1500 : 5000) && start >= 0 && planet.CountRivers() > 0,
        $"{ms:F0} ms, land {planet.LandFraction():P0}, rivers {planet.CountRivers()}, temperate {planet.Count(Biome.TemperateForest)}, working set {WorkingSetMb()} MB");
}
planet = new Planet(new WorldParams { Seed = 777, Frequency = 64 });

// ---------------------------------------------------------------- climate over a year
{
    var times = new List<double>();
    int tile = planet.FindStartTile();
    float minT = float.MaxValue, maxT = float.MinValue;
    for (long h = 1; h <= 24L * GameTime.DaysPerYear; h++)
    {
        times.Add(Ms(() => planet.Climate.Update(h * GameTime.TicksPerHour)));
        float t = planet.Climate.Temperature[tile];
        minT = MathF.Min(minT, t); maxT = MathF.Max(maxT, t);
    }
    Check("climate: one year of hourly planet updates", times.Average() < 15 && times.Max() < 80 && maxT - minT > 6,
        $"avg {times.Average():F2} ms, max {times.Max():F1} ms per update; start tile ranged {minT:F1}..{maxT:F1} °C over the year");
}

// ---------------------------------------------------------------- map generation at scale
var sizes = quick ? new[] { 300, 1000 } : new[] { 300, 600, 1000, 1500 };
foreach (int size in sizes)
{
    GC.Collect();
    long before = GC.GetTotalMemory(true);
    MapGen.Result res = null;
    int tile = planet.FindStartTile();
    double ms = Ms(() => res = MapGen.Generate(planet, tile, size, 31));
    long mem = (GC.GetTotalMemory(false) - before) / 1048576;
    int oaks = res.Map.Plants.Count(p => p == Plant.Oak);
    double budget = size * (double)size / 1_000_000 * 900 + 150;
    Check($"map generation {size}x{size}", ms < budget && res.CabinDoor >= 0,
        $"{ms:F0} ms (budget {budget:F0}), +{mem} MB managed, {oaks} oaks");
}

// ---------------------------------------------------------------- simulation load
GameSim sim = null;
{
    int id = 1;
    var pawns = PawnGenerator.GenerateGroup(3, 5, () => id++);
    int size = quick ? 600 : 1000;
    var colonyPlanet = new Planet(new WorldParams { Seed = 777, Frequency = 64 }); // the year-long climate test advanced the other one
    double ms = Ms(() => sim = GameSim.NewColony(colonyPlanet, colonyPlanet.FindStartTile(), size, pawns, 42, GameTime.TicksPerHour * 7));
    Check($"new colony {size}x{size}", ms < 4000, $"{ms:F0} ms, {sim.Animals.Count} deer in {sim.Herds.Count} herds, working set {WorkingSetMb()} MB");
    var tickTimes = new List<double>();
    var sw = new Stopwatch();
    int ticks = quick ? GameTime.TicksPerDay / 4 : GameTime.TicksPerDay;
    for (int i = 0; i < ticks; i++)
    {
        sw.Restart();
        sim.Step();
        tickTimes.Add(sw.Elapsed.TotalMilliseconds);
    }
    tickTimes.Sort();
    double avg = tickTimes.Average(), p99 = tickTimes[(int)(tickTimes.Count * 0.99)], max = tickTimes[^1];
    // at Ultra speed (15x) the game needs 900 ticks per real second: budget 14 ms/frame at 60 fps → 15 ticks/frame
    double ultraTicksPerSec = 1000.0 / avg;
    Check($"simulation {ticks} ticks with {sim.Animals.Count} deer", avg < 0.25 && p99 < 2.5,
        $"avg {avg * 1000:F0} µs, p99 {p99:F2} ms, max {max:F1} ms per tick → {ultraTicksPerSec:F0} ticks/s possible (Ultra needs 900)");
    Check("colonists survive a day autonomously", sim.Pawns.All(p => !p.Dead && p.Needs.Thirst > 0.02f),
        string.Join(", ", sim.Pawns.Select(p => $"{p.Name}: food {p.Needs.Food:P0} thirst {p.Needs.Thirst:P0} rest {p.Needs.Rest:P0}")));
}

// ---------------------------------------------------------------- pathfinding stress
{
    var rng = new Rng(9);
    var path = new List<Vector2>();
    var times = new List<double>();
    int found = 0, unreachable = 0;
    int n = quick ? 300 : 1500;
    var map = sim.Map;
    for (int k = 0; k < n; k++)
    {
        int a = map.CellAt(new Vector2(rng.Range(1f, map.Width - 1), rng.Range(1f, map.Height - 1)));
        int b = map.CellAt(new Vector2(rng.Range(1f, map.Width - 1), rng.Range(1f, map.Height - 1)));
        if (sim.Paths.Cost[a] == 0 || sim.Paths.Cost[b] == 0) continue;
        PathResult r = PathResult.Unreachable;
        times.Add(Ms(() => r = sim.Pathfinder.FindPath(a, b, path)));
        if (r == PathResult.Found) found++; else unreachable++;
    }
    times.Sort();
    Check($"pathfinding: {times.Count} random cross-map paths on {map.Width}x{map.Height}", times.Average() < 12 && times[(int)(times.Count * 0.95)] < 40,
        $"avg {times.Average():F2} ms, p95 {times[(int)(times.Count * 0.95)]:F1} ms, max {times[^1]:F1} ms; {found} found, {unreachable} unreachable (rejected instantly)");
}

// ---------------------------------------------------------------- save / load at scale
{
    string path = Path.Combine(Path.GetTempPath(), "remade_integration.rsav");
    double wms = Ms(() => SaveGame.Write(sim, path));
    long kb = new FileInfo(path).Length / 1024;
    GameSim loaded = null;
    double rms = Ms(() => loaded = SaveGame.Read(path));
    bool same = loaded.Tick == sim.Tick && loaded.Map.Terrain.SequenceEqual(sim.Map.Terrain) && loaded.Animals.Count == sim.Animals.Count;
    for (int i = 0; i < 600; i++) { sim.Step(); loaded.Step(); }
    bool deterministic = sim.Animals.Select(a => a.Position).SequenceEqual(loaded.Animals.Select(a => a.Position))
                         && sim.Pawns.Select(p => p.Position).SequenceEqual(loaded.Pawns.Select(p => p.Position));
    Check($"save/load {sim.Map.Width}x{sim.Map.Height}", wms < 2500 && rms < 5000 && same && deterministic,
        $"save {wms:F0} ms ({kb} KiB), load {rms:F0} ms, identical={same}, continues identically={deterministic}");
    File.Delete(path);
}

// ---------------------------------------------------------------- the goal scenario at scale
{
    int id = 1;
    var pawns = PawnGenerator.GenerateGroup(3, 8, () => id++);
    var g = GameSim.NewColony(new Planet(new WorldParams { Seed = 777, Frequency = 64 }), planet.FindStartTile(), 600, pawns, 77, GameTime.TicksPerHour * 9);
    var p = g.Pawns[0];
    p.Skills[(int)SkillId.Shooting] = 10;
    g.SetMode(p, ControlMode.Direct);
    string failure = null;
    try
    {
        var bow = g.Items.First(i => i.Def == Defs.Bow);
        int door = Array.IndexOf(g.Map.Buildings, Building.Door);
        Walk(g, p, g.Map.CellCenter(g.InteractionSpot(door, p.Position)), 0.3f);
        Interactions.Execute(g, p, Interactions.Nearby(g, p).First(i => i.Kind == InteractionKind.OpenDoor));
        Run(g, 40);
        Walk(g, p, bow.Position, 0.8f);
        Interactions.Execute(g, p, Interactions.Nearby(g, p).First(i => i.Item == bow));
        Run(g, 60);
        foreach (var arrows in g.Items.Where(i => i.Def == Defs.Arrow).ToList())
        {
            Walk(g, p, arrows.Position, 0.8f);
            Interactions.Execute(g, p, Interactions.Nearby(g, p).First(i => i.Item == arrows));
            Run(g, 60);
        }
        Walk(g, p, g.FindStandableNear(new Vector2(300, 300), 10), 0.5f);
        var deer = g.Animals.OrderBy(a => Vector2.Distance(a.Position, p.Position)).First();
        int shots = 0;
        while (!deer.Dead && p.Arrows > 0 && shots < 40)
        {
            if (Vector2.Distance(deer.Position, p.Position) > 14f || !g.LineOfSight(p.Position, deer.Position))
            {
                for (int k = 0; k < 40; k++)
                {
                    var s = g.FindStandableNear(p.Position + new Vector2(MathF.Cos(k), MathF.Sin(k)) * 8f, 4);
                    if (g.LineOfSight(p.Position, s)) { deer.Position = s; deer.Path.Clear(); deer.PathIndex = 0; break; }
                }
            }
            g.Input.Aim = true; g.Input.AimPoint = deer.Position; g.Input.Fire = true;
            Run(g, 75); shots++;
        }
        g.Input.Aim = false;
        if (!deer.Dead) failure = $"deer alive after {shots} shots";
        else
        {
            var meat = g.Items.Where(i => i.Def == Defs.Venison).OrderBy(i => Vector2.Distance(i.Position, p.Position)).First();
            Walk(g, p, meat.Position, 0.8f);
            Interactions.Execute(g, p, Interactions.Nearby(g, p).First(i => i.Item == meat));
            Run(g, 60);
            if (p.CountInInventory(Defs.Venison) == 0) failure = "meat not picked up";
        }
        Check("goal scenario on a 600x600 map (bow → deer → meat)", failure == null,
            failure ?? $"bow {p.Weapon?.Def.Id}, {p.Arrows} arrows left after {shots} shots, carrying {p.CountInInventory(Defs.Venison)} venison, load {p.CarriedMass:F1} kg");
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        Log.Exception(ex, "goal scenario");
        Check("goal scenario on a 600x600 map (bow → deer → meat)", false, ex.Message);
    }
}

// ---------------------------------------------------------------- report
int failed = results.Count(r => !r.ok);
string report = Path.Combine(root, "test-results", $"integration_{DateTime.Now:yyyyMMdd_HHmmss}.md");
using (var w = new StreamWriter(report))
{
    w.WriteLine($"# Integration tests (part 1) — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    w.WriteLine();
    w.WriteLine($"Build {Log.BuildId} · {Environment.ProcessorCount} CPUs · {(quick ? "quick" : "full")} run · peak working set {proc.PeakWorkingSet64 / 1048576} MB");
    w.WriteLine();
    w.WriteLine("| Result | Test | Details |");
    w.WriteLine("|---|---|---|");
    foreach (var (name, ok, detail) in results) w.WriteLine($"| {(ok ? "PASS" : "**FAIL**")} | {name} | {detail} |");
}
Console.WriteLine($"\n{results.Count - failed}/{results.Count} passed. Report: {report}");
Log.Info($"INTEGRATION RESULT: {(failed == 0 ? "PASS" : "FAIL")} ({failed} failures)");
Log.Shutdown();
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers

static void Run(GameSim g, int ticks) { for (int i = 0; i < ticks; i++) g.Step(); }

static void Walk(GameSim g, Pawn p, Vector2 target, float stop)
{
    var path = new List<Vector2>();
    int goal = g.Map.CellAt(target);
    if (g.Paths.Cost[goal] == 0 || g.Map.Buildings[goal] == Building.Door) goal = g.InteractionSpot(goal, p.Position);
    if (g.Pathfinder.FindPath(g.Map.CellAt(p.Position), goal, path) != PathResult.Found) throw new InvalidOperationException($"no path to {target}");
    path.Add(target);
    int idx = 0;
    for (int t = 0; t < 40000 && Vector2.Distance(p.Position, target) > stop; t++)
    {
        while (idx < path.Count - 1 && Vector2.Distance(p.Position, path[idx]) < 0.2f) idx++;
        var d = path[idx] - p.Position;
        g.Input.Move = d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.Zero;
        g.Step();
    }
    g.Input.Move = Vector2.Zero;
    if (Vector2.Distance(p.Position, target) > stop + 0.05f) throw new InvalidOperationException($"could not walk to {target}, stuck at {p.Position}");
}

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "DEVLOG.md"))) d = d.Parent;
    return d?.FullName ?? throw new DirectoryNotFoundException("project root (DEVLOG.md) not found above " + AppContext.BaseDirectory);
}
