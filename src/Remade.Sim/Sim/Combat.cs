using System;
using System.Numerics;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Sim;

public sealed partial class GameSim
{
    public const float FistReach = 1.4f, FistDamage = 6f;
    public const float ArrowSpeed = 34f / 60f; // cells per tick (≈ 34 m/s at 1x)

    /// <summary>Range of the pawn's current attack: the bow's range with arrows, else melee reach.</summary>
    public static float AttackRange(Pawn p) => p.HasRangedWeapon && p.Arrows > 0 ? p.Held.Def.Range : FistReach;

    /// <summary>Angular spread (radians, 1 sigma) of a shot: better shooters are steadier; moving makes it worse.</summary>
    public static float ShotSpread(Pawn p, bool moving)
    {
        float deg = 6.5f - p.Skills[(int)SkillId.Shooting] * 0.24f;
        deg *= p.StatFactor(Stat.ShotSpread);
        if (moving) deg += 3f;
        return MathF.Max(0.6f, deg) * MathF.PI / 180f;
    }

    /// <summary>Shoots (bow with arrows) or swings (otherwise) in a direction.</summary>
    public void Attack(Pawn p, Vector2 dir, bool moving, bool forceMelee = false)
    {
        if (dir.LengthSquared() < 1e-6f) throw new ArgumentException("attack direction is zero");
        dir = Vector2.Normalize(dir);
        if (!forceMelee && p.HasRangedWeapon && p.Arrows > 0)
        {
            int used = p.ConsumeFromInventory(Defs.Arrow, 1);
            Invariant.Check(used == 1, $"{p.Name}: arrow count {p.Arrows} but none consumed");
            float spread = ShotSpread(p, moving);
            float angle = MathF.Atan2(dir.Y, dir.X) + Rng.Gaussian() * spread;
            var d = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float range = p.Held.Def.Range;
            Projectiles.Add(new Projectile
            {
                Start = p.Position + d * 0.45f, Pos = p.Position + d * 0.45f, Dir = d, Speed = ArrowSpeed,
                MaxDist = range * Rng.Range(1.0f, 1.12f), Damage = p.Held.Def.Damage * Rng.Range(0.85f, 1.25f), Shooter = p,
            });
            p.WeaponCooldown = (int)(p.Held.Def.Cooldown * p.StatFactor(Stat.AimTime));
            p.Anim = PawnAnim.Shoot; p.AnimTime = 0;
            Events.Add(new SimEvent(SimEventKind.ArrowFired, p.Position, null, p.Id));
            Log.Debug($"{p.Name} shoots: aim {MathF.Atan2(dir.Y, dir.X) * 57.3f:F0}° actual {angle * 57.3f:F0}° spread {spread * 57.3f:F1}°, arrows left {p.Arrows}");
            return;
        }

        // melee: fists, or whatever is held used as a club; hits deer and people alike
        float reach = FistReach;
        float dmg = (p.Held?.Def.Kind == ThingKind.Weapon ? 8f : FistDamage) * p.StatFactor(Stat.MeleeDamage);
        p.WeaponCooldown = 55;
        p.Anim = PawnAnim.Swing; p.AnimTime = 0;
        Events.Add(new SimEvent(SimEventKind.Swing, p.Position, null, p.Id));
        float hitChance = MathF.Min(0.95f, (0.55f + p.Skills[(int)SkillId.Melee] * 0.02f) * p.StatFactor(Stat.MeleeHitChance));
        float cone = MathF.Cos(50f * MathF.PI / 180f);
        Animal bestA = null; Pawn bestP = null; float bestD = float.MaxValue;
        foreach (var a in Animals)
        {
            if (a.Dead) continue;
            Vector2 to = a.Position - p.Position;
            float dist = to.Length() - Animal.Radius * a.Size;
            if (dist > reach || (to.LengthSquared() > 1e-4f && Vector2.Dot(Vector2.Normalize(to), dir) < cone)) continue;
            if (dist < bestD) { bestD = dist; bestA = a; }
        }
        foreach (var o in Pawns)
        {
            if (o == p || o.Dead) continue;
            Vector2 to = o.Position - p.Position;
            float dist = to.Length() - Pawn.Radius;
            if (dist > reach || (to.LengthSquared() > 1e-4f && Vector2.Dot(Vector2.Normalize(to), dir) < cone)) continue;
            if (dist < bestD) { bestD = dist; bestP = o; bestA = null; }
        }
        if (bestA == null && bestP == null) return;
        Vector2 targetPos = bestP?.Position ?? bestA.Position;
        if (!Rng.Chance(hitChance))
        {
            Log.Debug($"{p.Name} swings and misses");
            if (bestA != null) Spook(bestA, p.Position);
            return;
        }
        if (bestP != null) HitPawn(bestP, dmg * Rng.Range(0.8f, 1.2f), p, bleed: 0.4f, "punch");
        else HitAnimal(bestA, dmg * Rng.Range(0.8f, 1.2f), p.Position, bleed: 0.6f);
        Events.Add(new SimEvent(SimEventKind.MeleeHit, targetPos, null, bestP?.Id ?? bestA.Id));
    }

