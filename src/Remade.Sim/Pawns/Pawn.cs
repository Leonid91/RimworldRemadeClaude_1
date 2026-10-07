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

public enum Passion : byte { None, Minor, Major }

public enum ControlMode : byte { Autonomous, Drafted, Direct }

/// <summary>Visible pose for the renderer.</summary>
public enum PawnAnim : byte { Idle, Walk, Run, Work, Drink, Eat, Sleep, Aim, Shoot, Swing, PickUp, Dead }

public enum Encumbrance : byte { Unencumbered, Encumbered, Heavy, Overloaded }

public sealed class TraitDef
{
    public string Id, Label, Description;
    public string[] Conflicts = Array.Empty<string>();
}

public static class Traits
{
    public static readonly TraitDef[] All =
    {
        new() { Id = "industrious", Label = "Industrious", Description = "Works with energy and focus.", Conflicts = new[] { "lazy" } },
        new() { Id = "lazy", Label = "Lazy", Description = "Prefers a slow pace.", Conflicts = new[] { "industrious" } },
        new() { Id = "optimist", Label = "Optimist", Description = "Sees the bright side of things.", Conflicts = new[] { "pessimist" } },
        new() { Id = "pessimist", Label = "Pessimist", Description = "Expects the worst.", Conflicts = new[] { "optimist" } },
        new() { Id = "tough", Label = "Tough", Description = "Shrugs off injuries.", Conflicts = new[] { "wimp" } },
        new() { Id = "wimp", Label = "Wimp", Description = "Feels every scratch.", Conflicts = new[] { "tough" } },
        new() { Id = "jogger", Label = "Jogger", Description = "Always moves at a brisk pace.", Conflicts = new[] { "slowpoke" } },
        new() { Id = "slowpoke", Label = "Slowpoke", Description = "Never in a hurry.", Conflicts = new[] { "jogger" } },
        new() { Id = "night_owl", Label = "Night owl", Description = "Most alive after dark." },
        new() { Id = "green_thumb", Label = "Green thumb", Description = "Loves plants and gardening." },
        new() { Id = "iron_stomach", Label = "Iron-stomached", Description = "Can eat almost anything." },
        new() { Id = "kind", Label = "Kind", Description = "Gentle with everyone.", Conflicts = new[] { "abrasive" } },
        new() { Id = "abrasive", Label = "Abrasive", Description = "Says what everyone else only thinks.", Conflicts = new[] { "kind" } },
        new() { Id = "careful_shooter", Label = "Careful shooter", Description = "Takes time to aim.", Conflicts = new[] { "trigger_happy" } },
        new() { Id = "trigger_happy", Label = "Trigger-happy", Description = "Shoots fast, misses often.", Conflicts = new[] { "careful_shooter" } },
        new() { Id = "brawler", Label = "Brawler", Description = "Prefers fists to arrows." },
        new() { Id = "nimble", Label = "Nimble", Description = "Light on their feet." },
        new() { Id = "ascetic", Label = "Ascetic", Description = "Needs very little.", Conflicts = new[] { "greedy" } },
        new() { Id = "greedy", Label = "Greedy", Description = "Always wants more.", Conflicts = new[] { "ascetic" } },
        new() { Id = "neurotic", Label = "Neurotic", Description = "Anxious but diligent." },
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
    public void Tick(float hours, bool asleep, float temperature, float exertion)
    {
        float metabolic = asleep ? 0.6f : exertion;
        float heat = 1f + MathF.Max(0f, temperature - 25f) * 0.06f; // sweating
        Food = Math.Clamp(Food - FoodPerHour * metabolic * hours, 0f, 1f);
        Thirst = Math.Clamp(Thirst - ThirstPerHour * (asleep ? 0.5f : exertion) * heat * hours, 0f, 1f);
        Rest = asleep ? Math.Clamp(Rest + RestGainPerHourAsleep * hours, 0f, 1f)
                      : Math.Clamp(Rest - RestPerHourAwake * (0.7f + 0.3f * exertion) * hours, 0f, 1f);
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
    public readonly Passion[] Passions = new Passion[(int)SkillId.Count];
    public readonly Health Health = new(BodyDef.Human);
    public readonly Needs Needs = new();

    // equipment
    public readonly List<Item> Apparel = new();
    public Item Weapon;

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

    // ------------------------------------------------------------------ inventory & equipment

    /// <summary>All containers (pockets, satchel…) provided by worn apparel, in wearing order.</summary>
    public IEnumerable<InventoryGrid> Containers
    {
        get { foreach (var a in Apparel) if (a.Contents != null) yield return a.Contents; }
    }

    public float CarriedMass
    {
        get
        {
            float m = Weapon?.Mass ?? 0f;
            foreach (var a in Apparel) { m += a.Mass; if (a.Contents != null) m += a.Contents.Mass; }
            return m;
        }
    }

    /// <summary>Encumbrance thresholds as a fraction of body mass, from military load guidance:
    /// ≤ 25 % unencumbered, ≤ 45 % (approach march load) encumbered, ≤ 70 % heavy, beyond that overloaded.</summary>
    public const float LightLoad = 0.25f, MarchLoad = 0.45f, HeavyLoad = 0.70f, MaxLoad = 1.0f;

    public Encumbrance EncumbranceLevel
    {
        get
        {
            float r = CarriedMass / BodyMassKg;
            return r <= LightLoad ? Encumbrance.Unencumbered : r <= MarchLoad ? Encumbrance.Encumbered
                 : r <= HeavyLoad ? Encumbrance.Heavy : Encumbrance.Overloaded;
        }
    }

    public float SpeedFactorFromLoad
    {
        get
        {
            float r = CarriedMass / BodyMassKg;
            if (r <= LightLoad) return 1f;
            if (r <= MarchLoad) return 1f - (r - LightLoad) / (MarchLoad - LightLoad) * 0.25f;
            if (r <= HeavyLoad) return 0.75f - (r - MarchLoad) / (HeavyLoad - MarchLoad) * 0.35f;
            return 0.25f;
        }
    }

    public float MoveSpeed(bool sprint)
    {
        float s = BaseSpeed * SpeedFactorFromLoad;
        if (HasTrait("jogger")) s *= 1.15f;
        if (HasTrait("slowpoke")) s *= 0.88f;
        if (HasTrait("nimble")) s *= 1.05f;
        if (sprint) s *= SprintFactor;
        return s;
    }

    public int CountInInventory(ThingDef def)
    {
        int c = 0;
        foreach (var g in Containers) c += g.CountOf(def);
        return c;
    }

    /// <summary>Removes up to n units of def from the containers. Returns how many were removed.</summary>
    public int ConsumeFromInventory(ThingDef def, int n)
    {
        int left = n;
        foreach (var g in Containers)
        {
            for (int i = g.Entries.Count - 1; i >= 0 && left > 0; i--)
            {
                var e = g.Entries[i];
                if (e.Item.Def != def) continue;
                int take = Math.Min(left, e.Item.Count);
                e.Item.Count -= take; left -= take;
                if (e.Item.Count == 0) g.Remove(e.Item);
            }
            if (left == 0) break;
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

    public bool HasRangedWeapon => Weapon != null && Weapon.Def.Weapon == WeaponKind.Bow;
    public int Arrows => CountInInventory(Defs.Arrow);

    public override string ToString() => $"{Name}#{Id}";
}
