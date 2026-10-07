using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Pawns;
using Remade.Things;
using Remade.World;

namespace Remade.Sim;

/// <summary>
/// The whole running game: planet + current map + entities, advanced in fixed ticks (60 per second at 1x).
/// Engine-independent; the Godot layer reads its state and feeds <see cref="Input"/>.
/// </summary>
public sealed partial class GameSim
{
    public readonly Planet Planet;
    public readonly LocalMap Map;
    public readonly PathGrid Paths;
    public readonly Pathfinder Pathfinder;
    public readonly LocalWeather Weather = new();
    public string ColonyName = "New Hope";

    public long Tick { get; private set; }
    public int SpeedIndex = 1;
    public Rng Rng;
    int _nextId = 1;

    public readonly List<Pawn> Pawns = new();
    public readonly List<Animal> Animals = new();
    public readonly List<Herd> Herds = new();
    public readonly List<Item> Items = new();          // spawned ground items
    public readonly List<Projectile> Projectiles = new();
    readonly Dictionary<int, List<Item>> _itemsByCell = new();

    /// <summary>Events since the game layer last cleared them (sounds, effects, messages).</summary>
    public readonly List<SimEvent> Events = new();
    /// <summary>Map changes since the game layer last consumed them (renderer).</summary>
    public readonly List<CellChange> FrameChanges = new();
    readonly List<CellChange> _tickChanges = new();

    public readonly DirectInput Input = new();
    /// <summary>Points of interest (camera focus) for simulation level of detail.</summary>
    public Vector2 Focus;

    double _tickAccumulator;
    public double LastStepMs { get; private set; }
    public int LastTicksThisFrame { get; private set; }
    bool _warnedBudget;
    readonly Dictionary<int, int> _autoDoors = new(); // door cell → ticks until auto-close

    public float Latitude => Planet.Grid.Latitude(Map.PlanetTile);

    public GameSim(Planet planet, LocalMap map, int seed)
    {
        Planet = planet;
        Map = map;
        Paths = new PathGrid(map);
        Pathfinder = new Pathfinder(Paths);
        Rng = Rng.FromParts(seed, 99);
        Log.MapId = map.Id;
    }

    public int NewId() => _nextId++;
    public int PeekNextId => _nextId;
    internal void SetNextId(int v) => _nextId = v;
    internal void SetTick(long t) { Tick = t; Log.Tick = t; }

    // ------------------------------------------------------------------ new game

    /// <summary>Creates a fresh colony: map from the planet tile, colonists at the landing site, loot in the cabin, deer herds.</summary>
    public static GameSim NewColony(Planet planet, int tile, int mapSize, List<Pawn> colonists, int seed, long startTick)
    {
        using var corr = Log.Correlate("NewColony");
        if (!BiomeInfo.Playable(planet.Biomes[tile]))
            throw new InvalidOperationException($"Tile {tile} ({planet.Biomes[tile]}) is not playable in this version");
        if (colonists == null || colonists.Count == 0) throw new ArgumentException("at least one colonist is required");
        // the planet and every map on it share one clock
        if (startTick < planet.Climate.LastUpdateTick)
            throw new ArgumentException($"Colony start tick {startTick} is before the planet's current time {planet.Climate.LastUpdateTick}");
        planet.Climate.Update(startTick);
        var gen = MapGen.Generate(planet, tile, mapSize, seed);
        var sim = new GameSim(planet, gen.Map, seed);
        sim.Tick = startTick;
        Log.Tick = startTick;
        int maxId = 0;
        foreach (var p in colonists) maxId = Math.Max(maxId, MaxIdOf(p));
        sim._nextId = maxId + 1;

        // colonists around the landing spot
        for (int i = 0; i < colonists.Count; i++)
        {
            var p = colonists[i];
            Vector2 spot = sim.FindStandableNear(gen.LandingSpot + new Vector2((i - colonists.Count / 2f) * 1.6f, 1.5f), 6);
            p.Position = spot;
            p.Facing = MathF.PI * 0.5f;
            p.Mode = ControlMode.Autonomous;
            sim.Pawns.Add(p);
            Log.Info($"Spawned colonist {p.FullName} #{p.Id} at {spot}");
        }

        // starting loot inside the cabin: a bow and a quiver of arrows, plus a few berries
        if (gen.CabinInterior.Count >= 3)
        {
            sim.SpawnItem(Defs.Bow, 1, sim.Map.CellCenter(gen.CabinInterior[0]));
            sim.SpawnItem(Defs.Arrow, 24, sim.Map.CellCenter(gen.CabinInterior[1]));
            sim.SpawnItem(Defs.Arrow, 12, sim.Map.CellCenter(gen.CabinInterior[^1]));
        }
        else throw new InvalidOperationException("MapGen produced a cabin without interior cells");

        sim.SpawnHerds(gen.LandingSpot);
        sim.Weather.Update(planet, tile, startTick, snap: true);
        sim.Map.ClearChanges();
        Log.GameState = "playing";
        Log.Info($"Colony ready on {sim.Map.Id}: {sim.Pawns.Count} colonists, {sim.Animals.Count} deer in {sim.Herds.Count} herds, " +
                 $"{sim.Items.Count} items, weather {sim.Weather.Describe()} {sim.Weather.Temperature:F1}°C");
        return sim;
    }

