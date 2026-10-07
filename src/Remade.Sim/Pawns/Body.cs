using System;
using System.Collections.Generic;
using Remade.Core;
using Remade.Things;

namespace Remade.Pawns;

[Flags]
public enum PartFlags : byte { None = 0, Vital = 1, NeedOne = 2, Organ = 4, Bone = 8 }

public sealed class BodyPartDef
{
    public int Index;
    public string Name;
    public int Parent = -1;
    public float MaxHp;
    /// <summary>Relative chance to be the part struck by an attack.</summary>
    public float HitWeight;
    public BodyRegion Region;
    public PartFlags Flags;
    /// <summary>Organs that are "need one of": parts sharing a group (lungs, kidneys).</summary>
    public string Group;
    public bool Vital => (Flags & PartFlags.Vital) != 0;
}

/// <summary>Anatomy of a creature: a tree of parts (as in RimWorld's human body), each in a coarse body region.</summary>
public sealed class BodyDef
{
    public readonly string Name;
    public readonly List<BodyPartDef> Parts = new();
    public float TotalHitWeight { get; private set; }
    /// <summary>Fraction of the core (root + vital) hit points that, once dealt as damage, kills by shock.</summary>
    public readonly float ShockFraction;

    BodyDef(string name, float shock) { Name = name; ShockFraction = shock; }

    int Add(string name, int parent, float hp, float hit, BodyRegion region, PartFlags flags = PartFlags.None, string group = null)
    {
        var p = new BodyPartDef { Index = Parts.Count, Name = name, Parent = parent, MaxHp = hp, HitWeight = hit, Region = region, Flags = flags, Group = group };
        Parts.Add(p);
        TotalHitWeight += hit;
        return p.Index;
    }

    public BodyPartDef this[int i] => Parts[i];
    public int Count => Parts.Count;

    public int Find(string name)
    {
        for (int i = 0; i < Parts.Count; i++) if (Parts[i].Name == name) return i;
        throw new KeyNotFoundException($"{Name} has no part '{name}'");
    }

    public IEnumerable<BodyPartDef> InRegion(BodyRegion r)
    {
        foreach (var p in Parts) if (p.Region == r) yield return p;
    }

    public int PickHitPart(ref Rng rng)
    {
        float roll = rng.NextFloat() * TotalHitWeight;
        foreach (var p in Parts)
        {
            roll -= p.HitWeight;
            if (roll <= 0) return p.Index;
        }
        return Parts.Count - 1;
    }

    // ------------------------------------------------------------------ definitions

    public static readonly BodyDef Human = BuildHuman();
    public static readonly BodyDef Deer = BuildDeer();

