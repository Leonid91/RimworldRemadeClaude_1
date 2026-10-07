using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remade.Core;
using Remade.Map;
using Remade.Pawns;
using Remade.Save;
using Remade.Sim;
using Remade.Things;
using Remade.World;

namespace Remade.Tests;

internal static class Fixtures
{
    static readonly Lazy<Planet> _planet = new(() => new Planet(new WorldParams { Seed = 42, Frequency = 32 }));
    public static Planet Planet => _planet.Value;

    public static int TemperateTile(Func<Planet, int, bool> extra = null)
    {
        var p = Planet;
        for (int i = 0; i < p.TileCount; i++)
            if (p.Biomes[i] == Biome.TemperateForest && p.Hills[i] != Hilliness.Impassable && (extra == null || extra(p, i))) return i;
        throw new InvalidOperationException("no matching temperate tile");
    }

    /// <summary>A fresh game on its own planet (the climate is mutable, so sims never share a planet).</summary>
    public static GameSim NewSim(int size = 96, int seed = 5, Func<Planet, int, bool> tileFilter = null)
    {
        var planet = new Planet(new WorldParams { Seed = 42, Frequency = 32 });
        int tile = TemperateTile(tileFilter);
        int id = 1;
        var pawns = PawnGenerator.GenerateGroup(3, seed, () => id++);
        return GameSim.NewColony(planet, tile, size, pawns, seed, GameTime.TicksPerHour * 8);
    }

    /// <summary>An open test map (all soil) with a given size; no planet features.</summary>
    public static LocalMap OpenMap(int w = 40, int h = 40)
    {
        var m = new LocalMap(w, h, 0, 1);
        return m;
    }
}

public class MapGenTests
{
    [Fact]
    public void GenerationIsDeterministic()
    {
        int tile = Fixtures.TemperateTile();
        var a = MapGen.Generate(Fixtures.Planet, tile, 80, 9).Map;
        var b = MapGen.Generate(Fixtures.Planet, tile, 80, 9).Map;
        Assert.Equal(a.Terrain, b.Terrain);
        Assert.Equal(a.Buildings, b.Buildings);
        Assert.Equal(a.Plants, b.Plants);
        Assert.Equal(a.Ground, b.Ground);
    }

    [Fact]
    public void CabinWithClosedDoorIsPlaced()
    {
        var res = MapGen.Generate(Fixtures.Planet, Fixtures.TemperateTile(), 96, 3);
        Assert.True(res.CabinDoor >= 0);
        Assert.Equal(Building.Door, res.Map.Buildings[res.CabinDoor]);
        Assert.False(res.Map.DoorOpen[res.CabinDoor]);
        Assert.True(res.CabinInterior.Count >= 6);
        foreach (int c in res.CabinInterior) Assert.Equal(Plant.None, res.Map.Plants[c]);
    }

    [Fact]
    public void RiverTileHasFreshWaterCrossingTheMap()
    {
        int tile = Fixtures.TemperateTile((p, t) => p.RiverSize[t] >= 1);
        var m = MapGen.Generate(Fixtures.Planet, tile, 128, 4).Map;
        int water = 0;
        for (int i = 0; i < m.CellCount; i++) if (m.IsFreshWater(i)) water++;
        Assert.True(water > 128 * 2, $"only {water} river cells");
        // river cells touch at least two different map edges (enters and leaves)
        int edges = 0;
        bool Any(Func<int, int> cellOf) { for (int k = 0; k < 128; k++) if (m.IsFreshWater(cellOf(k))) return true; return false; }
        if (Any(k => m.Index(k, 0))) edges++;
        if (Any(k => m.Index(k, 127))) edges++;
        if (Any(k => m.Index(0, k))) edges++;
        if (Any(k => m.Index(127, k))) edges++;
        Assert.True(edges >= 2, $"river touches {edges} edges");
        // water surface is above the riverbed
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
                if (m.IsFreshWater(m.Index(x, y))) Assert.True(m.GroundHeight(x + 0.5f, y + 0.5f) < LocalMap.WaterLevel + 0.3f);
    }