    static int MaxIdOf(Pawn p)
    {
        int m = p.Id;
        foreach (var a in p.Apparel) m = Math.Max(m, a.Id);
        foreach (var e in p.Inventory.Entries) m = Math.Max(m, e.Item.Id);
        if (p.Held != null) m = Math.Max(m, p.Held.Id);
        return m;
    }

    void SpawnHerds(Vector2 avoid)
    {
        int area = Map.Width * Map.Height;
        int herdCount = Math.Max(3, (int)(area / 16000f));
        int made = 0;
        for (int attempt = 0; attempt < herdCount * 20 && made < herdCount; attempt++)
        {
            var c = new Vector2(Rng.Range(8f, Map.Width - 8f), Rng.Range(8f, Map.Height - 8f));
            if (Vector2.Distance(c, avoid) < 30f) continue;
            int cell = Map.CellAt(c);
            if (cell < 0 || Map.Blocked(cell) || Map.TerrainAt(cell).Water) continue;
            var herd = new Herd { Id = Herds.Count, Anchor = c, MoveTimer = Rng.Range(2000, 8000) };
            Herds.Add(herd);
            int n = Rng.Range(2, 7);
            for (int k = 0; k < n; k++)
            {
                var pos = FindStandableNear(c + new Vector2(Rng.Range(-4f, 4f), Rng.Range(-4f, 4f)), 6);
                var a = new Animal
                {
                    Id = NewId(), Position = pos, Herd = herd.Id, Male = Rng.Chance(0.35f), Size = Rng.Range(0.85f, 1.12f),
                    Facing = Rng.Range(0f, MathF.Tau), State = AnimalState.Graze, StateTimer = Rng.Range(60, 600), ThinkTimer = Rng.Range(0, 60),
                };
                if (a.Male) a.Size *= 1.1f;
                Animals.Add(a);
            }
            made++;
        }
    }

    /// <summary>Nearest standable cell centre to a point, searching outward up to a radius.</summary>
    public Vector2 FindStandableNear(Vector2 p, int radius)
        => TryFindStandableNear(p, radius, out var s) ? s : throw new InvalidOperationException($"No standable cell within {radius} of {p}");

    /// <summary>Nearest dry, open cell (ring by ring); false when there is none within the radius (all water or rock).</summary>
    public bool TryFindStandableNear(Vector2 p, int radius, out Vector2 spot)
    {
        int cx = (int)p.X, cy = (int)p.Y;
        for (int r = 0; r <= radius; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = cx + dx, y = cy + dy;
                    if (!Map.InBounds(x, y)) continue;
                    int c = Map.Index(x, y);
                    if (!Map.Blocked(c) && !Map.TerrainAt(c).Water && Map.Plants[c] != Plant.Oak) { spot = r == 0 ? p : LocalMap.CellCenter(x, y); return true; }
                }
        spot = default;
        return false;
    }

    // ------------------------------------------------------------------ items

