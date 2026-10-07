using System;
using System.Numerics;
using Remade.Core;
using Remade.Map;
using Remade.Pawns;

namespace Remade.Sim;

public sealed partial class GameSim
{
    /// <summary>Corpses fade away after this many ticks (the meat has already been dropped).</summary>
    public const int CorpseTicks = 1500;
    const float LodDistance = 70f;

    void TickAnimal(Animal a)
    {
        a.AnimTime++;
        if (a.Dead)
        {
            if (Tick - a.DeathTick > CorpseTicks && a.DeathTick >= 0)
            {
                a.DeathTick = -2; // mark as gone
                _corpsesToPurge = true;
                Events.Add(new SimEvent(SimEventKind.AnimalDespawned, a.Position, null, a.Id));
            }
            return;
        }

        if (Tick % NeedsInterval == a.Id % NeedsInterval)
        {
            float hours = NeedsInterval / (float)GameTime.TicksPerHour;
            if (a.Health.TickBleeding(hours)) { KillAnimal(a); return; }
            // level of detail: animals far from the camera and from every colonist think rarely
            float near = Vector2.DistanceSquared(a.Position, Focus);
            foreach (var p in Pawns) near = MathF.Min(near, Vector2.DistanceSquared(a.Position, p.Position));
            a.Far = near > LodDistance * LodDistance;
        }

        if (--a.ThinkTimer <= 0)
        {
            a.ThinkTimer = a.Far ? 90 : 20;
            if (a.State != AnimalState.Flee) CheckThreats(a);
        }

        Vector2 before = a.Position;
        switch (a.State)
        {
            case AnimalState.Graze:
            case AnimalState.Rest:
                if (--a.StateTimer <= 0) ChooseActivity(a);
                break;
            case AnimalState.Wander:
            case AnimalState.Flee:
                if (a.PathIndex >= a.Path.Count) { a.Path.Clear(); a.PathIndex = 0; ChooseActivity(a); break; }
                float speed = a.State == AnimalState.Flee ? Animal.FleeSpeed : Animal.WalkSpeed;
                speed *= MathF.Max(0.35f, 1f - a.Health.BloodLoss * 0.8f); // wounded deer slow down
                StepAnimal(a, speed);
                if (a.State == AnimalState.Flee && --a.StateTimer <= 0 && a.PathIndex >= a.Path.Count - 1) ChooseActivity(a);
                break;
        }
        a.Velocity = a.Position - before;
    }

    void StepAnimal(Animal a, float speed)
    {
        int cell = Map.CellAt(a.Position);
        if (cell >= 0) speed *= 10f / Math.Max(10, (int)Paths.Cost[cell]);
        float budget = speed;
        while (budget > 0 && a.PathIndex < a.Path.Count)
        {
            Vector2 wp = a.Path[a.PathIndex];
            int wc = Map.CellAt(wp);
            if (wc >= 0 && Map.Blocked(wc)) { a.Path.Clear(); a.PathIndex = 0; return; } // a door closed in front of it
            Vector2 d = wp - a.Position;
            float len = d.Length();
            if (len > 1e-4f)
            {
                // turn smoothly toward the heading
                float want = MathF.Atan2(d.Y, d.X);
                float diff = MathF.IEEERemainder(want - a.Facing, MathF.Tau);
                a.Facing += Math.Clamp(diff, -0.25f, 0.25f);
            }
            if (len <= budget) { a.Position = wp; budget -= len; a.PathIndex++; }
            else { a.Position += d / len * budget; budget = 0; }
        }
    }

    void CheckThreats(Animal a)
    {
        foreach (var p in Pawns)
        {
            if (p.Dead) continue;
            float d = Vector2.Distance(a.Position, p.Position);
            // running or aiming colonists are noticed from further away; sleeping ones not at all
            float alert = p.Anim switch
            {
                PawnAnim.Run => 15f, PawnAnim.Walk => 9f, PawnAnim.Aim or PawnAnim.Shoot => 11f, PawnAnim.Sleep => 0f, _ => 6f,
            };
            if (a.State == AnimalState.Rest) alert *= 0.6f;
            if (d < alert) { Spook(a, p.Position, alertHerd: true); return; }
        }
    }

    /// <summary>Makes an animal run away from a point (and optionally its herd with it).</summary>
    void Spook(Animal a, Vector2 from, bool alertHerd = false)
    {
        if (a.Dead) return;
        bool wasFleeing = a.State == AnimalState.Flee;
        Vector2 away = a.Position - from;
        away = away.LengthSquared() < 1e-4f ? new Vector2(Rng.Range(-1f, 1f), Rng.Range(-1f, 1f)) : Vector2.Normalize(away);
        for (int tries = 0; tries < 8; tries++)
        {
            float ang = MathF.Atan2(away.Y, away.X) + Rng.Range(-0.7f, 0.7f) * (1 + tries * 0.3f);
            var target = a.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * Rng.Range(16f, 26f);
            target.X = Math.Clamp(target.X, 2, Map.Width - 3);
            target.Y = Math.Clamp(target.Y, 2, Map.Height - 3);
            if (TryPathAnimal(a, target, 4000))
            {
                a.State = AnimalState.Flee;
                a.StateTimer = Rng.Range(200, 400);
                if (!wasFleeing) Events.Add(new SimEvent(SimEventKind.AnimalFled, a.Position, null, a.Id));
                break;
            }
        }
        if (alertHerd && !wasFleeing)
            foreach (var o in Animals)
                if (o != a && !o.Dead && o.Herd == a.Herd && o.State != AnimalState.Flee && Vector2.Distance(o.Position, a.Position) < 20f)
                    Spook(o, from);
    }

    void ChooseActivity(Animal a)
    {
        var herd = Herds[a.Herd];
        if (--herd.MoveTimer <= 0)
        {
            herd.MoveTimer = Rng.Range(20, 60);
            var na = herd.Anchor + new Vector2(Rng.Range(-14f, 14f), Rng.Range(-14f, 14f));
            int c = Map.CellAt(na);
            if (c >= 0 && !Map.Blocked(c) && !Map.TerrainAt(c).Water) herd.Anchor = na;
        }
        float hour = GameTime.HourOfDay(Tick);
        bool night = hour >= 22f || hour < 5f;
        float roll = Rng.NextFloat();
        if (night && roll < 0.7f) { a.State = AnimalState.Rest; a.StateTimer = Rng.Range(600, 2000); return; }
        // stray too far from the herd → walk back; otherwise mostly graze
        if (Vector2.Distance(a.Position, herd.Anchor) > 10f || roll < 0.35f)
        {
            var target = herd.Anchor + new Vector2(Rng.Range(-6f, 6f), Rng.Range(-6f, 6f));
            if (TryPathAnimal(a, target, 1500)) { a.State = AnimalState.Wander; return; }
        }
        a.State = AnimalState.Graze;
        a.StateTimer = Rng.Range(240, 900);
    }

    bool TryPathAnimal(Animal a, Vector2 target, int maxExpand)
    {
        int start = Map.CellAt(a.Position), goal = Map.CellAt(target);
        if (start < 0 || goal < 0 || Map.Blocked(goal) || Map.TerrainAt(goal).Water) return false;
        a.Path.Clear(); a.PathIndex = 0;
        if (Pathfinder.FindPath(start, goal, a.Path, maxExpand, doorsBlock: true) != PathResult.Found) { a.Path.Clear(); return false; }
        return true;
    }

    bool _corpsesToPurge;

    void PurgeCorpses()
    {
        if (!_corpsesToPurge) return;
        _corpsesToPurge = false;
        Animals.RemoveAll(a => a.DeathTick == -2);
    }
}
