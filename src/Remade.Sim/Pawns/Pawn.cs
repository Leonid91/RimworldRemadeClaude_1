using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Diagnostics;
using Remade.Things;

namespace Remade.Pawns;

public enum Sex : byte { Male, Female }

public enum SkillId : byte
{
    Shooting, Melee, Construction, Mining, Cooking, Plants, Animals, Crafting, Artistic, Medical, Social, Intellectual,
    Count,
}

public enum ControlMode : byte { Autonomous, Drafted, Direct }

/// <summary>Visible pose for the renderer.</summary>
public enum PawnAnim : byte { Idle, Walk, Run, Work, Drink, Eat, Sleep, Aim, Shoot, Swing, PickUp, Dead }

public enum Encumbrance : byte { Unencumbered, Encumbered, Heavy, Overloaded }

/// <summary>Quantities that traits (and later skills, health, gear) modify. Each is a multiplier, 1 = normal.</summary>
public enum Stat : byte
{
    MoveSpeed, WorkSpeed, GatherSpeed, ShotSpread, AimTime, MeleeDamage, MeleeHitChance, DamageTaken,
    HungerRate, ThirstRate, FatigueRate, CarryCapacity,
}

public sealed class TraitDef
{
    public string Id, Label;
    public (Stat stat, float factor)[] Effects = Array.Empty<(Stat, float)>();
    public string[] Conflicts = Array.Empty<string>();

    /// <summary>One line per effect, e.g. "Move speed +15 %", and whether it helps the colonist (green) or not (red).</summary>
    public List<(string text, bool good)> EffectLines()
    {
        var lines = new List<(string, bool)>();
        foreach (var (stat, f) in Effects)
        {
            int pct = (int)MathF.Round((f - 1f) * 100f);
            lines.Add(($"{Stats.Label(stat)} {(pct >= 0 ? "+" : "−")}{Math.Abs(pct)} %{Stats.Hint(stat, f)}", (f > 1f) == Stats.HigherIsBetter(stat)));
        }
        return lines;
    }

    /// <summary>The effect lines as plain text.</summary>
    public string EffectsText() => string.Join("\n", EffectLines().ConvertAll(l => l.text));
}