    public Item SpawnItem(ThingDef def, int count, Vector2 pos)
    {
        Item first = null;
        while (count > 0)
        {
            int n = Math.Min(count, def.StackLimit);
            var it = new Item(NewId(), def, n);
            PlaceOnGround(it, pos);
            first ??= it;
            count -= n;
            pos += new Vector2(Rng.Range(-0.6f, 0.6f), Rng.Range(-0.6f, 0.6f));
        }
        return first;
    }

    public void PlaceOnGround(Item it, Vector2 pos)
    {
        Invariant.Check(!it.Spawned, $"{it} is already on the ground");
        int cell = Map.CellAt(pos);
        if (cell < 0 || Map.Blocked(cell) || Map.TerrainAt(cell).Deep)
        {
            pos = FindStandableNear(new Vector2(Math.Clamp(pos.X, 1, Map.Width - 2), Math.Clamp(pos.Y, 1, Map.Height - 2)), 8);
            cell = Map.CellAt(pos);
        }
        it.Spawned = true;
        it.Position = pos;
        it.Rotation = Rng.Range(0f, MathF.Tau);
        Items.Add(it);
        if (!_itemsByCell.TryGetValue(cell, out var list)) _itemsByCell[cell] = list = new List<Item>(2);
        list.Add(it);
        Events.Add(new SimEvent(SimEventKind.ItemSpawned, pos, null, it.Id));
    }

    public void Despawn(Item it)
    {
        Invariant.Check(it.Spawned, $"{it} is not on the ground");
        int cell = Map.CellAt(it.Position);
        bool removed = Items.Remove(it) & _itemsByCell.TryGetValue(cell, out var list) && list.Remove(it);
        Invariant.Check(removed, $"{it} missing from the item index at cell {cell}");
        if (list.Count == 0) _itemsByCell.Remove(cell);
        it.Spawned = false;
        Events.Add(new SimEvent(SimEventKind.ItemDespawned, it.Position, null, it.Id));
    }

    public IReadOnlyList<Item> ItemsAt(int cell) => _itemsByCell.TryGetValue(cell, out var l) ? l : Array.Empty<Item>();

