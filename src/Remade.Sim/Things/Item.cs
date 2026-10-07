using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Diagnostics;

namespace Remade.Things;

/// <summary>A stack of identical items. Lives either on the ground (Spawned, Position) or in an inventory/equipment slot.</summary>
public sealed class Item
{
    public int Id;
    public ThingDef Def;
    public int Count;
    public bool Spawned;
    public Vector2 Position;
    /// <summary>Random yaw for ground rendering (radians).</summary>
    public float Rotation;
    /// <summary>An arrow stuck in the ground, a trunk or a wall: the height of its tip (NaN when lying flat) and the
    /// direction it points (x east, y south, z up).</summary>
    public float StuckZ = float.NaN;
    public Vector3 StuckDir;
    public bool Stuck => !float.IsNaN(StuckZ);
    /// <summary>Storage provided by containers (satchel, pockets); travels with the item.</summary>
    public readonly InventoryGrid Contents;

    public Item(int id, ThingDef def, int count)
    {
        if (count < 1 || count > def.StackLimit)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"{def.Id}: count must be 1..{def.StackLimit}");
        Id = id; Def = def; Count = count;
        if (def.ContainerW > 0) Contents = new InventoryGrid(def.ContainerLabel ?? def.Label, def.ContainerW, def.ContainerH);
    }

    /// <summary>Mass of the stack itself (without contents).</summary>
    public float Mass => Def.Mass * Count;
    public float TotalMass => Mass + (Contents?.Mass ?? 0f);
    public string Label => Count > 1 ? $"{Def.Label} x{Count}" : Def.Label;
    public override string ToString() => $"{Def.Id}#{Id}x{Count}";
}

/// <summary>One placed stack in an inventory grid.</summary>
public struct GridEntry
{
    public Item Item;
    public int X, Y;
    public bool Rotated;
    public int W => Rotated ? Item.Def.GridH : Item.Def.GridW;
    public int H => Rotated ? Item.Def.GridW : Item.Def.GridH;
}

/// <summary>
/// A Stalker / Project Zomboid style grid container (pockets, satchel…). Each stack occupies its footprint;
/// stacks of the same definition merge first. Occupancy is tracked per cell for O(1) fit checks.
/// </summary>
public sealed class InventoryGrid
{
    public readonly string Label;
    public readonly int W, H;
    public readonly List<GridEntry> Entries = new();
    readonly int[] _occ; // entry index + 1, 0 = free

    public InventoryGrid(string label, int w, int h)
    {
        if (w < 1 || h < 1 || w > 20 || h > 20) throw new ArgumentOutOfRangeException(nameof(w), $"{w}x{h}", "grid size 1..20");
        Label = label; W = w; H = h;
        _occ = new int[w * h];
    }

    public float Mass
    {
        get { float m = 0; foreach (var e in Entries) m += e.Item.Mass; return m; }
    }

    public bool Fits(int x, int y, int w, int h, int ignoreEntry = -1)
    {
        if (x < 0 || y < 0 || x + w > W || y + h > H) return false;
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                int o = _occ[yy * W + xx];
                if (o != 0 && o - 1 != ignoreEntry) return false;
            }
        return true;
    }

    public bool FindSpot(ThingDef def, out int fx, out int fy, out bool rotated)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool rot = pass == 1;
            if (rot && def.GridW == def.GridH) break;
            int w = rot ? def.GridH : def.GridW, h = rot ? def.GridW : def.GridH;
            for (int y = 0; y + h <= H; y++)
                for (int x = 0; x + w <= W; x++)
                    if (Fits(x, y, w, h)) { fx = x; fy = y; rotated = rot; return true; }
        }
        fx = fy = -1; rotated = false;
        return false;
    }

    /// <summary>
    /// Takes as many units of <paramref name="item"/> as fit: merges into existing stacks first, then places new stacks
    /// (new Item objects) in free spots. Decrements item.Count; the caller disposes of the source when it reaches 0.
    /// </summary>
    public int Absorb(Item item, Func<int> newId)
    {
        int taken = 0;
        for (int i = 0; i < Entries.Count && item.Count > 0; i++)
        {
            var e = Entries[i];
            if (e.Item.Def != item.Def || e.Item.Count >= item.Def.StackLimit) continue;
            int n = Math.Min(item.Count, item.Def.StackLimit - e.Item.Count);
            e.Item.Count += n; item.Count -= n; taken += n;
        }
        while (item.Count > 0 && FindSpot(item.Def, out int x, out int y, out bool rot))
        {
            int n = Math.Min(item.Count, item.Def.StackLimit);
            Place(new Item(newId(), item.Def, n), x, y, rot);
            item.Count -= n; taken += n;
        }
        return taken;
    }

    /// <summary>Places this exact item object (keeps its identity) if a spot exists. For unique items.</summary>
    public bool TryInsert(Item item)
    {
        if (!FindSpot(item.Def, out int x, out int y, out bool rot)) return false;
        Place(item, x, y, rot);
        return true;
    }

    public void Place(Item item, int x, int y, bool rotated)
    {
        var e = new GridEntry { Item = item, X = x, Y = y, Rotated = rotated };
        Invariant.Check(Fits(x, y, e.W, e.H), $"Place {item} at {x},{y} in {Label}: does not fit");
        Entries.Add(e);
        Mark(Entries.Count - 1, e, Entries.Count);
    }

    public bool Remove(Item item)
    {
        int idx = Entries.FindIndex(e => e.Item == item);
        if (idx < 0) return false;
        Entries.RemoveAt(idx);
        Rebuild();
        return true;
    }

    /// <summary>Moves a stack within the grid (drag and drop). Returns false if it does not fit there.</summary>
    public bool Move(Item item, int x, int y, bool rotated)
    {
        int idx = Entries.FindIndex(e => e.Item == item);
        if (idx < 0) throw new InvalidOperationException($"{item} is not in {Label}");
        var e = Entries[idx];
        e.X = x; e.Y = y; e.Rotated = rotated;
        if (!Fits(x, y, e.W, e.H, idx)) return false;
        Entries[idx] = e;
        Rebuild();
        return true;
    }

    public int EntryAt(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? -1 : _occ[y * W + x] - 1;

    public int CountOf(ThingDef def)
    {
        int c = 0;
        foreach (var e in Entries) if (e.Item.Def == def) c += e.Item.Count;
        return c;
    }

    void Mark(int idx, GridEntry e, int value)
    {
        for (int yy = e.Y; yy < e.Y + e.H; yy++)
            for (int xx = e.X; xx < e.X + e.W; xx++)
                _occ[yy * W + xx] = value;
    }

    void Rebuild()
    {
        Array.Clear(_occ);
        for (int i = 0; i < Entries.Count; i++) Mark(i, Entries[i], i + 1);
    }
}