    static BodyDef BuildHuman()
    {
        var b = new BodyDef("Human", 0.55f);
        const PartFlags V = PartFlags.Vital, O = PartFlags.Organ, B = PartFlags.Bone, N = PartFlags.NeedOne;
        int torso = b.Add("Torso", -1, 40, 18, BodyRegion.Torso, V);
        int neck = b.Add("Neck", torso, 25, 4, BodyRegion.Neck, V);
        int head = b.Add("Head", neck, 25, 4, BodyRegion.Head, V);
        int skull = b.Add("Skull", head, 25, 1.5f, BodyRegion.Head, B);
        b.Add("Brain", skull, 10, 1, BodyRegion.Head, V | O);
        b.Add("Left eye", head, 10, 0.5f, BodyRegion.Eyes, O);
        b.Add("Right eye", head, 10, 0.5f, BodyRegion.Eyes, O);
        b.Add("Left ear", head, 12, 0.6f, BodyRegion.Head);
        b.Add("Right ear", head, 12, 0.6f, BodyRegion.Head);
        b.Add("Nose", head, 10, 0.6f, BodyRegion.Head);
        int jaw = b.Add("Jaw", head, 20, 1f, BodyRegion.Head, B);
        b.Add("Tongue", jaw, 10, 0.3f, BodyRegion.Head, O);
        b.Add("Sternum", torso, 20, 1.5f, BodyRegion.Torso, B);
        b.Add("Ribcage", torso, 30, 3f, BodyRegion.Torso, B);
        b.Add("Spine", torso, 25, 1.5f, BodyRegion.Torso, B);
        b.Add("Stomach", torso, 20, 1.5f, BodyRegion.Torso, O);
        b.Add("Heart", torso, 15, 1f, BodyRegion.Torso, V | O);
        b.Add("Left lung", torso, 15, 1.5f, BodyRegion.Torso, N | O, "lungs");
        b.Add("Right lung", torso, 15, 1.5f, BodyRegion.Torso, N | O, "lungs");
        b.Add("Left kidney", torso, 15, 1f, BodyRegion.Torso, N | O, "kidneys");
        b.Add("Right kidney", torso, 15, 1f, BodyRegion.Torso, N | O, "kidneys");
        b.Add("Liver", torso, 20, 1.5f, BodyRegion.Torso, V | O);
        b.Add("Waist", torso, 10, 1.5f, BodyRegion.Waist);
        b.Add("Pelvis", torso, 25, 2f, BodyRegion.Waist, B);
        foreach (var side in new[] { "Left", "Right" })
        {
            bool l = side == "Left";
            b.Add($"{side} clavicle", torso, 25, 1f, l ? BodyRegion.ShoulderL : BodyRegion.ShoulderR, B);
            int shoulder = b.Add($"{side} shoulder", torso, 30, 3f, l ? BodyRegion.ShoulderL : BodyRegion.ShoulderR);
            int arm = b.Add($"{side} arm", shoulder, 30, 5f, l ? BodyRegion.ArmL : BodyRegion.ArmR);
            b.Add($"{side} humerus", arm, 25, 1f, l ? BodyRegion.ArmL : BodyRegion.ArmR, B);
            b.Add($"{side} radius", arm, 20, 1f, l ? BodyRegion.ArmL : BodyRegion.ArmR, B);
            int hand = b.Add($"{side} hand", arm, 20, 2f, l ? BodyRegion.HandL : BodyRegion.HandR);
            foreach (var f in new[] { "thumb", "index finger", "middle finger", "ring finger", "pinky" })
                b.Add($"{side} {f}", hand, 8, 0.4f, l ? BodyRegion.HandL : BodyRegion.HandR);
        }
        foreach (var side in new[] { "Left", "Right" })
        {
            bool l = side == "Left";
            int leg = b.Add($"{side} leg", torso, 30, 8f, l ? BodyRegion.LegL : BodyRegion.LegR);
            b.Add($"{side} femur", leg, 25, 1.5f, l ? BodyRegion.LegL : BodyRegion.LegR, B);
            b.Add($"{side} tibia", leg, 25, 1.5f, l ? BodyRegion.LegL : BodyRegion.LegR, B);
            int foot = b.Add($"{side} foot", leg, 25, 2f, l ? BodyRegion.FootL : BodyRegion.FootR);
            foreach (var t in new[] { "big toe", "second toe", "third toe", "fourth toe", "little toe" })
                b.Add($"{side} {t}", foot, 8, 0.3f, l ? BodyRegion.FootL : BodyRegion.FootR);
        }
        return b;
    }

    static BodyDef BuildDeer()
    {
        var b = new BodyDef("Deer", 0.3f);
        const PartFlags V = PartFlags.Vital, O = PartFlags.Organ, N = PartFlags.NeedOne;
        int body = b.Add("Body", -1, 45, 30, BodyRegion.Torso, V);
        int neck = b.Add("Neck", body, 22, 6, BodyRegion.Neck, V);
        int head = b.Add("Head", neck, 20, 5, BodyRegion.Head, V);
        b.Add("Brain", head, 8, 1.2f, BodyRegion.Head, V | O);
        b.Add("Heart", body, 12, 2.5f, BodyRegion.Torso, V | O);
        b.Add("Left lung", body, 14, 3f, BodyRegion.Torso, N | O, "lungs");
        b.Add("Right lung", body, 14, 3f, BodyRegion.Torso, N | O, "lungs");
        b.Add("Liver", body, 16, 2f, BodyRegion.Torso, V | O);
        foreach (var leg in new[] { "Front left leg", "Front right leg", "Hind left leg", "Hind right leg" })
            b.Add(leg, body, 22, 6f, BodyRegion.LegL);
        return b;
    }
}

public struct Injury
{
    public int Part;
    public float Damage;
    public float BleedPerHour; // fraction of total blood per game hour
    public long Tick;
}

