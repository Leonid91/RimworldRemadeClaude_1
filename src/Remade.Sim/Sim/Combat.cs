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

    /// <summary>Range of the pawn's current attack: the bow's range with arrows, else melee reach.</summary>
    public static float AttackRange(Pawn p) => p.HasRangedWeapon && p.Arrows > 0 ? p.Held.Def.Range : FistReach;

    /// <summary>
    /// Angular spread (radians, 1 sigma) of a shot, left/right (up/down is 0.7 of it): a novice (skill 0) 3.2°, a
    /// master (20) 0.6°; moving adds 2.5°. At 10 m a novice puts most arrows within half a metre of the aim point.
    /// </summary>
    public static float ShotSpread(Pawn p, bool moving)
    {
        float deg = 3.2f - p.Skills[(int)SkillId.Shooting] * 0.13f;
        deg *= p.StatFactor(Stat.ShotSpread);
        if (moving) deg += 2.5f;
        return MathF.Max(0.5f, deg) * MathF.PI / 180f;
    }

    /// <summary>Shoots or swings toward a direction on the ground (aims at chest height 10 m away).</summary>
    public void Attack(Pawn p, Vector2 dir, bool moving, bool forceMelee = false)
    {
        if (dir.LengthSquared() < 1e-6f) throw new ArgumentException("attack direction is zero");
        var to = p.Position + Vector2.Normalize(dir) * 10f;
        Attack(p, new Vector3(to, GroundZ(to) + 1.0f), moving, forceMelee);
    }

    /// <summary>
    /// Shoots (bow with arrows) at a 3D point (x, y on the map, z height in metres), or swings (otherwise) toward it.
    /// The arrow is launched on the arc that reaches the point, with a random error from skill, traits and moving.
    /// </summary>
    public void Attack(Pawn p, Vector3 target, bool moving, bool forceMelee = false)
    {
        var dir = new Vector2(target.X, target.Y) - p.Position;
        if (dir.LengthSquared() < 1e-6f) throw new ArgumentException("attack target is at the attacker's position");
        dir = Vector2.Normalize(dir);
        p.Facing = MathF.Atan2(dir.Y, dir.X);
        if (!forceMelee && p.HasRangedWeapon && p.Arrows > 0)
        {
            int used = p.ConsumeFromInventory(Defs.Arrow, 1);
            Invariant.Check(used == 1, $"{p.Name}: arrow count {p.Arrows} but none consumed");
            float spread = ShotSpread(p, moving);
            var from = LaunchPoint(p);
            var v = LaunchVelocity(from, target);
            // aiming error: a random turn left/right and up/down
            float yaw = Rng.Gaussian() * spread, pitch = Rng.Gaussian() * spread * 0.7f;
            float hx = v.X, hy = v.Y, hl = MathF.Sqrt(hx * hx + hy * hy);
            float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
            float nx = hx * cy - hy * sy, ny = hx * sy + hy * cy;
            float elev = MathF.Atan2(v.Z, hl) + pitch;
            var dirH = new Vector2(nx, ny) / MathF.Max(hl, 1e-6f);
            var vel = new Vector3(dirH * MathF.Cos(elev), MathF.Sin(elev)) * ArrowSpeed;
            Projectiles.Add(new Projectile
            {
                Start = from, Pos = from, Vel = vel, Damage = p.Held.Def.Damage * Rng.Range(0.85f, 1.25f), Shooter = p,
            });
            p.WeaponCooldown = (int)(p.Held.Def.Cooldown * p.StatFactor(Stat.AimTime));
            p.Anim = PawnAnim.Shoot; p.AnimTime = 0;
            Events.Add(new SimEvent(SimEventKind.ArrowFired, p.Position, null, p.Id));
            Log.Debug($"{p.Name} shoots at {target}: elevation {MathF.Atan2(v.Z, hl) * 57.3f:F1}°, error {yaw * 57.3f:F1}°/{pitch * 57.3f:F1}°, arrows left {p.Arrows}");
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

    /// <summary>
    /// Wounds a colonist (arrow or blow) in a given body part, or a random one (part &lt; 0). Damage is scaled by their
    /// Damage taken stat (Tough, Delicate).
    /// </summary>
    void HitPawn(Pawn victim, float damage, Pawn attacker, float bleed, string what, int part = -1)
    {
        damage *= victim.StatFactor(Stat.DamageTaken);
        if (part < 0) part = victim.Health.Body.PickHitPart(ref Rng);
        string partName = victim.Health.Body[part].Name.ToLowerInvariant();
        bool died = victim.Health.Damage(part, damage, Tick, bleed);
        string by = attacker != null ? $"{attacker.Name}'s {what}" : $"A {what}";
        Message($"{by} hits {victim.Name} in the {partName}" + (died ? $" — {victim.Name} is killed." : "."), victim.Position);
        Log.Info($"{victim} hit in the {partName} for {damage:F1} by {attacker?.ToString() ?? "-"} ({what}); health {victim.Health.Summary:P0}{(died ? ", killed: " + victim.Health.DeathCause : "")}");
        if (died) Die(victim);
    }

    void DropArrow(Vector2 at)
    {
        int cell = Map.CellAt(at);
        if (cell < 0 || Map.TerrainAt(cell).Water) return;
        // merge with an arrow stack already lying there
        foreach (var it in ItemsAt(cell))
            if (it.Def == Defs.Arrow && !it.Stuck && it.Count < Defs.Arrow.StackLimit) { it.Count++; return; }
        SpawnItem(Defs.Arrow, 1, at);
    }

    void HitAnimal(Animal a, float damage, Vector2 from, float bleed, int part = -1)
    {
        if (part < 0) part = a.Health.Body.PickHitPart(ref Rng);
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
        // the arrows stuck in it can be pulled out; some broke
        int arrows = 0;
        for (int i = 0; i < a.Embedded.Count; i++) if (Rng.Chance(0.6f)) arrows++;
        a.Embedded.Clear();
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
        // lead the target a little and aim at the middle of its body
        Vector2 lead = ranged ? target.Position + target.Velocity * (dist / ArrowSpeed) * 0.8f : target.Position;
        Attack(p, new Vector3(lead, GroundZ(lead) + 1.0f * target.Size), moving: false, forceMelee: !ranged);
        j.Timer = 0;
    }
}