    /// <summary>Wounds a colonist (arrow or blow). Damage is scaled by their Damage taken stat (Tough, Delicate).</summary>
    void HitPawn(Pawn victim, float damage, Pawn attacker, float bleed, string what)
    {
        damage *= victim.StatFactor(Stat.DamageTaken);
        int part = victim.Health.Body.PickHitPart(ref Rng);
        string partName = victim.Health.Body[part].Name.ToLowerInvariant();
        bool died = victim.Health.Damage(part, damage, Tick, bleed);
        string by = attacker != null ? $"{attacker.Name}'s {what}" : $"A {what}";
        Message($"{by} hits {victim.Name} in the {partName}" + (died ? $" — {victim.Name} is killed." : "."), victim.Position);
        Log.Info($"{victim} hit in the {partName} for {damage:F1} by {attacker?.ToString() ?? "-"} ({what}); health {victim.Health.Summary:P0}{(died ? ", killed: " + victim.Health.DeathCause : "")}");
        if (died) Die(victim);
    }

    void TickProjectiles()
    {
        for (int i = Projectiles.Count - 1; i >= 0; i--)
        {
            var pr = Projectiles[i];
            StepProjectile(pr);
            if (pr.Done) Projectiles.RemoveAt(i);
        }
    }

    void StepProjectile(Projectile pr)
    {
        float step = MathF.Min(pr.Speed, pr.MaxDist - pr.Traveled);
        Vector2 a = pr.Pos, b = pr.Pos + pr.Dir * step;

        // creatures along the segment (closest first)
        Animal hit = null; float hitT = float.MaxValue;
        foreach (var an in Animals)
        {
            if (an.Dead) continue;
            float t = SegmentCircle(a, b, an.Position, Animal.Radius * an.Size);
            if (t >= 0 && t < hitT) { hitT = t; hit = an; }
        }
        Pawn hitPawn = null;
        foreach (var o in Pawns)
        {
            if (o.Dead || o == pr.Shooter) continue;
            float t = SegmentCircle(a, b, o.Position, Pawn.Radius + 0.05f);
            if (t >= 0 && t < hitT) { hitT = t; hitPawn = o; hit = null; }
        }
        // solid cells and tree trunks along the segment
        float blockT = float.MaxValue; bool tree = false;
        int samples = Math.Max(1, (int)MathF.Ceiling(step / 0.25f));
        for (int s = 1; s <= samples; s++)
        {
            float t = s / (float)samples;
            int cell = Map.CellAt(Vector2.Lerp(a, b, t));
            if (cell < 0) { blockT = t; break; }
            if (Map.BlocksProjectiles(cell)) { blockT = t; break; }
            if (Map.Plants[cell] == Plant.Oak && Vector2.Distance(Vector2.Lerp(a, b, t), Map.CellCenter(cell)) < TrunkRadius(cell) + 0.05f
                && pr.Traveled > 1.5f && Rng.Chance(0.5f)) { blockT = t; tree = true; break; }
        }

        if (hitPawn != null && hitT * step <= blockT * step)
        {
            pr.Done = true;
            Vector2 at = Vector2.Lerp(a, b, hitT);
            Events.Add(new SimEvent(SimEventKind.ArrowHit, at, null, hitPawn.Id));
            HitPawn(hitPawn, pr.Damage, pr.Shooter, bleed: 2.2f, "arrow");
            return;
        }
        if (hit != null && hitT * step <= blockT * step)
        {
            pr.Done = true;
            Vector2 at = Vector2.Lerp(a, b, hitT);
            hit.EmbeddedArrows++;
            Events.Add(new SimEvent(SimEventKind.ArrowHit, at, null, hit.Id));
            HitAnimal(hit, pr.Damage, pr.Start, bleed: 2.2f);
            return;
        }
        if (blockT <= 1f)
        {
            pr.Done = true;
            Vector2 at = Vector2.Lerp(a, b, MathF.Max(0f, blockT - 0.3f / MathF.Max(step, 0.01f)));
            Events.Add(new SimEvent(SimEventKind.ArrowMissed, at, tree ? "tree" : "wall"));
            if (Rng.Chance(0.6f)) DropArrow(at);
            return;
        }
        pr.Pos = b;
        pr.Traveled += step;
        if (pr.Traveled >= pr.MaxDist - 1e-4f)
        {
            pr.Done = true;
            Events.Add(new SimEvent(SimEventKind.ArrowMissed, b, "ground"));
            // arrows landing near animals spook them
            foreach (var an in Animals) if (!an.Dead && Vector2.Distance(an.Position, b) < 6f) Spook(an, pr.Start);
            int cell = Map.CellAt(b);
            if (cell >= 0 && !Map.TerrainAt(cell).Water && Rng.Chance(0.75f)) DropArrow(b);
        }
    }

