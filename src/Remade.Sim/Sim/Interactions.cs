using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Map;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Sim;

public enum InteractionKind : byte { PickUp, Gather, Drink, OpenDoor, CloseDoor, Hunt, Attack, GoTo, Eat }

/// <summary>Something a pawn can do with a nearby thing. Shared by the E key, the right-click menu and the AI.</summary>
public sealed class Interaction
{
    public InteractionKind Kind;
    public string Label;
    public Vector2 Position;     // where to highlight
    public int Cell = -1;
    public Item Item;
    public Animal Animal;
    public bool Disabled;
    public string DisabledReason;

    public override string ToString() => Label;
}

public static class Interactions
{
    /// <summary>
    /// What the directly controlled pawn can do right now with the E key: things within reach, nearest first.
    /// When several are available the player cycles through them with the mouse wheel.
    /// </summary>
    public static List<Interaction> Nearby(GameSim sim, Pawn p)
    {
        var list = new List<Interaction>();
        var map = sim.Map;
        float reach = GameSim.Reach;
        foreach (var it in sim.ItemsNear(p.Position, reach))
            list.Add(new Interaction { Kind = InteractionKind.PickUp, Item = it, Position = it.Position, Label = $"Pick up {it.Label}" });

        int cx = (int)p.Position.X, cy = (int)p.Position.Y;
        int waterCell = -1; float waterD = float.MaxValue;
        for (int y = cy - 2; y <= cy + 2; y++)
            for (int x = cx - 2; x <= cx + 2; x++)
            {
                if (!map.InBounds(x, y)) continue;
                int c = map.Index(x, y);
                Vector2 cc = LocalMap.CellCenter(x, y);
                float d = Vector2.Distance(cc, p.Position);
                // a cell is in reach if its nearest point is within reach
                float edge = Vector2.Distance(new Vector2(Math.Clamp(p.Position.X, x, x + 1), Math.Clamp(p.Position.Y, y, y + 1)), p.Position);
                if (edge > reach * 0.75f) continue;
                if (map.Plants[c] == Plant.BerryBush && map.Berries[c] > 0)
                    list.Add(new Interaction { Kind = InteractionKind.Gather, Cell = c, Position = cc, Label = $"Gather berries ({map.Berries[c]})" });
                if (map.Buildings[c] == Building.Door)
                {
                    bool open = map.DoorOpen[c];
                    list.Add(new Interaction { Kind = open ? InteractionKind.CloseDoor : InteractionKind.OpenDoor, Cell = c, Position = cc, Label = open ? "Close door" : "Open door" });
                }
                if (map.IsFreshWater(c) && d < waterD) { waterD = d; waterCell = c; }
            }
        if (waterCell >= 0)
            list.Add(new Interaction { Kind = InteractionKind.Drink, Cell = waterCell, Position = map.CellCenter(waterCell), Label = "Drink water" });

        list.Sort((a, b) => Vector2.DistanceSquared(a.Position, p.Position).CompareTo(Vector2.DistanceSquared(b.Position, p.Position)));
        return list;
    }

    /// <summary>Options for the right-click menu on a cell (the pawn walks there first if needed).</summary>
    public static List<Interaction> ForCell(GameSim sim, Pawn p, int cell)
    {
        var list = new List<Interaction>();
        var map = sim.Map;
        Vector2 cc = map.CellCenter(cell);
        foreach (var a in sim.Animals)
        {
            if (a.Dead || Vector2.Distance(a.Position, cc) > 1.2f) continue;
            if (p.HasRangedWeapon)
            {
                var hunt = new Interaction { Kind = InteractionKind.Hunt, Animal = a, Position = a.Position, Label = $"Hunt {a.Label.ToLowerInvariant()}" };
                if (p.Arrows == 0) { hunt.Disabled = true; hunt.DisabledReason = "no arrows"; }
                list.Add(hunt);
            }
            list.Add(new Interaction { Kind = InteractionKind.Attack, Animal = a, Position = a.Position, Label = $"Attack {a.Label.ToLowerInvariant()} (melee)" });
        }
        foreach (var it in sim.ItemsAt(cell))
        {
            list.Add(new Interaction { Kind = InteractionKind.PickUp, Item = it, Position = it.Position, Label = $"Pick up {it.Label}" });
            if (it.Def.Kind == ThingKind.Food)
                list.Add(new Interaction { Kind = InteractionKind.Eat, Item = it, Position = it.Position, Label = $"Eat {it.Def.Label}" });
        }
        if (map.Plants[cell] == Plant.BerryBush)
        {
            var g = new Interaction { Kind = InteractionKind.Gather, Cell = cell, Position = cc, Label = $"Gather berries ({map.Berries[cell]})" };
            if (map.Berries[cell] == 0) { g.Disabled = true; g.DisabledReason = "no berries"; }
            list.Add(g);
        }
        if (map.Buildings[cell] == Building.Door)
            list.Add(new Interaction { Kind = map.DoorOpen[cell] ? InteractionKind.CloseDoor : InteractionKind.OpenDoor, Cell = cell, Position = cc, Label = map.DoorOpen[cell] ? "Close door" : "Open door" });
        if (map.IsFreshWater(cell))
            list.Add(new Interaction { Kind = InteractionKind.Drink, Cell = cell, Position = cc, Label = "Drink water" });
        if (sim.Paths.Cost[cell] != 0 && map.Buildings[cell] != Building.Door)
            list.Add(new Interaction { Kind = InteractionKind.GoTo, Cell = cell, Position = cc, Label = "Go here" });
        return list;
    }

    /// <summary>Starts the job for an interaction (player-ordered).</summary>
    public static void Execute(GameSim sim, Pawn p, Interaction i)
    {
        if (i.Disabled) throw new InvalidOperationException($"Interaction '{i.Label}' is disabled: {i.DisabledReason}");
        Diagnostics.Log.Action($"{p.Name}: {i.Label}");
        Job job = i.Kind switch
        {
            InteractionKind.PickUp => new Job { Kind = JobKind.PickUp, TargetItem = i.Item, Label = i.Label },
            InteractionKind.Gather => new Job { Kind = JobKind.Gather, TargetCell = i.Cell, Label = "Gathering berries", WorkTicks = 160 },
            InteractionKind.Drink => new Job { Kind = JobKind.Drink, TargetCell = i.Cell, Label = "Drinking", WorkTicks = 90 },
            InteractionKind.OpenDoor or InteractionKind.CloseDoor => new Job { Kind = JobKind.ToggleDoor, TargetCell = i.Cell, Label = i.Label },
            InteractionKind.Hunt => new Job { Kind = JobKind.Hunt, TargetAnimal = i.Animal, Label = i.Label },
            InteractionKind.Attack => new Job { Kind = JobKind.Melee, TargetAnimal = i.Animal, Label = i.Label },
            InteractionKind.GoTo => new Job { Kind = JobKind.Goto, TargetCell = i.Cell, Label = "Going" },
            InteractionKind.Eat => new Job { Kind = JobKind.PickUp, TargetItem = i.Item, Label = i.Label, EatAfter = true },
            _ => throw new ArgumentOutOfRangeException(nameof(i), i.Kind, "unknown interaction"),
        };
        job.Forced = true;
        sim.StartJob(p, job);
    }
}
