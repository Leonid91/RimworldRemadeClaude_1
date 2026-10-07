using System;

namespace Remade.Map;

public enum Terrain : byte
{
    Soil, RichSoil, Gravel, Sand, Mud, Marsh, RoughGranite,
    RiverShallow, RiverDeep, OceanShallow, OceanDeep, LakeShallow, LakeDeep,
    Count,
}

public enum Building : byte { None, Granite, WoodWall, Door }

public enum Plant : byte { None, Oak, BerryBush }

public readonly struct TerrainDef
{
    public readonly string Label;
    public readonly bool Walkable;
    /// <summary>Path cost multiplier (10 = normal ground).</summary>
    public readonly byte Cost;
    public readonly bool Water, FreshWater, Deep;
    public readonly float Fertility;

    public TerrainDef(string label, bool walk, byte cost, bool water, bool fresh, bool deep, float fert)
    { Label = label; Walkable = walk; Cost = cost; Water = water; FreshWater = fresh; Deep = deep; Fertility = fert; }

    static readonly TerrainDef[] Table =
    {
        new("Soil", true, 10, false, false, false, 1.0f),
        new("Rich soil", true, 10, false, false, false, 1.4f),
        new("Gravel", true, 10, false, false, false, 0.7f),
        new("Sand", true, 13, false, false, false, 0.1f),
        new("Mud", true, 18, false, false, false, 0.6f),
        new("Marshy soil", true, 22, false, false, false, 0.9f),
        new("Rough granite", true, 10, false, false, false, 0f),
        new("Shallow moving water", true, 30, true, true, false, 0f),
        new("Deep moving water", false, 0, true, true, true, 0f),
        new("Shallow ocean water", true, 30, true, false, false, 0f),
        new("Deep ocean water", false, 0, true, false, true, 0f),
        new("Shallow water", true, 30, true, true, false, 0f),
        new("Deep water", false, 0, true, true, true, 0f),
    };

    public static ref readonly TerrainDef Of(Terrain t)
    {
        if ((uint)t >= (uint)Table.Length) throw new ArgumentOutOfRangeException(nameof(t), t, "unknown terrain");
        return ref Table[(int)t];
    }
}

public static class BuildingInfo
{
    public static string Label(Building b) => b switch
    {
        Building.None => "",
        Building.Granite => "Granite",
        Building.WoodWall => "Wooden wall",
        Building.Door => "Wooden door",
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };

    public static ushort MaxHp(Building b) => b switch
    {
        Building.Granite => 1500,
        Building.WoodWall => 300,
        Building.Door => 200,
        _ => 0,
    };

    /// <summary>Solid buildings block movement, sight and projectiles (doors only while closed).</summary>
    public static bool Solid(Building b) => b == Building.Granite || b == Building.WoodWall;
}

public static class PlantInfo
{
    public static string Label(Plant p) => p switch
    {
        Plant.None => "",
        Plant.Oak => "Oak tree",
        Plant.BerryBush => "Berry bush",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    public const int OakVariants = 12;
    public const int BushVariants = 4;
    public const int MaxBerries = 12;
}