    [Fact]
    public void CoastalTileHasSeaOnTheSeaSide()
    {
        int tile = Fixtures.TemperateTile((p, t) => p.Coast[t]);
        var feat = TileFeatures.From(Fixtures.Planet, tile);
        Assert.NotEmpty(feat.SeaDirs);
        var m = MapGen.Generate(feat, tile, 128, 2).Map;
        Vector2 dir = Vector2.Normalize(feat.SeaDirs.Aggregate(Vector2.Zero, (a, b) => a + b) + new Vector2(1e-4f, 0));
        int seaSide = 0, landSide = 0;
        for (int i = 0; i < m.CellCount; i++)
        {
            if (m.Terrain[i] is not (Terrain.OceanShallow or Terrain.OceanDeep)) continue;
            float proj = Vector2.Dot(m.CellCenter(i) - new Vector2(64, 64), dir);
            if (proj > 0) seaSide++; else landSide++;
        }
        Assert.True(seaSide > 200, $"sea cells on the sea side: {seaSide}");
        Assert.True(seaSide > landSide * 3);
    }

    [Fact]
    public void OnlyGraniteRockAndItIsMinable()
    {
        var m = MapGen.Generate(Fixtures.Planet, Fixtures.TemperateTile((p, t) => p.Hills[t] >= Hilliness.SmallHills), 128, 8).Map;
        int rock = 0;
        for (int i = 0; i < m.CellCount; i++)
        {
            if (m.Buildings[i] == Building.Granite) { rock++; Assert.True(m.RockHeight[i] > 2f); }
            Assert.True(m.Buildings[i] is Building.None or Building.Granite or Building.WoodWall or Building.Door);
        }
        Assert.True(rock > 0);
        int cell = Array.IndexOf(m.Buildings, Building.Granite);
        m.MineOut(cell);
        Assert.Equal(Building.None, m.Buildings[cell]);
        Assert.Equal(Terrain.RoughGranite, m.Terrain[cell]);
        Assert.True(m.ChangeCount > 0);
    }

    [Fact]
    public void ManyOakVariantsAndBerryBushesExist()
    {
        var m = MapGen.Generate(Fixtures.Planet, Fixtures.TemperateTile(), 160, 6).Map;
        var variants = new HashSet<byte>();
        int oaks = 0, bushes = 0;
        for (int i = 0; i < m.CellCount; i++)
        {
            if (m.Plants[i] == Plant.Oak) { oaks++; variants.Add(m.PlantVariant[i]); }
            if (m.Plants[i] == Plant.BerryBush) bushes++;
        }
        Assert.True(oaks > 300, $"{oaks} oaks");
        Assert.True(variants.Count >= 8, $"{variants.Count} oak variants");
        Assert.True(bushes >= 1);
    }
}

public class PathfinderTests
{
    static LocalMap WallMap()
    {
        var m = Fixtures.OpenMap(40, 40);
        for (int y = 0; y < 35; y++) m.Buildings[m.Index(20, y)] = Building.WoodWall; // wall with a gap at the bottom
        return m;
    }

    [Fact]
    public void FindsPathAroundWall()
    {
        var m = WallMap();
        var pf = new Pathfinder(new PathGrid(m));
        var path = new List<Vector2>();
        Assert.Equal(PathResult.Found, pf.FindPath(m.Index(10, 5), m.Index(30, 5), path));
        Assert.True(path.Any(p => p.Y > 34), "path must go through the gap");
        Assert.Equal(new Vector2(30.5f, 5.5f), path[^1]);
        // every straight leg of the smoothed path is clear
        var grid = new PathGrid(m);
        var prev = m.Index(10, 5);
        foreach (var wp in path) { int c = m.CellAt(wp); Assert.True(grid.ClearLine(prev, c)); prev = c; }
    }

    [Fact]
    public void UnreachableIsRejectedWithoutSearching()
    {
        var m = WallMap();
        for (int y = 35; y < 40; y++) m.Buildings[m.Index(20, y)] = Building.WoodWall; // close the gap
        var pf = new Pathfinder(new PathGrid(m));
        var path = new List<Vector2>();
        long before = pf.Expanded;
        Assert.Equal(PathResult.Unreachable, pf.FindPath(m.Index(10, 5), m.Index(30, 5), path));
        Assert.Equal(before, pf.Expanded);
    }

    [Fact]
    public void NoCornerCutting()
    {
        var m = Fixtures.OpenMap(10, 10);
        m.Buildings[m.Index(5, 4)] = Building.WoodWall;
        m.Buildings[m.Index(4, 5)] = Building.WoodWall;
        var grid = new PathGrid(m);
        Assert.False(grid.ClearLine(m.Index(4, 4), m.Index(5, 5)));
        var pf = new Pathfinder(grid);
        var path = new List<Vector2>();
        Assert.Equal(PathResult.Found, pf.FindPath(m.Index(4, 4), m.Index(5, 5), path));
        Assert.True(path.Count >= 2, "must walk around the diagonal gap");
    }

