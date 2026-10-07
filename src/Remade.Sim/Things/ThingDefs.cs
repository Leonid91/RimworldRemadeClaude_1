using System;
using System.Collections.Generic;

namespace Remade.Things;

public enum ThingKind : byte { Weapon, Ammo, Apparel, Food, Resource }

public enum WeaponKind : byte { None, Bow, Melee }

/// <summary>Apparel layers, drawn as tabs on the equipment body figure.</summary>
public enum ApparelLayer : byte { Skin, Middle, Outer, Headgear, Eyes, Belt }

/// <summary>Coarse body regions used for apparel coverage and the body figure (each maps to detailed body parts).</summary>
public enum BodyRegion : byte
{
    Head, Eyes, Neck, Torso, ShoulderL, ShoulderR, ArmL, ArmR, HandL, HandR, Waist, LegL, LegR, FootL, FootR,
    Count,
}

public sealed class ThingDef
{
    public string Id;
    public string Label;
    public string Description;
    public ThingKind Kind;
    /// <summary>Mass of one unit, kg.</summary>
    public float Mass;
    public int StackLimit = 1;
    /// <summary>Inventory grid footprint (columns × rows) of one stack.</summary>
    public int GridW = 1, GridH = 1;
    // food
    public float Nutrition;   // fraction of the food need restored per unit
    public float Hydration;
    // weapon
    public WeaponKind Weapon;
    public float Range, Damage, Cooldown;   // range in cells, cooldown in ticks
    // apparel
    public ApparelLayer[] Layers = Array.Empty<ApparelLayer>();
    public BodyRegion[] Covers = Array.Empty<BodyRegion>();
    /// <summary>Inventory container this apparel provides (pockets, satchel), 0×0 if none.</summary>
    public int ContainerW, ContainerH;
    public string ContainerLabel;
    /// <summary>Display colour (sRGB, 0xRRGGBB) for UI and procedural models.</summary>
    public uint Color = 0xB0B0B0;

    public override string ToString() => Id;
}

/// <summary>All item definitions. Masses follow real-world references (see comments).</summary>
public static class Defs
{
    static readonly Dictionary<string, ThingDef> ById = new();

    public static ThingDef Get(string id)
        => ById.TryGetValue(id, out var d) ? d : throw new KeyNotFoundException($"No ThingDef '{id}'");

    public static IEnumerable<ThingDef> All => ById.Values;

    static ThingDef Add(ThingDef d) { ById.Add(d.Id, d); return d; }

    // A recurve hunting bow weighs ~1–1.5 kg; arrows ~25–35 g each.
    public static readonly ThingDef Bow = Add(new ThingDef
    {
        Id = "bow", Label = "recurve bow", Kind = ThingKind.Weapon, Mass = 1.2f, GridW = 1, GridH = 4,
        Weapon = WeaponKind.Bow, Range = 24f, Damage = 13f, Cooldown = 70, Color = 0x8A5A2B,
        Description = "A wooden recurve hunting bow. Needs arrows.",
    });

    public static readonly ThingDef Arrow = Add(new ThingDef
    {
        Id = "arrow", Label = "arrow", Kind = ThingKind.Ammo, Mass = 0.03f, StackLimit = 30, GridW = 1, GridH = 3, Color = 0xC8B08A,
        Description = "Fletched wooden arrows with iron points.",
    });

    // Venison: one unit = 0.5 kg portion; a deer yields roughly 15–25 kg of meat.
    public static readonly ThingDef Venison = Add(new ThingDef
    {
        Id = "venison", Label = "venison", Kind = ThingKind.Food, Mass = 0.5f, StackLimit = 10, GridW = 2, GridH = 2,
        Nutrition = 0.18f, Color = 0xA33A32, Description = "Raw deer meat.",
    });

    public static readonly ThingDef Berries = Add(new ThingDef
    {
        Id = "berries", Label = "wild berries", Kind = ThingKind.Food, Mass = 0.05f, StackLimit = 40, GridW = 1, GridH = 1,
        Nutrition = 0.03f, Hydration = 0.01f, Color = 0x5B2A7A, Description = "Sweet, dark wild berries.",
    });

    public static readonly ThingDef GraniteChunk = Add(new ThingDef
    {
        Id = "granite_chunk", Label = "granite chunk", Kind = ThingKind.Resource, Mass = 25f, StackLimit = 1, GridW = 2, GridH = 2,
        Color = 0x8A8480, Description = "A heavy block of granite broken off while mining.",
    });

    // Everyone starts in a white cotton T-shirt (~0.2 kg) and jeans (~0.7 kg); no other apparel exists yet.
    public static readonly ThingDef TShirt = Add(new ThingDef
    {
        Id = "tshirt", Label = "white cotton T-shirt", Kind = ThingKind.Apparel, Mass = 0.2f, GridW = 2, GridH = 2, Color = 0xECEBE6,
        Layers = new[] { ApparelLayer.Skin },
        Covers = new[] { BodyRegion.Torso, BodyRegion.Neck, BodyRegion.ShoulderL, BodyRegion.ShoulderR },
    });

    public static readonly ThingDef Jeans = Add(new ThingDef
    {
        Id = "jeans", Label = "jeans", Kind = ThingKind.Apparel, Mass = 0.7f, GridW = 2, GridH = 2, Color = 0x3E5C86,
        Layers = new[] { ApparelLayer.Skin },
        Covers = new[] { BodyRegion.Waist, BodyRegion.LegL, BodyRegion.LegR },
    });
}
