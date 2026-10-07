using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Diagnostics;

namespace Remade.Map;

[Flags]
public enum MapLayer : byte { Terrain = 1, Building = 2, Plant = 4, Door = 8, Grass = 16 }

/// <summary>A cell change for listeners (renderer, path grid). Queued, consumed once per frame.</summary>
public readonly struct CellChange
{
    public readonly int Cell;
    public readonly MapLayer Layer;
    public CellChange(int cell, MapLayer layer) { Cell = cell; Layer = layer; }
}

/// <summary>
/// The grid of a playable location. Struct-of-arrays layers, one entry per cell; cell (x, y) covers world
/// X ∈ [x, x+1], Z ∈ [y, y+1] (1 cell = 1 m). North is −y. All mutations go through setters that record
/// <see cref="CellChange"/>s so the renderer and path grid can update incrementally.
/// </summary>
public sealed class LocalMap
{
    public readonly int Width, Height;
    public readonly int CellCount;
    /// <summary>Planet tile this map belongs to — its natural conditions come from that tile.</summary>
    public readonly int PlanetTile;
    public readonly int Seed;
    public string Id => $"tile{PlanetTile}";

    public readonly Terrain[] Terrain;
    public readonly Building[] Buildings;
    public readonly ushort[] BuildingHp;
    public readonly Plant[] Plants;
    public readonly byte[] PlantVariant;
    /// <summary>Plant size/growth 0..255.</summary>
    public readonly byte[] PlantGrowth;
    public readonly byte[] Berries;
    /// <summary>Grass density 0..255 (visual + grazing).</summary>
    public readonly byte[] Grass;
    /// <summary>Ground height at cell corners, (Width+1)·(Height+1), metres. Water surface is at 0.</summary>
    public readonly float[] Ground;
    /// <summary>Rock massif height above ground for rock cells (visual), metres.</summary>
    public readonly float[] RockHeight;
    /// <summary>Door open state, keyed by cell.</summary>
    public readonly Dictionary<int, bool> DoorOpen = new();

    public const float WaterLevel = 0f;

    readonly List<CellChange> _changes = new();
    public int ChangeCount => _changes.Count;