    [Fact]
    public void ClosedDoorsBlockAnimalsButNotColonists()
    {
        var m = Fixtures.OpenMap(20, 20);
        for (int y = 0; y < 20; y++) m.Buildings[m.Index(10, y)] = Building.WoodWall;
        m.SetBuilding(m.Index(10, 10), Building.Door);
        var pf = new Pathfinder(new PathGrid(m));
        var path = new List<Vector2>();
        Assert.Equal(PathResult.Found, pf.FindPath(m.Index(2, 2), m.Index(18, 18), path));
        Assert.Equal(PathResult.Unreachable, pf.FindPath(m.Index(2, 2), m.Index(18, 18), path, doorsBlock: true));
        m.SetDoor(m.Index(10, 10), true);
        Assert.Equal(PathResult.Found, pf.FindPath(m.Index(2, 2), m.Index(18, 18), path, doorsBlock: true));
    }

    [Fact]
    public void ComponentsUpdateWhenRockIsMined()
    {
        var m = Fixtures.OpenMap(20, 20);
        for (int y = 0; y < 20; y++) { m.Buildings[m.Index(10, y)] = Building.Granite; m.Terrain[m.Index(10, y)] = Terrain.RoughGranite; }
        var grid = new PathGrid(m);
        Assert.False(grid.Reachable(m.Index(2, 2), m.Index(18, 2)));
        m.MineOut(m.Index(10, 7));
        var ch = new List<CellChange>();
        m.DrainChanges(ch);
        grid.Apply(ch);
        Assert.True(grid.Reachable(m.Index(2, 2), m.Index(18, 2)));
    }
}

public class InventoryTests
{
    int _id = 1;
    int NewId() => _id++;

    [Fact]
    public void StacksMergeThenFillFreeSpots()
    {
        var g = new InventoryGrid("test", 4, 4);
        var a = new Item(NewId(), Defs.Venison, 6);
        Assert.Equal(6, g.Absorb(a, NewId));
        Assert.Equal(0, a.Count);
        var b = new Item(NewId(), Defs.Venison, 7);
        Assert.Equal(7, g.Absorb(b, NewId));
        Assert.Equal(13, g.CountOf(Defs.Venison));
        Assert.Equal(2, g.Entries.Count); // 10 + 3
    }

    [Fact]
    public void LongItemsRotateToFit()
    {
        var g = new InventoryGrid("wide", 4, 2);
        Assert.True(g.TryInsert(new Item(NewId(), Defs.Bow, 1))); // 1x4 only fits rotated (4x1)
        Assert.True(g.Entries[0].Rotated);
        Assert.False(g.TryInsert(new Item(NewId(), Defs.Venison, 1)) && g.TryInsert(new Item(NewId(), Defs.Venison, 1)) && g.TryInsert(new Item(NewId(), Defs.Venison, 1)));
    }

    [Fact]
    public void FullGridRejects()
    {
        var g = new InventoryGrid("tiny", 1, 1);
        Assert.True(g.TryInsert(new Item(NewId(), Defs.Berries, 40)));
        var more = new Item(NewId(), Defs.Berries, 5);
        Assert.Equal(0, g.Absorb(more, NewId));
        Assert.Equal(5, more.Count);
    }

    [Fact]
    public void MoveWithinGrid()
    {
        var g = new InventoryGrid("m", 4, 4);
        var it = new Item(NewId(), Defs.Venison, 1);
        g.TryInsert(it);
        Assert.True(g.Move(it, 2, 2, false));
        Assert.Equal(0, g.EntryAt(3, 3));
        Assert.Equal(-1, g.EntryAt(0, 0));
        Assert.False(g.Move(it, 3, 3, false));
    }
}

public class PawnTests
{
    int _id = 1;
    int NewId() => _id++;

    [Fact]
    public void GeneratedPawnsAreValid()
    {
        var rng = new Rng(7);
        for (int i = 0; i < 200; i++)
        {
            var p = PawnGenerator.Generate(ref rng, NewId);
            Assert.InRange(p.BioAge, 18, 64);
            Assert.True(p.ChronoAge >= p.BioAge);
            Assert.All(p.Skills, s => Assert.InRange(s, (byte)0, (byte)20));
            Assert.InRange(p.Traits.Count, 1, 3);
            foreach (var t in p.Traits) foreach (var c in t.Conflicts) Assert.False(p.HasTrait(c));
            Assert.InRange(p.BodyMassKg, 40f, 130f);
            Assert.Equal(new[] { Defs.TShirt, Defs.Jeans }, p.Apparel.Select(a => a.Def).OrderBy(d => d.Id == "jeans").ToArray());
            Assert.NotNull(p.Inventory);
            Assert.Null(p.Held);
            Assert.Equal(p.AgeLabel.Contains('('), p.BioAge != p.ChronoAge);
        }
    }

