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

    public static readonly ThingDef TShirt = Add(new ThingDef
    {
        Id = "tshirt", Label = "cotton T-shirt", Kind = ThingKind.Apparel, Mass = 0.2f, GridW = 2, GridH = 2, Color = 0x7C8C70,
        Layers = new[] { ApparelLayer.Skin },
        Covers = new[] { BodyRegion.Torso, BodyRegion.Neck, BodyRegion.ShoulderL, BodyRegion.ShoulderR },
    });

    public static readonly ThingDef Trousers = Add(new ThingDef
    {
        Id = "trousers", Label = "canvas trousers", Kind = ThingKind.Apparel, Mass = 0.6f, GridW = 2, GridH = 2, Color = 0x4F5866,
        Layers = new[] { ApparelLayer.Skin },
        Covers = new[] { BodyRegion.Waist, BodyRegion.LegL, BodyRegion.LegR },
        ContainerW = 4, ContainerH = 2, ContainerLabel = "Trouser pockets",
    });

    public static readonly ThingDef Jacket = Add(new ThingDef
    {
        Id = "jacket", Label = "waxed jacket", Kind = ThingKind.Apparel, Mass = 1.1f, GridW = 2, GridH = 3, Color = 0x6B5B3E,
        Layers = new[] { ApparelLayer.Outer },
        Covers = new[] { BodyRegion.Torso, BodyRegion.Neck, BodyRegion.ShoulderL, BodyRegion.ShoulderR, BodyRegion.ArmL, BodyRegion.ArmR },
        ContainerW = 2, ContainerH = 2, ContainerLabel = "Jacket pockets",
    });

    public static readonly ThingDef Shirt = Add(new ThingDef
    {
        Id = "shirt", Label = "flannel shirt", Kind = ThingKind.Apparel, Mass = 0.35f, GridW = 2, GridH = 2, Color = 0x8E3B33,
        Layers = new[] { ApparelLayer.Middle },
        Covers = new[] { BodyRegion.Torso, BodyRegion.Neck, BodyRegion.ShoulderL, BodyRegion.ShoulderR, BodyRegion.ArmL, BodyRegion.ArmR },
    });

    public static readonly ThingDef Cap = Add(new ThingDef
    {
        Id = "cap", Label = "wool cap", Kind = ThingKind.Apparel, Mass = 0.1f, GridW = 1, GridH = 1, Color = 0x3E4A5C,
        Layers = new[] { ApparelLayer.Headgear }, Covers = new[] { BodyRegion.Head },
    });

    public static readonly ThingDef Boots = Add(new ThingDef
    {
        Id = "boots", Label = "leather boots", Kind = ThingKind.Apparel, Mass = 1.4f, GridW = 2, GridH = 2, Color = 0x4A3426,
        Layers = new[] { ApparelLayer.Outer }, Covers = new[] { BodyRegion.FootL, BodyRegion.FootR },
    });

    public static readonly ThingDef Glasses = Add(new ThingDef
    {
        Id = "glasses", Label = "reading glasses", Kind = ThingKind.Apparel, Mass = 0.03f, GridW = 1, GridH = 1, Color = 0x222222,
        Layers = new[] { ApparelLayer.Eyes }, Covers = new[] { BodyRegion.Eyes },
    });

    public static readonly ThingDef Satchel = Add(new ThingDef
    {
        Id = "satchel", Label = "leather satchel", Kind = ThingKind.Apparel, Mass = 0.8f, GridW = 2, GridH = 2, Color = 0x7A5230,
        Layers = new[] { ApparelLayer.Belt }, Covers = new[] { BodyRegion.Waist },
        ContainerW = 6, ContainerH = 4, ContainerLabel = "Satchel",
    });
}