public static class Stats
{
    public static string Label(Stat s) => s switch
    {
        Stat.MoveSpeed => "Move speed",
        Stat.WorkSpeed => "Work speed (mining)",
        Stat.GatherSpeed => "Gathering speed",
        Stat.ShotSpread => "Shot spread",
        Stat.AimTime => "Time between shots",
        Stat.MeleeDamage => "Melee damage",
        Stat.MeleeHitChance => "Melee hit chance",
        Stat.DamageTaken => "Damage taken",
        Stat.HungerRate => "Hunger rate",
        Stat.ThirstRate => "Thirst rate",
        Stat.FatigueRate => "Tiredness rate",
        Stat.CarryCapacity => "Carrying capacity",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    /// <summary>True when a larger value of the stat is good for the colonist.</summary>
    public static bool HigherIsBetter(Stat s) => s switch
    {
        Stat.MoveSpeed or Stat.WorkSpeed or Stat.GatherSpeed or Stat.MeleeDamage or Stat.MeleeHitChance or Stat.CarryCapacity => true,
        Stat.ShotSpread or Stat.AimTime or Stat.DamageTaken or Stat.HungerRate or Stat.ThirstRate or Stat.FatigueRate => false,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    /// <summary>Clarifies stats where "less" is better.</summary>
    public static string Hint(Stat s, float f) => s switch
    {
        Stat.ShotSpread => f < 1 ? " (more accurate)" : " (less accurate)",
        Stat.AimTime => f < 1 ? " (shoots faster)" : " (shoots slower)",
        Stat.DamageTaken => f < 1 ? " (tougher)" : " (more fragile)",
        Stat.HungerRate or Stat.ThirstRate or Stat.FatigueRate => f < 1 ? " (slower)" : " (faster)",
        _ => "",
    };
}

/// <summary>Traits: every trait has measurable effects on stats, listed in its tooltip.</summary>
public static class Traits
{
    static TraitDef T(string id, string label, string[] conflicts, params (Stat, float)[] fx)
        => new() { Id = id, Label = label, Effects = fx, Conflicts = conflicts ?? Array.Empty<string>() };

    public static readonly TraitDef[] All =
    {
        T("industrious", "Industrious", new[] { "lazy" }, (Stat.WorkSpeed, 1.35f), (Stat.GatherSpeed, 1.35f)),
        T("lazy", "Lazy", new[] { "industrious" }, (Stat.WorkSpeed, 0.8f), (Stat.GatherSpeed, 0.8f)),
        T("jogger", "Jogger", new[] { "slowpoke" }, (Stat.MoveSpeed, 1.15f)),
        T("slowpoke", "Slowpoke", new[] { "jogger" }, (Stat.MoveSpeed, 0.88f)),
        T("nimble", "Nimble", null, (Stat.MoveSpeed, 1.05f), (Stat.MeleeHitChance, 1.15f)),
        T("tough", "Tough", new[] { "delicate" }, (Stat.DamageTaken, 0.5f)),
        T("delicate", "Delicate", new[] { "tough" }, (Stat.DamageTaken, 1.25f)),
        T("careful_shooter", "Careful shooter", new[] { "trigger_happy" }, (Stat.ShotSpread, 0.75f), (Stat.AimTime, 1.25f)),
        T("trigger_happy", "Trigger-happy", new[] { "careful_shooter" }, (Stat.ShotSpread, 1.3f), (Stat.AimTime, 0.75f)),
        T("brawler", "Brawler", null, (Stat.MeleeDamage, 1.3f), (Stat.ShotSpread, 1.2f)),
        T("green_thumb", "Green thumb", null, (Stat.GatherSpeed, 1.5f)),
        T("strong_back", "Strong back", null, (Stat.CarryCapacity, 1.2f)),
        T("light_eater", "Light eater", new[] { "big_appetite" }, (Stat.HungerRate, 0.7f)),
        T("big_appetite", "Big appetite", new[] { "light_eater" }, (Stat.HungerRate, 1.4f)),
        T("hardy", "Hardy", null, (Stat.ThirstRate, 0.75f)),
        T("short_sleeper", "Short sleeper", new[] { "sleepyhead" }, (Stat.FatigueRate, 0.75f)),
        T("sleepyhead", "Sleepyhead", new[] { "short_sleeper" }, (Stat.FatigueRate, 1.3f)),
    };

    public static TraitDef Get(string id)
    {
        foreach (var t in All) if (t.Id == id) return t;
        throw new KeyNotFoundException($"No trait '{id}'");
    }
}

/// <summary>
/// Needs in [0,1] (1 = fully satisfied). Rates are real-life based, expressed per game hour:
/// food — an adult gets hungry ~5 h after a meal and is ravenous after a day; thirst — ~2.5 L of water lost per day,
/// faster in heat; rest — ~16 h awake then ~8 h of sleep.
/// </summary>
public sealed class Needs
{
    public float Food = 1f, Thirst = 1f, Rest = 1f;

    public const float FoodPerHour = 1f / 18f;          // full → empty in 18 h awake
    public const float ThirstPerHour = 1f / 16f;        // full → empty in 16 h at ≤ 25 °C
    public const float RestPerHourAwake = 1f / 18f;     // tired (≈ 10 %) after ~16 h awake
    public const float RestGainPerHourAsleep = 1f / 7.5f;
    public const float HungryThreshold = 0.3f, ThirstyThreshold = 0.3f, TiredThreshold = 0.25f;

    /// <summary>Advances needs. <paramref name="exertion"/> 1 = walking, 1.6 = running/working hard.</summary>
    public void Tick(float hours, bool asleep, float temperature, float exertion, float hunger = 1f, float thirst = 1f, float fatigue = 1f)
    {
        float metabolic = asleep ? 0.6f : exertion;
        float heat = 1f + MathF.Max(0f, temperature - 25f) * 0.06f; // sweating
        Food = Math.Clamp(Food - FoodPerHour * metabolic * hunger * hours, 0f, 1f);
        Thirst = Math.Clamp(Thirst - ThirstPerHour * (asleep ? 0.5f : exertion) * heat * thirst * hours, 0f, 1f);
        Rest = asleep ? Math.Clamp(Rest + RestGainPerHourAsleep * hours, 0f, 1f)
                      : Math.Clamp(Rest - RestPerHourAwake * (0.7f + 0.3f * exertion) * fatigue * hours, 0f, 1f);
    }

    public static string FoodLabel(float v) => v < 0.05f ? "Starving" : v < HungryThreshold ? "Hungry" : v < 0.6f ? "Peckish" : "Fed";
    public static string ThirstLabel(float v) => v < 0.05f ? "Dehydrated" : v < ThirstyThreshold ? "Thirsty" : v < 0.6f ? "Slightly thirsty" : "Quenched";
    public static string RestLabel(float v) => v < 0.05f ? "Exhausted" : v < TiredThreshold ? "Tired" : v < 0.6f ? "Drowsy" : "Rested";
}

public sealed class Pawn
{
    public int Id;
    public string FirstName, NickName, LastName;
    public Sex Sex;
    public int BioAge, ChronoAge;
    public float BodyMassKg, HeightM;
    public uint SkinColor, HairColor;
    public byte HairStyle;
    public readonly List<TraitDef> Traits = new();
    public readonly byte[] Skills = new byte[(int)SkillId.Count];
    public readonly Health Health = new(BodyDef.Human);
    public readonly Needs Needs = new();

    // equipment
    public readonly List<Item> Apparel = new();
    /// <summary>What is held in the hands: a weapon, or anything else being carried by hand.</summary>
    public Item Held;
    /// <summary>The colonist's inventory; its size follows how much this colonist can carry (see CreateInventory).</summary>
    public InventoryGrid Inventory { get; private set; }
    /// <summary>Arrows stuck in the colonist (in its own frame).</summary>
    public readonly List<Remade.Sim.StuckArrow> Embedded = new();

    // world state
    public Vector2 Position;
    public float Facing;         // radians, 0 = +x (east), π/2 = +y (south)
    public Vector2 Velocity;     // cells per tick, for animation
    public ControlMode Mode;
    public PawnAnim Anim;
    public float AnimTime;       // ticks in the current anim
    public bool Aiming;
    public Vector2 AimDir = new(0, 1);
    public int WeaponCooldown;   // ticks until the weapon can fire again
    public Job Job;
    public readonly List<Vector2> Path = new();
    public int PathIndex;
    public string LastJobLabel = "Idle";

    public const float Radius = 0.28f;
    /// <summary>Walking speed, cells per tick at 1x (≈ 4.4 m/s of RimWorld scale, 60 ticks per second).</summary>
    public const float BaseSpeed = 4.4f / 60f;
    public const float SprintFactor = 1.65f;

    public string Name => string.IsNullOrEmpty(NickName) ? FirstName : NickName;
    public string FullName => string.IsNullOrEmpty(NickName) || NickName == FirstName
        ? $"{FirstName} {LastName}" : $"{FirstName} '{NickName}' {LastName}";
    public string AgeLabel => BioAge == ChronoAge ? $"{BioAge}" : $"{BioAge} ({ChronoAge})";
    public bool Dead => Health.Dead;

    public bool HasTrait(string id)
    {
        foreach (var t in Traits) if (t.Id == id) return true;
        return false;
    }

    // ------------------------------------------------------------------ stats

    /// <summary>Combined multiplier of every trait affecting a stat (1 = normal).</summary>
    public float StatFactor(Stat stat)
    {
        float f = 1f;
        foreach (var t in Traits)
            foreach (var (s, v) in t.Effects)
                if (s == stat) f *= v;
        return f;
    }

    // ------------------------------------------------------------------ inventory & equipment

    /// <summary>Body mass that load limits are measured against (body mass × carrying-capacity traits).</summary>
    public float CarryBasisKg => BodyMassKg * StatFactor(Stat.CarryCapacity);

    /// <summary>
    /// Creates the inventory grid. Its number of slots follows the approach-march load (45 % of the carry basis):
    /// roughly one 1×1 slot per kilogram, 8 columns wide.
    /// </summary>
    public void CreateInventory(int rows = -1)
    {
        if (Inventory != null) throw new InvalidOperationException($"{FullName} already has an inventory");
        if (rows < 0) rows = Math.Clamp((int)MathF.Ceiling(CarryBasisKg * MarchLoad / InventoryColumns), 3, 12);
        Inventory = new InventoryGrid("Inventory", InventoryColumns, rows);
    }

    public const int InventoryColumns = 8;

    public float CarriedMass
    {
        get
        {
            float m = Held?.TotalMass ?? 0f;
            foreach (var a in Apparel) m += a.TotalMass;
            if (Inventory != null) m += Inventory.Mass;
            return m;
        }
    }

    /// <summary>Load zones as a fraction of the carry basis (body mass), from load-carriage guidance:
    /// ≤ 25 % unencumbered (comfortable hiking pack), ≤ 45 % encumbered (military approach-march load),
    /// ≤ 70 % heavily encumbered, beyond that overloaded; nothing can be picked up past 100 %.</summary>
    public const float LightLoad = 0.25f, MarchLoad = 0.45f, HeavyLoad = 0.70f, MaxLoad = 1.0f;

    public Encumbrance EncumbranceLevel
    {
        get
        {
            float r = CarriedMass / CarryBasisKg;
            return r <= LightLoad ? Encumbrance.Unencumbered : r <= MarchLoad ? Encumbrance.Encumbered
                 : r <= HeavyLoad ? Encumbrance.Heavy : Encumbrance.Overloaded;
        }
    }

    public float SpeedFactorFromLoad
    {
        get
        {
            float r = CarriedMass / CarryBasisKg;
            if (r <= LightLoad) return 1f;
            if (r <= MarchLoad) return 1f - (r - LightLoad) / (MarchLoad - LightLoad) * 0.25f;
            if (r <= HeavyLoad) return 0.75f - (r - MarchLoad) / (HeavyLoad - MarchLoad) * 0.35f;
            return 0.25f;
        }
    }

    public float MoveSpeed(bool sprint)
    {
        float s = BaseSpeed * SpeedFactorFromLoad * StatFactor(Stat.MoveSpeed);
        if (sprint) s *= SprintFactor;
        return s;
    }

    public int CountInInventory(ThingDef def) => (Inventory?.CountOf(def) ?? 0) + (Held?.Def == def ? Held.Count : 0);

    /// <summary>Removes up to n units of def from the inventory. Returns how many were removed.</summary>
    public int ConsumeFromInventory(ThingDef def, int n)
    {
        int left = n;
        var g = Inventory;
        for (int i = g.Entries.Count - 1; i >= 0 && left > 0; i--)
        {
            var e = g.Entries[i];
            if (e.Item.Def != def) continue;
            int take = Math.Min(left, e.Item.Count);
            e.Item.Count -= take; left -= take;
            if (e.Item.Count == 0) g.Remove(e.Item);
        }
        return n - left;
    }

    /// <summary>Apparel already occupying any (layer, region) pair the def needs.</summary>
    public List<Item> WearConflicts(ThingDef def)
    {
        if (def.Kind != ThingKind.Apparel) throw new ArgumentException($"{def.Id} is not apparel");
        var list = new List<Item>();
        foreach (var a in Apparel)
        {
            bool clash = false;
            foreach (var l in def.Layers)
                foreach (var r in def.Covers)
                    if (Array.IndexOf(a.Def.Layers, l) >= 0 && Array.IndexOf(a.Def.Covers, r) >= 0) clash = true;
            if (clash) list.Add(a);
        }
        return list;
    }

    public void Wear(Item item)
    {
        Invariant.Check(item.Def.Kind == ThingKind.Apparel, $"{item} is not apparel");
        Invariant.Check(!item.Spawned, $"{item} must be despawned before wearing");
        var c = WearConflicts(item.Def);
        Invariant.Check(c.Count == 0, $"{FullName} cannot wear {item}: conflicts with {string.Join(", ", c)}");
        Apparel.Add(item);
    }

    /// <summary>Apparel worn on a layer and region (for the body figure), or null.</summary>
    public Item WornAt(ApparelLayer layer, BodyRegion region)
    {
        foreach (var a in Apparel)
            if (Array.IndexOf(a.Def.Layers, layer) >= 0 && Array.IndexOf(a.Def.Covers, region) >= 0) return a;
        return null;
    }

    public bool HasRangedWeapon => Held != null && Held.Def.Weapon == WeaponKind.Bow;
    public int Arrows => CountInInventory(Defs.Arrow);

    public override string ToString() => $"{Name}#{Id}";
}