    [Fact]
    public void ApparelLayersConflictOnlyOnSameLayerAndRegion()
    {
        var p = new Pawn { BodyMassKg = 70 };
        p.Wear(new Item(NewId(), Defs.TShirt, 1));
        Assert.Empty(p.WearConflicts(Defs.Jeans));   // legs, not the torso: fine
        Assert.Single(p.WearConflicts(Defs.TShirt)); // another skin top: conflict
        p.Wear(new Item(NewId(), Defs.Jeans, 1));
        Assert.Equal(Defs.Jeans, p.WornAt(ApparelLayer.Skin, BodyRegion.LegL).Def);
        Assert.Null(p.WornAt(ApparelLayer.Outer, BodyRegion.Torso));
    }

    [Fact]
    public void EncumbranceFollowsBodyMassFractions()
    {
        var p = new Pawn { BodyMassKg = 80 };
        p.CreateInventory();
        Assert.Equal(Encumbrance.Unencumbered, p.EncumbranceLevel);
        for (int i = 0; i < 4; i++) Assert.True(p.Inventory.TryInsert(new Item(NewId(), Defs.Venison, 10))); // 20 kg
        Assert.Equal(Encumbrance.Unencumbered, p.EncumbranceLevel); // 20 kg / 80 kg = 25 %: the comfortable limit
        Assert.True(p.Inventory.TryInsert(new Item(NewId(), Defs.Berries, 10)));
        Assert.Equal(Encumbrance.Encumbered, p.EncumbranceLevel);
        Assert.True(p.SpeedFactorFromLoad < 1f && p.SpeedFactorFromLoad > 0.75f);
    }

    [Fact]
    public void InventorySizeFollowsCarryingCapacity()
    {
        var light = new Pawn { BodyMassKg = 50 }; light.CreateInventory();
        var heavy = new Pawn { BodyMassKg = 110 }; heavy.CreateInventory();
        var strong = new Pawn { BodyMassKg = 50 }; strong.Traits.Add(Traits.Get("strong_back")); strong.CreateInventory();
        Assert.True(heavy.Inventory.H > light.Inventory.H);
        Assert.Equal(60f, strong.CarryBasisKg, 3);
        Assert.True(strong.Inventory.H >= light.Inventory.H);
        Assert.Equal(Pawn.InventoryColumns, light.Inventory.W);
    }

    [Fact]
    public void EveryTraitHasVisibleStatEffects()
    {
        foreach (var t in Traits.All)
        {
            Assert.NotEmpty(t.Effects);
            string text = t.EffectsText();
            Assert.False(string.IsNullOrWhiteSpace(text), t.Id);
            Assert.Contains("%", text);
        }
        var p = new Pawn { BodyMassKg = 70 };
        p.Traits.Add(Traits.Get("jogger"));
        Assert.True(p.MoveSpeed(false) > new Pawn { BodyMassKg = 70 }.MoveSpeed(false));
    }

    [Fact]
    public void NeedsDecayAtRealisticRates()
    {
        var n = new Needs();
        n.Tick(16f, asleep: false, temperature: 18f, exertion: 1f);
        Assert.InRange(n.Thirst, 0f, 0.05f);      // a whole waking day without water → parched
        Assert.InRange(n.Food, 0.05f, 0.2f);       // hungry well before a day is over
        Assert.InRange(n.Rest, 0.05f, 0.2f);       // tired after 16 h awake
        n.Tick(8f, asleep: true, temperature: 18f, exertion: 1f);
        Assert.True(n.Rest > 0.95f);               // a night's sleep restores rest
        var hot = new Needs(); var mild = new Needs();
        hot.Tick(4f, false, 38f, 1f); mild.Tick(4f, false, 15f, 1f);
        Assert.True(hot.Thirst < mild.Thirst);     // heat makes you thirsty faster
    }

    [Fact]
    public void VitalPartDestroyedKills()
    {
        var h = new Health(BodyDef.Human);
        Assert.False(h.Damage(h.Body.Find("Left arm"), 10, 0));
        Assert.True(h.Damage(h.Body.Find("Heart"), 20, 0));
        Assert.Contains("Heart", h.DeathCause);
    }

