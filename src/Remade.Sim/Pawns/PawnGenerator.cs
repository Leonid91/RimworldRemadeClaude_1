using System;
using System.Collections.Generic;
using Remade.Core;
using Remade.Things;

namespace Remade.Pawns;

/// <summary>Creates random colonists: names, ages (biological and chronological), body, looks, traits, skills, starting clothes.</summary>
public static class PawnGenerator
{
    static readonly string[] MaleNames =
    {
        "Aldric", "Bram", "Caspian", "Dorian", "Elias", "Fenwick", "Gideon", "Hollis", "Ivo", "Jasper", "Kellan", "Leon",
        "Magnus", "Nils", "Osric", "Piers", "Quentin", "Rowan", "Silas", "Tobias", "Ulric", "Vance", "Wendell", "Yorick",
    };
    static readonly string[] FemaleNames =
    {
        "Ada", "Brielle", "Calla", "Delphine", "Edda", "Freya", "Greta", "Hazel", "Iris", "Juno", "Keira", "Liesel",
        "Maren", "Nadia", "Odette", "Petra", "Quilla", "Rhea", "Saskia", "Tamsin", "Una", "Vera", "Wren", "Ysolde",
    };
    static readonly string[] LastNames =
    {
        "Ashdown", "Blackwood", "Calder", "Dunmore", "Everly", "Fairholm", "Garrow", "Hartley", "Ingram", "Kestrel",
        "Lockhart", "Marlowe", "Northcott", "Oakes", "Pendry", "Ravensworth", "Sallow", "Thorne", "Underhill", "Vexley",
        "Whitlock", "Yarrow",
    };
    static readonly string[] Nicks =
    {
        "Ash", "Badger", "Cricket", "Doc", "Finch", "Flint", "Gus", "Hawk", "Juniper", "Kit", "Moss", "Pip", "Rook", "Sparrow", "Tink", "Wick",
    };
    static readonly uint[] SkinTones = { 0xF2D3B3, 0xE8B98F, 0xD39C6F, 0xB07A50, 0x8A5A3B, 0x5E3B26 };
    static readonly uint[] HairColors = { 0x1E1A16, 0x3B2A1E, 0x5A3A22, 0x8B5A2B, 0xB8873E, 0xD9C08A, 0x9A9A9A, 0x6B2E1E };

    public static Pawn Generate(ref Rng rng, Func<int> newId)
    {
        var p = new Pawn { Id = newId() };
        p.Sex = rng.Chance(0.5f) ? Sex.Male : Sex.Female;
        p.FirstName = p.Sex == Sex.Male ? rng.Pick(MaleNames) : rng.Pick(FemaleNames);
        p.LastName = rng.Pick(LastNames);
        p.NickName = rng.Chance(0.35f) ? rng.Pick(Nicks) : p.FirstName;

        p.BioAge = Math.Clamp((int)(30 + rng.Gaussian() * 9), 18, 64);
        // some colonists spent years in cryptosleep: chronological age differs from biological age
        p.ChronoAge = rng.Chance(0.25f) ? p.BioAge + rng.Range(2, rng.Chance(0.3f) ? 400 : 40) : p.BioAge;

        bool male = p.Sex == Sex.Male;
        p.HeightM = Math.Clamp((male ? 1.76f : 1.63f) + rng.Gaussian() * 0.07f, 1.5f, 2.0f);
        float bmi = Math.Clamp(23.5f + rng.Gaussian() * 2.6f, 18.5f, 31f);
        p.BodyMassKg = MathF.Round(bmi * p.HeightM * p.HeightM, 1);
        p.SkinColor = rng.Pick(SkinTones);
        p.HairColor = p.BioAge > 55 && rng.Chance(0.6f) ? 0x9A9A9A : rng.Pick(HairColors);
        p.HairStyle = (byte)rng.Range(0, 4);

        RollTraits(p, ref rng);
        RollSkills(p, ref rng);
        Dress(p, ref rng, newId);
        return p;
    }

    static void RollTraits(Pawn p, ref Rng rng)
    {
        int n = rng.Range(1, 4);
        for (int guard = 0; p.Traits.Count < n && guard < 50; guard++)
        {
            var t = rng.Pick(Traits.All);
            if (p.Traits.Contains(t)) continue;
            bool clash = false;
            foreach (var have in p.Traits)
                if (Array.IndexOf(t.Conflicts, have.Id) >= 0 || Array.IndexOf(have.Conflicts, t.Id) >= 0) clash = true;
            if (!clash) p.Traits.Add(t);
        }
    }

    static void RollSkills(Pawn p, ref Rng rng)
    {
        // a couple of specialities, the rest mostly low-to-middling; older colonists know a bit more
        float ageBonus = Math.Clamp((p.BioAge - 20) / 10f, 0f, 3f);
        int special1 = rng.Range(0, (int)SkillId.Count), special2 = rng.Range(0, (int)SkillId.Count);
        for (int s = 0; s < (int)SkillId.Count; s++)
        {
            float v = MathF.Abs(rng.Gaussian()) * 3.5f + ageBonus + rng.Range(0f, 2f);
            if (s == special1) v += rng.Range(6f, 12f);
            if (s == special2) v += rng.Range(3f, 8f);
            if (rng.Chance(0.12f)) v = rng.Range(0f, 1.5f); // a few untouched skills
            p.Skills[s] = (byte)Math.Clamp((int)v, 0, 20);
        }
    }

    static void Dress(Pawn p, ref Rng rng, Func<int> newId)
    {
        p.Wear(new Item(newId(), Defs.TShirt, 1));
        p.Wear(new Item(newId(), Defs.Jeans, 1));
        p.CreateInventory();
    }

    public static List<Pawn> GenerateGroup(int count, int seed, Func<int> newId)
    {
        var rng = Rng.FromParts(seed, 4242);
        var list = new List<Pawn>();
        var used = new HashSet<string>();
        while (list.Count < count)
        {
            var p = Generate(ref rng, newId);
            // colonists are told apart by their display name (nickname or first name)
            if (!used.Add(p.FirstName) || !used.Add(p.Name) && p.Name != p.FirstName) continue;
            list.Add(p);
        }
        return list;
    }
}