    void DropArrow(Vector2 at)
    {
        int cell = Map.CellAt(at);
        if (cell < 0 || Map.TerrainAt(cell).Water) return;
        // merge with an arrow stack already lying there
        foreach (var it in ItemsAt(cell))
            if (it.Def == Defs.Arrow && it.Count < Defs.Arrow.StackLimit) { it.Count++; return; }
        SpawnItem(Defs.Arrow, 1, at);
    }

    /// <summary>Returns the segment parameter t∈[0,1] of the first intersection with a circle, or -1.</summary>
    static float SegmentCircle(Vector2 a, Vector2 b, Vector2 c, float r)
    {
        Vector2 d = b - a, f = a - c;
        float A = Vector2.Dot(d, d);
        if (A < 1e-9f) return f.LengthSquared() <= r * r ? 0f : -1f;
        float B = 2 * Vector2.Dot(f, d), C = Vector2.Dot(f, f) - r * r;
        if (C <= 0) return 0f;
        float disc = B * B - 4 * A * C;
        if (disc < 0) return -1f;
        float t = (-B - MathF.Sqrt(disc)) / (2 * A);
        return t >= 0 && t <= 1 ? t : -1f;
    }

    void HitAnimal(Animal a, float damage, Vector2 from, float bleed)
    {
        int part = a.Health.Body.PickHitPart(ref Rng);
        string partName = a.Health.Body[part].Name;
        bool died = a.Health.Damage(part, damage, Tick, bleed);
        Log.Info($"{a} hit in the {partName} for {damage:F1} (condition {a.Health.Condition(part):P0}, blood loss {a.Health.BloodLoss:P0}){(died ? " — killed: " + a.Health.DeathCause : "")}");
        if (died) KillAnimal(a);
        else Spook(a, from, alertHerd: true);
    }