    [Fact]
    public void BleedingAccumulatesBloodLoss()
    {
        var h = new Health(BodyDef.Deer);
        h.Damage(h.Body.Find("Body"), 12, 0, bleedFactor: 2.2f);
        Assert.False(h.Dead);
        bool died = false;
        for (int i = 0; i < 40 && !died; i++) died = h.TickBleeding(0.5f);
        Assert.True(h.BloodLoss > 0.2f);
    }

    [Fact]
    public void HumanBodyHasRimworldParts()
    {
        var b = BodyDef.Human;
        foreach (var name in new[] { "Brain", "Skull", "Left eye", "Jaw", "Tongue", "Sternum", "Ribcage", "Spine", "Heart", "Left lung", "Liver", "Left kidney",
                                     "Stomach", "Pelvis", "Waist", "Right clavicle", "Right shoulder", "Right humerus", "Right radius", "Right thumb",
                                     "Right pinky", "Left femur", "Left tibia", "Left big toe", "Left little toe" })
            b.Find(name);
        Assert.True(b.Count > 60);
    }
}

public class SimTests
{
    [Fact]
    public void NewColonyHasPawnsLootAndDeer()
    {
        var sim = Fixtures.NewSim();
        Assert.Equal(3, sim.Pawns.Count);
        Assert.Contains(sim.Items, i => i.Def == Defs.Bow);
        Assert.Contains(sim.Items, i => i.Def == Defs.Arrow);
        Assert.True(sim.Animals.Count >= 6);
        foreach (var p in sim.Pawns) Assert.False(sim.Map.Blocked(sim.Map.CellAt(p.Position)));
    }

    [Fact]
    public void LocalTemperatureFollowsThePlanetTile()
    {
        var sim = Fixtures.NewSim();
        var sample = sim.Planet.Climate.Sample(sim.Map.PlanetTile);
        float hour = GameTime.HourOfDay(sim.Tick);
        Assert.Equal(sample.TemperatureAtHour(hour), sim.Weather.Temperature, 2);
        // a season later the planet and the map have both changed together
        for (int i = 0; i < GameTime.TicksPerDay * 15; i += GameTime.TicksPerHour)
            for (int k = 0; k < GameTime.TicksPerHour; k++) sim.Step();
        var later = sim.Planet.Climate.Sample(sim.Map.PlanetTile);
        Assert.NotEqual(sample.Temperature, later.Temperature);
        Assert.InRange(sim.Weather.PlanetTemperature, later.Temperature - 0.01f, later.Temperature + 0.01f);
        Assert.InRange(sim.Weather.Temperature, later.Temperature - later.DiurnalRange - 3f, later.Temperature + later.DiurnalRange + 3f);
    }

    [Fact]
    public void AutonomousPawnsSurviveADay()
    {
        var sim = Fixtures.NewSim(seed: 11);
        for (int i = 0; i < GameTime.TicksPerDay; i++) sim.Step();
        foreach (var p in sim.Pawns)
        {
            Assert.False(p.Dead);
            Assert.True(p.Needs.Thirst > 0.05f, $"{p.Name} thirst {p.Needs.Thirst}");
            Assert.False(sim.Map.Blocked(sim.Map.CellAt(p.Position)));
        }
    }