/// <summary>
/// Health of a creature: per-part hit points, injuries, bleeding and death rules
/// (vital part destroyed, every "need one" organ of a group destroyed, blood loss, or overwhelming damage).
/// </summary>
public sealed class Health
{
    public readonly BodyDef Body;
    public readonly float[] Hp;
    public readonly List<Injury> Injuries = new();
    public float BloodLoss; // 0..1, death at 1
    public bool Dead { get; private set; }
    public string DeathCause { get; private set; }

    public Health(BodyDef body)
    {
        Body = body;
        Hp = new float[body.Count];
        for (int i = 0; i < body.Count; i++) Hp[i] = body[i].MaxHp;
    }

    public float Condition(int part) => Math.Clamp(Hp[part] / Body[part].MaxHp, 0f, 1f);

    /// <summary>Overall health 0..1, weighting parts by their max HP.</summary>
    public float Summary
    {
        get
        {
            float cur = 0, max = 0;
            for (int i = 0; i < Hp.Length; i++) { cur += Hp[i]; max += Body[i].MaxHp; }
            return Math.Clamp(cur / max * (1f - BloodLoss), 0f, 1f);
        }
    }

    public float BleedRate { get { float b = 0; foreach (var j in Injuries) b += j.BleedPerHour; return b; } }

    /// <summary>Applies damage to a part (and spills overkill into its parent). Returns true if this killed the creature.</summary>
    public bool Damage(int part, float amount, long tick, float bleedFactor = 1f)
    {
        if (Dead) throw new InvalidOperationException($"{Body.Name}: damaging a dead creature");
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), amount, "damage must be positive");
        float applied = Math.Min(amount, Hp[part]);
        Hp[part] -= applied;
        Injuries.Add(new Injury { Part = part, Damage = applied, BleedPerHour = applied * 0.012f * bleedFactor, Tick = tick });
        float overkill = amount - applied;
        if (overkill > 0 && Body[part].Parent >= 0) Hp[Body[part].Parent] = Math.Max(0, Hp[Body[part].Parent] - overkill);
        return CheckDeath();
    }

    /// <summary>Advances bleeding by a number of game hours. Returns true if the creature bled out.</summary>
    public bool TickBleeding(float hours)
    {
        if (Dead) return false;
        float rate = BleedRate;
        if (rate <= 0) return false;
        BloodLoss = Math.Min(1f, BloodLoss + rate * hours);
        // wounds slowly clot
        for (int i = 0; i < Injuries.Count; i++)
        {
            var j = Injuries[i];
            j.BleedPerHour = Math.Max(0, j.BleedPerHour - 0.02f * hours);
            Injuries[i] = j;
        }
        return CheckDeath();
    }

    /// <summary>Recomputes the death state after loading part HP and injuries.</summary>
    public void Revalidate()
    {
        for (int i = 0; i < Hp.Length; i++)
            if (!float.IsFinite(Hp[i]) || Hp[i] < 0 || Hp[i] > Body[i].MaxHp)
                throw new System.IO.InvalidDataException($"{Body.Name}: part {Body[i].Name} has invalid HP {Hp[i]}");
        if (BloodLoss < 0 || BloodLoss > 1) throw new System.IO.InvalidDataException($"{Body.Name}: blood loss {BloodLoss}");
        Dead = false; DeathCause = null;
        CheckDeath();
    }

    bool CheckDeath()
    {
        if (Dead) return true;
        string cause = null;
        for (int i = 0; i < Hp.Length && cause == null; i++)
            if (Hp[i] <= 0 && Body[i].Vital) cause = $"{Body[i].Name} destroyed";
        if (cause == null)
        {
            var groups = new Dictionary<string, bool>();
            foreach (var p in Body.Parts)
                if (p.Group != null) groups[p.Group] = (groups.TryGetValue(p.Group, out bool any) && any) || Hp[p.Index] > 0;
            foreach (var kv in groups) if (!kv.Value) { cause = $"all {kv.Key} destroyed"; break; }
        }
        if (cause == null && BloodLoss >= 1f) cause = "blood loss";
        if (cause == null)
        {
            float dmg = 0, total = 0;
            foreach (var j in Injuries) dmg += j.Damage;
            for (int i = 0; i < Hp.Length; i++) if (Body[i].Parent < 0 || Body[i].Vital) total += Body[i].MaxHp;
            if (dmg >= total * Body.ShockFraction) cause = "shock from injuries";
        }
        if (cause != null) { Dead = true; DeathCause = cause; }
        return Dead;
    }
}