    void KillAnimal(Animal a)
    {
        a.State = AnimalState.Dead;
        a.DeathTick = Tick;
        a.Path.Clear(); a.PathIndex = 0;
        a.Velocity = Vector2.Zero;
        // butchered on the spot: a deer yields 15–25 kg of venison (0.5 kg per unit)
        int meat = (int)(Rng.Range(30, 46) * a.Size);
        SpawnItem(Defs.Venison, meat, a.Position);
        int arrows = 0;
        for (int i = 0; i < a.EmbeddedArrows; i++) if (Rng.Chance(0.5f)) arrows++;
        if (arrows > 0) SpawnItem(Defs.Arrow, arrows, a.Position + new Vector2(0.4f, 0.2f));
        Events.Add(new SimEvent(SimEventKind.AnimalKilled, a.Position, $"{a.Label} killed — {meat * Defs.Venison.Mass:F1} kg of venison", a.Id));
        Log.Info($"{a} died ({a.Health.DeathCause}) at {a.Position}: dropped {meat} venison, {arrows} arrows recovered");
        foreach (var other in Animals) if (!other.Dead && other.Herd == a.Herd) Spook(other, a.Position);
    }

    // ------------------------------------------------------------------ ordered hunting (drafted / autonomous by order)

    void RunHuntJob(Pawn p, Job j)
    {
        var target = j.TargetAnimal;
        if (target == null || target.Dead) { EndJob(p); p.Anim = PawnAnim.Idle; return; }
        bool ranged = j.Kind == JobKind.Hunt && p.HasRangedWeapon && p.Arrows > 0;
        float range = ranged ? p.Held.Def.Range * 0.85f : FistReach * 0.9f;
        float dist = Vector2.Distance(p.Position, target.Position);
        if (dist > range || (ranged && !LineOfSight(p.Position, target.Position)))
        {
            // re-path toward the moving target every 40 ticks
            if (p.Path.Count == 0 || p.PathIndex >= p.Path.Count || ++j.Timer % 40 == 0)
            {
                int start = Map.CellAt(p.Position), goal = Map.CellAt(target.Position);
                p.Path.Clear(); p.PathIndex = 0;
                if (goal < 0 || Pathfinder.FindPath(start, goal, p.Path, 20000) != PathResult.Found)
                {
                    EndJob(p, $"cannot reach the {target.Label.ToLowerInvariant()}.");
                    return;
                }
            }
            StepAlongPath(p, p.MoveSpeed(j.Forced), p.Path, ref p.PathIndex);
            p.Anim = PawnAnim.Run;
            j.Stage = 0;
            return;
        }
        // in range: aim (warm-up) then shoot
        Vector2 to = target.Position - p.Position;
        p.Facing = MathF.Atan2(to.Y, to.X);
        p.AimDir = Vector2.Normalize(to);
        p.Aiming = true;
        p.Anim = PawnAnim.Aim;
        if (j.Stage == 0) { j.Stage = 1; j.Timer = 0; }
        if (++j.Timer < (ranged ? (int)(45 * p.StatFactor(Stat.AimTime)) : 10) || p.WeaponCooldown > 0) return;
        // lead the target a little
        Vector2 lead = ranged ? target.Position + target.Velocity * (dist / ArrowSpeed) * 0.8f : target.Position;
        Attack(p, lead - p.Position, moving: false, forceMelee: !ranged);
        j.Timer = 0;
    }

    public bool LineOfSight(Vector2 a, Vector2 b)
    {
        float len = Vector2.Distance(a, b);
        int n = Math.Max(1, (int)(len / 0.4f));
        for (int i = 1; i < n; i++)
        {
            int c = Map.CellAt(Vector2.Lerp(a, b, i / (float)n));
            if (c < 0 || Map.BlocksProjectiles(c)) return false;
        }
        return true;
    }
}