    /// <summary>The goal of this first version, played through the simulation API exactly like the player would.</summary>
    [Fact]
    public void GoalScenario_PickUpBow_ShootDeer_PickUpMeat()
    {
        var sim = Fixtures.NewSim(size: 110, seed: 21);
        var pawn = sim.Pawns[0];
        pawn.Skills[(int)SkillId.Shooting] = 12;
        sim.SetMode(pawn, ControlMode.Direct);
        Assert.Equal(pawn, sim.Controlled);

        // walk to the bow (cabin: open the door with E on the way)
        var bow = sim.Items.First(i => i.Def == Defs.Bow);
        var door = Array.IndexOf(sim.Map.Buildings, Building.Door);
        WalkTo(sim, pawn, sim.Map.CellCenter(door), stopAt: 1.2f);
        var doorAction = Interactions.Nearby(sim, pawn).FirstOrDefault(i => i.Kind == InteractionKind.OpenDoor);
        Assert.NotNull(doorAction);
        Interactions.Execute(sim, pawn, doorAction);
        Run(sim, 40);
        Assert.True(sim.Map.DoorOpen[door]);
        WalkTo(sim, pawn, bow.Position, stopAt: 0.9f);
        var pick = Interactions.Nearby(sim, pawn).First(i => i.Item == bow);
        Interactions.Execute(sim, pawn, pick);
        Run(sim, 60);
        Assert.Equal(bow, pawn.Held);
        foreach (var arrows in sim.Items.Where(i => i.Def == Defs.Arrow).ToList())
        {
            WalkTo(sim, pawn, arrows.Position, stopAt: 0.9f);
            Interactions.Execute(sim, pawn, Interactions.Nearby(sim, pawn).First(i => i.Item == arrows));
            Run(sim, 60);
        }
        Assert.True(pawn.Arrows >= 30, $"arrows {pawn.Arrows}");

        // step outside, put a deer in the open in clear view, and shoot until it falls
        WalkTo(sim, pawn, sim.FindStandableNear(new Vector2(sim.Map.Width / 2f, sim.Map.Height / 2f), 8), stopAt: 0.5f);
        Vector2 spot = Vector2.Zero;
        for (int k = 0; k < 64 && spot == Vector2.Zero; k++)
        {
            float ang = k * 0.4f;
            var s = sim.FindStandableNear(pawn.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 7f, 4);
            if (sim.LineOfSight(pawn.Position, s) && Vector2.Distance(s, pawn.Position) > 4f) spot = s;
        }
        Assert.NotEqual(Vector2.Zero, spot);
        var deer = sim.Animals[0];
        deer.Position = spot;
        deer.Path.Clear(); deer.PathIndex = 0;
        deer.State = AnimalState.Graze;
        deer.StateTimer = 100000;
        int shots = 0;
        while (!deer.Dead && shots < 40 && pawn.Arrows > 0)
        {
            sim.Input.Aim = true;
            sim.Input.AimPoint = deer.Position;
            sim.Input.Fire = true;
            Run(sim, 75);
            shots++;
            if (!deer.Dead && Vector2.Distance(deer.Position, pawn.Position) > 15f)
            {   // it fled: follow it, it is wounded
                deer.Position = spot;
                deer.Path.Clear(); deer.PathIndex = 0;
            }
        }
        sim.Input.Aim = false;
        Assert.True(deer.Dead, $"deer still alive after {shots} shots");
        var meat = sim.Items.Where(i => i.Def == Defs.Venison).ToList();
        Assert.NotEmpty(meat);

        // pick up the meat
        var stack = meat[0];
        WalkTo(sim, pawn, stack.Position, stopAt: 0.9f);
        Interactions.Execute(sim, pawn, Interactions.Nearby(sim, pawn).First(i => i.Item == stack));
        Run(sim, 60);
        Assert.True(pawn.CountInInventory(Defs.Venison) > 0);
    }

    static void Run(GameSim sim, int ticks) { for (int i = 0; i < ticks; i++) sim.Step(); }

    /// <summary>Steers the directly controlled pawn with WASD-like input toward a point (A* waypoints, like a player would).</summary>
    static void WalkTo(GameSim sim, Pawn p, Vector2 target, float stopAt)
    {
        var path = new List<Vector2>();
        int goal = sim.Map.CellAt(target);
        if (sim.Paths.Cost[goal] == 0 || sim.Map.Buildings[goal] == Building.Door) goal = sim.InteractionSpot(goal, p.Position);
        var res = sim.Pathfinder.FindPath(sim.Map.CellAt(p.Position), goal, path);
        Assert.Equal(PathResult.Found, res);
        path.Add(target);
        int idx = 0;
        for (int t = 0; t < 20000 && Vector2.Distance(p.Position, target) > stopAt; t++)
        {
            while (idx < path.Count - 1 && Vector2.Distance(p.Position, path[idx]) < 0.3f) idx++;
            var d = path[idx] - p.Position;
            sim.Input.Move = d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.Zero;
            sim.Step();
        }
        sim.Input.Move = Vector2.Zero;
        Assert.True(Vector2.Distance(p.Position, target) <= stopAt + 0.05f, $"could not walk to {target}, stuck at {p.Position}");
    }
}

public class EquipAndCombatTests
{
    static void Run(GameSim sim, int ticks) { for (int i = 0; i < ticks; i++) sim.Step(); }

    [Fact]
    public void PutAwayAndReEquipKeepsTheBowInTheHands()
    {
        var sim = Fixtures.NewSim();
        var p = sim.Pawns[0];
        var bow = sim.Items.First(i => i.Def == Defs.Bow);
        sim.Despawn(bow);
        p.Held = bow;
        sim.Unequip(p);
        Assert.Null(p.Held);
        Assert.Contains(p.Inventory.Entries, e => e.Item == bow);
        Assert.False(p.HasRangedWeapon);
        sim.EquipFromInventory(p, bow);
        Assert.Equal(bow, p.Held);
        Assert.DoesNotContain(p.Inventory.Entries, e => e.Item == bow);
        Assert.True(p.HasRangedWeapon);
    }