    public LocalMap(int width, int height, int planetTile, int seed)
    {
        if (width < 8 || height < 8 || width > 4096 || height > 4096)
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height}", "map size must be 8..4096");
        Width = width; Height = height; CellCount = width * height;
        PlanetTile = planetTile; Seed = seed;
        Terrain = new Terrain[CellCount];
        Buildings = new Building[CellCount];
        BuildingHp = new ushort[CellCount];
        Plants = new Plant[CellCount];
        PlantVariant = new byte[CellCount];
        PlantGrowth = new byte[CellCount];
        Berries = new byte[CellCount];
        Grass = new byte[CellCount];
        Ground = new float[(width + 1) * (height + 1)];
        RockHeight = new float[CellCount];
    }

    // ---------------------------------------------------------------- addressing

    public int Index(int x, int y) => y * Width + x;
    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public void XY(int cell, out int x, out int y) { y = cell / Width; x = cell - y * Width; }
    public static Vector2 CellCenter(int x, int y) => new(x + 0.5f, y + 0.5f);
    public Vector2 CellCenter(int cell) { XY(cell, out int x, out int y); return new(x + 0.5f, y + 0.5f); }
    public int CellAt(Vector2 p)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
        return InBounds(x, y) ? Index(x, y) : -1;
    }

    // ---------------------------------------------------------------- queries

    public ref readonly TerrainDef TerrainAt(int cell) => ref TerrainDef.Of(Terrain[cell]);

    /// <summary>True if a pawn may stand in the cell (closed doors count as passable for path planning; AI opens them).</summary>
    public bool Walkable(int cell)
    {
        if (!TerrainDef.Of(Terrain[cell]).Walkable) return false;
        return !BuildingInfo.Solid(Buildings[cell]);
    }

    /// <summary>Physically blocked right now (solid building, deep water, or a closed door).</summary>
    public bool Blocked(int cell)
    {
        if (!Walkable(cell)) return true;
        return Buildings[cell] == Building.Door && !DoorOpen[cell];
    }

    /// <summary>Stops projectiles and line of sight.</summary>
    public bool BlocksProjectiles(int cell)
        => BuildingInfo.Solid(Buildings[cell]) || (Buildings[cell] == Building.Door && !DoorOpen[cell]);

    public bool IsFreshWater(int cell) => TerrainDef.Of(Terrain[cell]).FreshWater;

    /// <summary>Bilinear ground height at a world position (x, z).</summary>
    public float GroundHeight(float x, float z)
    {
        x = Math.Clamp(x, 0f, Width - 0.001f);
        z = Math.Clamp(z, 0f, Height - 0.001f);
        int ix = (int)x, iz = (int)z;
        float fx = x - ix, fz = z - iz;
        int w = Width + 1;
        float h00 = Ground[iz * w + ix], h10 = Ground[iz * w + ix + 1];
        float h01 = Ground[(iz + 1) * w + ix], h11 = Ground[(iz + 1) * w + ix + 1];
        return (h00 * (1 - fx) + h10 * fx) * (1 - fz) + (h01 * (1 - fx) + h11 * fx) * fz;
    }

    /// <summary>Height a creature stands at: ground, but never below the water surface it wades in.</summary>
    public float StandHeight(float x, float z) => MathF.Max(GroundHeight(x, z), WaterLevel - 0.35f);

    // ---------------------------------------------------------------- mutations

    public void SetTerrain(int cell, Terrain t)
    {
        if (Terrain[cell] == t) return;
        Terrain[cell] = t;
        _changes.Add(new CellChange(cell, MapLayer.Terrain));
    }

    public void SetBuilding(int cell, Building b)
    {
        if (Buildings[cell] == b) return;
        if (Buildings[cell] == Building.Door) DoorOpen.Remove(cell);
        Buildings[cell] = b;
        BuildingHp[cell] = BuildingInfo.MaxHp(b);
        if (b == Building.Door) DoorOpen[cell] = false;
        if (b != Building.None && Plants[cell] != Plant.None) SetPlant(cell, Plant.None, 0, 0);
        _changes.Add(new CellChange(cell, MapLayer.Building));
    }

    public void SetDoor(int cell, bool open)
    {
        Invariant.Check(Buildings[cell] == Building.Door, $"SetDoor on cell {cell} which is {Buildings[cell]}");
        if (DoorOpen[cell] == open) return;
        DoorOpen[cell] = open;
        _changes.Add(new CellChange(cell, MapLayer.Door));
        Log.Debug($"door {cell} {(open ? "opened" : "closed")}");
    }

    public void SetPlant(int cell, Plant p, byte variant, byte growth)
    {
        Plants[cell] = p;
        PlantVariant[cell] = variant;
        PlantGrowth[cell] = growth;
        if (p != Plant.BerryBush) Berries[cell] = 0;
        _changes.Add(new CellChange(cell, MapLayer.Plant));
    }

    public void SetBerries(int cell, byte count)
    {
        Invariant.Check(Plants[cell] == Plant.BerryBush, $"SetBerries on cell {cell} without a berry bush");
        if (Berries[cell] == count) return;
        Berries[cell] = count;
        _changes.Add(new CellChange(cell, MapLayer.Plant));
    }

    public void SetGrass(int cell, byte v)
    {
        if (Grass[cell] == v) return;
        Grass[cell] = v;
        _changes.Add(new CellChange(cell, MapLayer.Grass));
    }

    /// <summary>Removes a mined rock cell: the floor becomes rough granite.</summary>
    public void MineOut(int cell)
    {
        Invariant.Check(Buildings[cell] == Building.Granite, $"MineOut on cell {cell} which is {Buildings[cell]}");
        SetBuilding(cell, Building.None);
        RockHeight[cell] = 0;
        SetTerrain(cell, Map.Terrain.RoughGranite);
    }

    /// <summary>Moves the pending change list into <paramref name="into"/> and clears it.</summary>
    public void DrainChanges(List<CellChange> into)
    {
        into.AddRange(_changes);
        _changes.Clear();
    }

    public void ClearChanges() => _changes.Clear();
}