    public IEnumerable<Item> ItemsNear(Vector2 p, float radius)
    {
        int r = (int)MathF.Ceiling(radius);
        int cx = (int)p.X, cy = (int)p.Y;
        for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (!Map.InBounds(x, y)) continue;
                if (!_itemsByCell.TryGetValue(Map.Index(x, y), out var l)) continue;
                foreach (var it in l) if (Vector2.Distance(it.Position, p) <= radius) yield return it;
            }
    }

    // ------------------------------------------------------------------ time

    public int SpeedMultiplier => GameTime.SpeedMultipliers[SpeedIndex];

    /// <summary>
    /// Advances the simulation by real time at the current speed, within a per-frame time budget.
    /// If the budget is exceeded the game runs slower than requested (logged once) rather than freezing.
    /// </summary>
    public void Advance(double realSeconds, double budgetMs = 14)
    {
        LastTicksThisFrame = 0;
        if (SpeedIndex == 0) { _tickAccumulator = 0; return; }
        _tickAccumulator += realSeconds * GameTime.TicksPerSecondAt1x * SpeedMultiplier;
        // never try to catch up more than a quarter second of game time after a hitch
        _tickAccumulator = Math.Min(_tickAccumulator, GameTime.TicksPerSecondAt1x * SpeedMultiplier * 0.25);
        var sw = Stopwatch.StartNew();
        while (_tickAccumulator >= 1)
        {
            Step();
            _tickAccumulator -= 1;
            LastTicksThisFrame++;
            if (sw.Elapsed.TotalMilliseconds > budgetMs)
            {
                if (!_warnedBudget)
                {
                    _warnedBudget = true;
                    Log.Warn($"Simulation over budget at speed {SpeedMultiplier}x: {LastTicksThisFrame} ticks took {sw.Elapsed.TotalMilliseconds:F1} ms; running slower than requested");
                }
                _tickAccumulator = Math.Min(_tickAccumulator, 1);
                break;
            }
        }
        LastStepMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>One simulation tick.</summary>
    public void Step()
    {
        Tick++;
        Log.Tick = Tick;
        if (Tick % GameTime.TicksPerHour == 0) Planet.Climate.Update(Tick);
        if (Tick % LocalWeather.UpdateInterval == 0) Weather.Update(Planet, Map.PlanetTile, Tick, snap: false);
        if (Tick % GameTime.TicksPerDay == 0) DailyUpdate();

        for (int i = 0; i < Pawns.Count; i++) TickPawn(Pawns[i]);
        for (int i = 0; i < Animals.Count; i++) TickAnimal(Animals[i]);
        PurgeCorpses();
        TickProjectiles();
        TickDoors();
        Input.Fire = false;

        Map.DrainChanges(_tickChanges);
        if (_tickChanges.Count > 0)
        {
            Paths.Apply(_tickChanges);
            FrameChanges.AddRange(_tickChanges);
            _tickChanges.Clear();
        }
    }

    void DailyUpdate()
    {
        // berry bushes regrow outside winter
        var season = GameTime.SeasonAt(Tick, Latitude);
        if (season == Season.Winter) return;
        for (int i = 0; i < Map.CellCount; i++)
        {
            if (Map.Plants[i] != Plant.BerryBush || Map.Berries[i] >= PlantInfo.MaxBerries) continue;
            Map.SetBerries(i, (byte)Math.Min(PlantInfo.MaxBerries, Map.Berries[i] + 2));
        }
    }

    void TickDoors()
    {
        if (_autoDoors.Count == 0) return;
        List<int> close = null;
        foreach (var kv in _autoDoors)
        {
            int cell = kv.Key;
            bool occupied = false;
            var c = Map.CellCenter(cell);
            foreach (var p in Pawns) if (Vector2.DistanceSquared(p.Position, c) < 0.8f) occupied = true;
            if (occupied) { _autoDoors[cell] = 40; continue; }
            if (kv.Value <= 0) (close ??= new List<int>()).Add(cell);
        }
        if (close != null)
            foreach (int cell in close) { _autoDoors.Remove(cell); if (Map.DoorOpen[cell]) Map.SetDoor(cell, false); }
        if (_autoDoors.Count > 0)
            foreach (int k in new List<int>(_autoDoors.Keys)) if (_autoDoors.ContainsKey(k)) _autoDoors[k]--;
    }

    internal Dictionary<int, int> AutoDoorsSnapshot() => new(_autoDoors);
    internal void RestoreAutoDoor(int cell, int timer) => _autoDoors[cell] = timer;

    /// <summary>
    /// AI pawns open doors on their way and the door swings shut behind them. A door someone deliberately left open
    /// (opened with E / the menu) stays open: only doors opened here are scheduled to close.
    /// </summary>
    internal void AutoOpenDoor(int cell)
    {
        if (!Map.DoorOpen[cell])
        {
            Map.SetDoor(cell, true);
            Events.Add(new SimEvent(SimEventKind.DoorToggled, Map.CellCenter(cell), "open", cell));
            _autoDoors[cell] = 40;
        }
        else if (_autoDoors.ContainsKey(cell)) _autoDoors[cell] = 40;
    }

    public void Message(string text, Vector2 pos)
    {
        Events.Add(new SimEvent(SimEventKind.Message, pos, text));
        Log.Info("message: " + text);
    }

    public Pawn Controlled
    {
        get { foreach (var p in Pawns) if (p.Mode == ControlMode.Direct) return p; return null; }
    }

    /// <summary>Switches a pawn's control mode. Only one pawn can be under direct control at a time.</summary>
    public void SetMode(Pawn p, ControlMode mode)
    {
        if (p.Dead) throw new InvalidOperationException($"{p} is dead");
        if (mode == ControlMode.Direct)
            foreach (var other in Pawns)
                if (other != p && other.Mode == ControlMode.Direct) { other.Mode = ControlMode.Autonomous; ClearJob(other); }
        if (p.Mode == mode) return;
        Log.Info($"{p.FullName}: control {p.Mode} -> {mode}");
        p.Mode = mode;
        p.Aiming = false;
        ClearJob(p);
    }
}