    [Fact]
    public void EquippingSomethingElsePutsTheHeldItemAway()
    {
        var sim = Fixtures.NewSim();
        var p = sim.Pawns[0];
        var bow = sim.Items.First(i => i.Def == Defs.Bow);
        sim.Despawn(bow);
        p.Held = bow;
        var meat = new Item(sim.NewId(), Defs.Venison, 3);
        Assert.True(p.Inventory.TryInsert(meat));
        sim.EquipFromInventory(p, meat);
        Assert.Equal(meat, p.Held);
        Assert.Contains(p.Inventory.Entries, e => e.Item == bow);
    }

    [Fact]
    public void ArrowsHitColonistsToo()
    {
        var sim = Fixtures.NewSim();
        var shooter = sim.Pawns[0];
        var target = sim.Pawns[1];
        var bow = sim.Items.First(i => i.Def == Defs.Bow);
        sim.Despawn(bow);
        shooter.Held = bow;
        Assert.True(shooter.Inventory.TryInsert(new Item(sim.NewId(), Defs.Arrow, 10)));
        // the target stands 1.6 m east of the shooter
        var a = shooter.Position;
        float before = target.Health.Summary;
        int hits = 0;
        for (int shot = 0; shot < 8 && hits == 0; shot++)
        {
            target.Position = a + new Vector2(1.6f, 0); target.Path.Clear(); target.Job = null;
            shooter.Position = a; shooter.WeaponCooldown = 0;
            sim.Attack(shooter, new Vector2(1, 0), moving: false);
            Run(sim, 10);
            if (target.Health.Summary < before) hits++;
        }
        Assert.True(hits > 0, "no arrow hit the colonist");
    }
}

public class SaveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "remade_save_" + Guid.NewGuid().ToString("N")[..6]);
    public SaveTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void RoundTripPreservesTheGame()
    {
        var sim = Fixtures.NewSim(seed: 33);
        for (int i = 0; i < 3000; i++) sim.Step();
        sim.Map.SetDoor(Array.IndexOf(sim.Map.Buildings, Building.Door), true);
        sim.Map.MineOut(Array.IndexOf(sim.Map.Buildings, Building.Granite) is int g && g >= 0 ? g : throw new Exception("no rock"));
        string path = Path.Combine(_dir, "a.rsav");
        SaveGame.Write(sim, path);
        var header = SaveGame.ReadHeader(path);
        Assert.Equal(sim.Tick, header.Tick);
        Assert.Equal(sim.ColonyName, header.ColonyName);

        var loaded = SaveGame.Read(path);
        Assert.Equal(sim.Tick, loaded.Tick);
        Assert.Equal(sim.Map.Terrain, loaded.Map.Terrain);
        Assert.Equal(sim.Map.Buildings, loaded.Map.Buildings);
        Assert.Equal(sim.Map.DoorOpen, loaded.Map.DoorOpen);
        Assert.Equal(sim.Pawns.Select(p => p.FullName), loaded.Pawns.Select(p => p.FullName));
        Assert.Equal(sim.Pawns.Select(p => p.Needs.Thirst), loaded.Pawns.Select(p => p.Needs.Thirst));
        Assert.Equal(sim.Pawns.Select(p => p.CarriedMass), loaded.Pawns.Select(p => p.CarriedMass));
        Assert.Equal(sim.Animals.Count, loaded.Animals.Count);
        Assert.Equal(sim.Items.Count, loaded.Items.Count);
        Assert.Equal(sim.Planet.Climate.Temperature, loaded.Planet.Climate.Temperature);

        // both continue identically (determinism across save/load)
        for (int i = 0; i < 2000; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(sim.Animals.Select(a => a.Position), loaded.Animals.Select(a => a.Position));
    }

    [Fact]
    public void CorruptSavesFailLoudly()
    {
        var sim = Fixtures.NewSim(seed: 34);
        string path = Path.Combine(_dir, "b.rsav");
        SaveGame.Write(sim, path);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        Assert.Throws<SaveFormatException>(() => SaveGame.Read(path));
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<SaveFormatException>(() => SaveGame.ReadHeader(path));
    }
}

public class DirectControlTests
{
    /// <summary>A colonist pushed diagonally into a door jamb must slip through the open door instead of sticking.</summary>
    [Fact]
    public void DoorwayAssistSlipsThroughOpenDoor()
    {
        var planet = new Planet(new WorldParams { Seed = 1, Frequency = 16 });
        var map = new LocalMap(20, 20, 0, 1);
        for (int y = 0; y < 20; y++) map.Buildings[map.Index(10, y)] = Building.WoodWall;
        map.Buildings[map.Index(10, 10)] = Building.Door;
        map.DoorOpen[map.Index(10, 10)] = true;
        var sim = new GameSim(planet, map, 1);
        int id = 1;
        var p = PawnGenerator.GenerateGroup(1, 3, () => id++)[0];
        p.Position = new Vector2(11.5f, 10.05f); // east of the door, nearly at the jamb's corner
        sim.Pawns.Add(p);
        sim.SetMode(p, ControlMode.Direct);
        sim.Input.Move = Vector2.Normalize(new Vector2(-1f, -0.25f)); // west and slightly north, into the wall corner
        for (int t = 0; t < 120 && p.Position.X > 9.5f; t++) sim.Step();
        Assert.True(p.Position.X < 10f, $"stuck at {p.Position}");
    }

    /// <summary>A door the player left open must stay open after another colonist walks through it.</summary>
    [Fact]
    public void ColonistsDoNotCloseADoorLeftOpen()
    {
        var planet = new Planet(new WorldParams { Seed = 1, Frequency = 16 });
        var map = new LocalMap(20, 20, 0, 1);
        for (int y = 0; y < 20; y++) map.Buildings[map.Index(10, y)] = Building.WoodWall;
        int door = map.Index(10, 10);
        map.Buildings[door] = Building.Door;
        map.DoorOpen[door] = true;
        var sim = new GameSim(planet, map, 1);
        int id = 1;
        var p = PawnGenerator.GenerateGroup(1, 3, () => id++)[0];
        p.Position = new Vector2(13.5f, 10.5f);
        sim.Pawns.Add(p);
        sim.SetMode(p, ControlMode.Drafted);
        sim.StartJob(p, new Job { Kind = JobKind.Goto, TargetCell = map.Index(6, 10), Forced = true });
        for (int t = 0; t < 600 && p.Position.X > 7f; t++) sim.Step();
        Assert.True(p.Position.X < 7.5f, $"did not get through: {p.Position}");
        for (int t = 0; t < 200; t++) sim.Step();
        Assert.True(sim.Map.DoorOpen[door]);
    }
}

public class ClockTests
{
    [Fact]
    public void ColonyCannotStartBeforeThePlanetsCurrentTime()
    {
        var planet = new Planet(new WorldParams { Seed = 2, Frequency = 16 });
        planet.Climate.Update(GameTime.TicksPerDay * 3);
        int id = 1;
        var pawns = PawnGenerator.GenerateGroup(1, 1, () => id++);
        int tile = Enumerable.Range(0, planet.TileCount).First(t => planet.Biomes[t] == Biome.TemperateForest);
        Assert.Throws<ArgumentException>(() => GameSim.NewColony(planet, tile, 64, pawns, 1, GameTime.TicksPerHour));
    }
}

public class MiningTests
{
    [Fact]
    public void MiningRemovesGraniteAndOpensThePath()
    {
        var planet = new Planet(new WorldParams { Seed = 1, Frequency = 16 });
        var map = new LocalMap(20, 20, 0, 1);
        for (int y = 0; y < 20; y++) { map.Buildings[map.Index(10, y)] = Building.Granite; map.BuildingHp[map.Index(10, y)] = 1500; map.Terrain[map.Index(10, y)] = Terrain.RoughGranite; }
        var sim = new GameSim(planet, map, 1);
        int id = 1;
        var p = PawnGenerator.GenerateGroup(1, 3, () => id++)[0];
        p.Position = new Vector2(8.5f, 10.5f);
        sim.Pawns.Add(p);
        sim.SetMode(p, ControlMode.Drafted);
        Assert.False(sim.Paths.Reachable(map.Index(2, 2), map.Index(18, 2)));
        var mine = Interactions.ForCell(sim, p, map.Index(10, 10)).Single(i => i.Kind == InteractionKind.Mine);
        Interactions.Execute(sim, p, mine);
        for (int t = 0; t < 2000 && map.Buildings[map.Index(10, 10)] == Building.Granite; t++) sim.Step();
        Assert.Equal(Building.None, map.Buildings[map.Index(10, 10)]);
        Assert.Equal(Terrain.RoughGranite, map.Terrain[map.Index(10, 10)]);
        Assert.True(sim.Paths.Reachable(map.Index(2, 2), map.Index(18, 2)));
        Assert.Null(p.Job);
    }
}
