using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Sim;

/// <summary>
/// Arrows in three dimensions. Space is (x east, y south, z up), metres. An arrow leaves the archer's shoulder aimed at
/// a 3D point (a creature's body under the cursor, or the ground); the launch angle is solved for gravity, then the
/// arrow flies with gravity and a drift from the wind. It stops on the first thing it meets: the ground (it sticks in
/// it), water (lost), a wall or rock face, a tree trunk (stuck in the wood), the crown (deflected, falls), or a
/// creature's body — colonists are upright cylinders, deer a body capsule with a neck, a head and four legs — and the
/// impact point decides the body part (an arrow at knee height hits a leg). Arrows stuck in creatures stay in them.
/// </summary>
public sealed partial class GameSim
{
    public const float ArrowSpeedMs = 50f;                 // recurve bow, m/s
    public const float ArrowSpeed = ArrowSpeedMs / 60f;    // m per tick
    public const float Gravity = 9.81f / 3600f;            // m per tick²
    /// <summary>Wind drift: acceleration in m/s² per m/s of wind across the arrow.</summary>
    public const float WindDrift = 0.12f;
    public const float WallHeight = 2.6f;

    /// <summary>Height a colonist's body reaches (the drawn figure is a little taller than life, see PawnModel).</summary>
    public static float BodyHeight(Pawn p) => 1.62f * p.HeightM / 1.75f;
    public float GroundZ(Vector2 p) => Map.StandHeight(p.X, p.Y);
    /// <summary>Where arrows leave the bow: in front of the right shoulder.</summary>
    public Vector3 LaunchPoint(Pawn p)
    {
        var f = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
        var xy = p.Position + f * 0.35f;
        return new Vector3(xy, GroundZ(p.Position) + BodyHeight(p) * 0.82f);
    }
    public Vector3 EyePoint(Pawn p) => new(p.Position, GroundZ(p.Position) + BodyHeight(p) * 0.9f);

    /// <summary>Initial velocity (m per tick) to reach <paramref name="to"/> on the low arc; 45° when out of reach.</summary>
    public static Vector3 LaunchVelocity(Vector3 from, Vector3 to)
    {
        var dxy = new Vector2(to.X - from.X, to.Y - from.Y);
        float d = dxy.Length();
        if (d < 1e-3f) return new Vector3(0, 0, to.Z >= from.Z ? ArrowSpeed : -ArrowSpeed);
        float dz = to.Z - from.Z, v = ArrowSpeed, g = Gravity;
        float disc = v * v * v * v - g * (g * d * d + 2f * dz * v * v);
        float theta = disc < 0 ? MathF.PI / 4f : MathF.Atan((v * v - MathF.Sqrt(disc)) / (g * d));
        var h = dxy / d;
        return new Vector3(h * MathF.Cos(theta) * v, MathF.Sin(theta) * v);
    }

    /// <summary>Wind acceleration on an arrow (m per tick²), from the local weather.</summary>
    Vector3 WindAccel()
    {
        var w = Weather.WindDir * Weather.WindSpeed;
        return new Vector3(w * (WindDrift / 3600f), 0f);
    }

    // ------------------------------------------------------------------ flight

    void TickProjectiles()
    {
        for (int i = Projectiles.Count - 1; i >= 0; i--)
        {
            var pr = Projectiles[i];
            StepProjectile(pr);
            if (pr.Done) Projectiles.RemoveAt(i);
        }
    }

    /// <summary>What an arrow met on one step of its flight.</summary>
    struct ArrowHit
    {
        public float T;            // 0..1 along the step
        public int Kind;           // 0 none, 1 ground, 2 water, 3 wall, 4 rock, 5 trunk, 6 crown, 7 pawn, 8 animal, 9 off map
        public int Cell;
        public Pawn Pawn;
        public Animal Animal;
        public int Part;
    }

    void StepProjectile(Projectile pr)
    {
        var a = pr.Pos;
        pr.Vel += new Vector3(0, 0, -Gravity) + WindAccel();
        var b = a + pr.Vel;
        var hit = Trace(a, b, pr.Shooter, pr.Traveled, real: true);
        if (hit.Kind == 0)
        {
            pr.Pos = b;
            pr.Traveled += (b - a).Length();
            if (++pr.Age > 600) { pr.Done = true; Events.Add(new SimEvent(SimEventKind.ArrowMissed, new Vector2(b.X, b.Y), "lost")); }
            return;
        }
        pr.Done = true;
        var at = Vector3.Lerp(a, b, hit.T);
        var at2 = new Vector2(at.X, at.Y);
        var dir = Vector3.Normalize(pr.Vel);
        switch (hit.Kind)
        {
            case 7:
                AttachArrow(hit.Pawn.Embedded, hit.Pawn.Position, hit.Pawn.Facing, GroundZ(hit.Pawn.Position), at, dir, hit.Part);
                Events.Add(new SimEvent(SimEventKind.ArrowHit, at2, null, hit.Pawn.Id));
                HitPawn(hit.Pawn, pr.Damage, pr.Shooter, bleed: 2.2f, "arrow", hit.Part);
                break;
            case 8:
                AttachArrow(hit.Animal.Embedded, hit.Animal.Position, hit.Animal.Facing, GroundZ(hit.Animal.Position), at, dir, hit.Part);
                Events.Add(new SimEvent(SimEventKind.ArrowHit, at2, null, hit.Animal.Id));
                HitAnimal(hit.Animal, pr.Damage, new Vector2(pr.Start.X, pr.Start.Y), bleed: 2.2f, hit.Part);
                break;
            case 1:
            case 5:
                // stuck in the ground or in the trunk: it stays there, pointing the way it flew
                Events.Add(new SimEvent(SimEventKind.ArrowMissed, at2, hit.Kind == 1 ? "ground" : "tree"));
                if (Rng.Chance(0.85f)) SpawnStuckArrow(at, dir, hit.Kind == 5 ? hit.Cell : -1);
                SpookNear(at2, new Vector2(pr.Start.X, pr.Start.Y));
                break;
            case 3:
                Events.Add(new SimEvent(SimEventKind.ArrowMissed, at2, "wall"));
                if (Rng.Chance(0.7f)) SpawnStuckArrow(at - dir * 0.02f, dir, hit.Cell);
                break;
            case 4:
            case 6:
                // glances off rock, or is deflected by branches: falls to the ground nearby
                Events.Add(new SimEvent(SimEventKind.ArrowMissed, at2, hit.Kind == 4 ? "rock" : "leaves"));
                if (Rng.Chance(hit.Kind == 4 ? 0.5f : 0.9f)) DropArrow(at2 - new Vector2(dir.X, dir.Y) * 0.4f);
                SpookNear(at2, new Vector2(pr.Start.X, pr.Start.Y));
                break;
            case 2:
                Events.Add(new SimEvent(SimEventKind.ArrowMissed, at2, "water"));
                SpookNear(at2, new Vector2(pr.Start.X, pr.Start.Y));
                break;
            default:
                Events.Add(new SimEvent(SimEventKind.ArrowMissed, at2, "lost"));
                break;
        }
    }

    void SpookNear(Vector2 at, Vector2 from)
    {
        foreach (var an in Animals) if (!an.Dead && Vector2.Distance(an.Position, at) < 6f) Spook(an, from);
    }

    /// <summary>
    /// First thing met between a and b (3D). Creatures and obstacles are sampled every 6 cm; the shooter is ignored, and
    /// so are the first 0.6 m of flight (the bow itself). <paramref name="real"/> = false for aim previews (no random crown
    /// deflection, no creatures except as solid targets).
    /// </summary>
    ArrowHit Trace(Vector3 a, Vector3 b, Pawn shooter, float traveled, bool real)
    {
        float len = (b - a).Length();
        int n = Math.Max(1, (int)MathF.Ceiling(len / 0.06f));
        // creatures near the segment (cheap bounding test first)
        var nearPawns = new List<Pawn>();
        foreach (var o in Pawns)
            if (!o.Dead && o != shooter && SegDist2D(a, b, o.Position) < Pawn.Radius + 0.1f) nearPawns.Add(o);
        var nearAnimals = new List<Animal>();
        foreach (var an in Animals)
            if (!an.Dead && SegDist2D(a, b, an.Position) < 1.3f * an.Size) nearAnimals.Add(an);
        int crownCell = -1;
        for (int s = 1; s <= n; s++)
        {
            float t = s / (float)n;
            var p = Vector3.Lerp(a, b, t);
            float flown = traveled + len * t;
            var p2 = new Vector2(p.X, p.Y);
            int cell = Map.CellAt(p2);
            if (cell < 0) return new ArrowHit { T = t, Kind = 9 };
            if (flown > 0.6f)
            {
                // previews must not draw random numbers (the simulation's RNG stays deterministic)
                foreach (var o in nearPawns)
                {
                    if (!real) { if (PawnPartAtNoRng(o, p)) return new ArrowHit { T = t, Kind = 7, Pawn = o, Part = -1 }; }
                    else if (PawnPartAt(o, p, out int part)) return new ArrowHit { T = t, Kind = 7, Pawn = o, Part = part };
                }
                foreach (var an in nearAnimals)
                {
                    if (!real) { if (DeerInside(an, p)) return new ArrowHit { T = t, Kind = 8, Animal = an, Part = -1 }; }
                    else if (DeerPartAt(an, p, out int part)) return new ArrowHit { T = t, Kind = 8, Animal = an, Part = part };
                }
            }
            float ground = Map.GroundHeight(p.X, p.Y);
            if (Map.TerrainAt(cell).Water && p.Z < LocalMap.WaterLevel) return new ArrowHit { T = t, Kind = 2, Cell = cell };
            if (p.Z < ground) return new ArrowHit { T = t, Kind = 1, Cell = cell };
            if (Map.BlocksProjectiles(cell))
            {
                bool rock = Map.Buildings[cell] == Building.Granite;
                float top = ground + (rock ? MathF.Max(1.2f, Map.RockHeight[cell]) : WallHeight);
                if (p.Z < top) return new ArrowHit { T = t, Kind = rock ? 4 : 3, Cell = cell };
            }
            // trees: the trunk (solid wood) and the crown (leaves and twigs deflect some arrows)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    Map.XY(cell, out int cx, out int cy);
                    int x = cx + dx, y = cy + dy;
                    if (!Map.InBounds(x, y)) continue;
                    int c = Map.Index(x, y);
                    if (Map.Plants[c] != Plant.Oak) continue;
                    float scale = TreeScale(c);
                    var center = Map.CellCenter(c);
                    float g0 = Map.GroundHeight(center.X, center.Y);
                    float d = Vector2.Distance(p2, center);
                    if (d < TrunkRadius(c) && p.Z < g0 + 2.3f * scale) return new ArrowHit { T = t, Kind = 5, Cell = c };
                    if (real && flown > 1.5f && c != crownCell)
                    {
                        var cc = new Vector3(center, g0 + 3.3f * scale);
                        if ((p - cc).Length() < 1.6f * scale)
                        {
                            crownCell = c; // one chance per crown crossed
                            if (Rng.Chance(0.3f)) return new ArrowHit { T = t, Kind = 6, Cell = c };
                        }
                    }
                }
        }
        return default;
    }

    /// <summary>Grown size of an oak (the drawn model is scaled the same way).</summary>
    float TreeScale(int cell) => 0.32f + Map.PlantGrowth[cell] / 255f * 0.78f;

    static float SegDist2D(Vector3 a, Vector3 b, Vector2 c)
    {
        var a2 = new Vector2(a.X, a.Y); var ab = new Vector2(b.X, b.Y) - a2;
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(c - a2, ab) / l2, 0f, 1f);
        return Vector2.Distance(a2 + ab * t, c);
    }

    // ------------------------------------------------------------------ bodies

    /// <summary>
    /// Is the point inside a colonist (an upright cylinder of radius Pawn.Radius up to the body height)? If so, which
    /// part: by height (feet, legs, waist and hands, torso and arms, neck, head, eyes) and by side.
    /// </summary>
    public bool PawnPartAt(Pawn o, Vector3 p, out int part)
    {
        part = -1;
        var d = new Vector2(p.X, p.Y) - o.Position;
        if (d.Length() > Pawn.Radius) return false;
        float ground = GroundZ(o.Position), h = BodyHeight(o);
        float zf = (p.Z - ground) / h;
        if (zf < 0f || zf > 1f) return false;
        var fwd = new Vector2(MathF.Cos(o.Facing), MathF.Sin(o.Facing));
        var right = new Vector2(-fwd.Y, fwd.X);
        float side = Vector2.Dot(d, right) / Pawn.Radius;   // −1 left … +1 right
        float front = Vector2.Dot(d, fwd);
        bool r = side > 0;
        BodyRegion region =
            zf > 0.9f ? (front > 0.05f && zf < 0.95f ? BodyRegion.Eyes : BodyRegion.Head)
            : zf > 0.84f ? BodyRegion.Head
            : zf > 0.79f ? BodyRegion.Neck
            : zf > 0.52f ? (MathF.Abs(side) > 0.62f ? (zf > 0.68f ? (r ? BodyRegion.ShoulderR : BodyRegion.ShoulderL) : (r ? BodyRegion.ArmR : BodyRegion.ArmL)) : BodyRegion.Torso)
            : zf > 0.42f ? (MathF.Abs(side) > 0.7f ? (r ? BodyRegion.HandR : BodyRegion.HandL) : BodyRegion.Waist)
            : zf > 0.06f ? (r ? BodyRegion.LegR : BodyRegion.LegL)
            : (r ? BodyRegion.FootR : BodyRegion.FootL);
        part = o.Health.Body.PickInRegion(region, ref Rng);
        return true;
    }

    /// <summary>
    /// Is the point inside a deer? The body is a capsule along its facing, the neck a slanted capsule, the head a sphere
    /// and the legs boxes under the body; returns the part hit.
    /// </summary>
    public bool DeerPartAt(Animal an, Vector3 p, out int part)
    {
        part = -1;
        float s = an.Size;
        var d = new Vector2(p.X, p.Y) - an.Position;
        if (d.Length() > 1.3f * s) return false;
        var fwd = new Vector2(MathF.Cos(an.Facing), MathF.Sin(an.Facing));
        var right = new Vector2(-fwd.Y, fwd.X);
        float u = Vector2.Dot(d, fwd), v = Vector2.Dot(d, right);
        float z = p.Z - GroundZ(an.Position);
        var body = an.Health.Body;
        // head
        var head = new Vector3(0.88f * s, 0, 1.5f * s);
        if ((new Vector3(u, v, z) - head).Length() < 0.15f * s) { part = body.PickPart(ref Rng, "Head", "Brain"); return true; }
        // neck: from the front of the body up to the head
        if (DistToSegment(new Vector3(u, v, z), new Vector3(0.5f * s, 0, 1.1f * s), new Vector3(0.82f * s, 0, 1.42f * s)) < 0.1f * s)
        { part = body.Find("Neck"); return true; }
        // body capsule
        float L = 0.55f * s, R = 0.26f * s, zc = 1.0f * s;
        float cu = Math.Clamp(u, -L, L);
        if (new Vector3(u - cu, v, z - zc).Length() < R) { part = body.PickPart(ref Rng, "Body", "Heart", "Left lung", "Right lung", "Liver"); return true; }
        // legs: front and hind pairs under the body
        if (z >= 0 && z < zc - R * 0.6f && MathF.Abs(v) < 0.17f * s && MathF.Abs(u) > 0.25f * s && MathF.Abs(u) < L + 0.05f * s)
        {
            string name = (u > 0 ? "Front " : "Hind ") + (v > 0 ? "right leg" : "left leg");
            part = body.Find(name);
            return true;
        }
        return false;
    }

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return (a + ab * t - p).Length();
    }

    /// <summary>Remembers an arrow stuck in a creature, in the creature's own frame (forward, right, up from the ground).</summary>
    static void AttachArrow(List<StuckArrow> list, Vector2 pos, float facing, float ground, Vector3 at, Vector3 dir, int part)
    {
        var fwd = new Vector2(MathF.Cos(facing), MathF.Sin(facing));
        var right = new Vector2(-fwd.Y, fwd.X);
        var d = new Vector2(at.X, at.Y) - pos;
        var d2 = new Vector2(dir.X, dir.Y);
        list.Add(new StuckArrow
        {
            Local = new Vector3(Vector2.Dot(d, fwd), Vector2.Dot(d, right), at.Z - ground),
            Dir = new Vector3(Vector2.Dot(d2, fwd), Vector2.Dot(d2, right), dir.Z),
            Part = part,
        });
    }

    /// <summary>World position and direction of an arrow stuck in a creature (for drawing).</summary>
    public static (Vector3 pos, Vector3 dir) StuckArrowWorld(StuckArrow s, Vector2 pos, float facing, float ground)
    {
        var fwd = new Vector2(MathF.Cos(facing), MathF.Sin(facing));
        var right = new Vector2(-fwd.Y, fwd.X);
        var xy = pos + fwd * s.Local.X + right * s.Local.Y;
        var dxy = fwd * s.Dir.X + right * s.Dir.Y;
        return (new Vector3(xy, ground + s.Local.Z), Vector3.Normalize(new Vector3(dxy, s.Dir.Z)));
    }

    void SpawnStuckArrow(Vector3 at, Vector3 dir, int intoCell)
    {
        var xy = new Vector2(at.X, at.Y);
        int cell = Map.CellAt(xy);
        if (cell < 0 || Map.TerrainAt(cell).Water) return;
        var it = new Item(NewId(), Defs.Arrow, 1);
        PlaceOnGround(it, xy, exact: true);
        it.StuckZ = at.Z;
        it.StuckDir = dir;
        Log.Debug($"arrow stuck at {at} ({(intoCell >= 0 ? "in " + Map.Buildings[intoCell] + "/" + Map.Plants[intoCell] : "in the ground")})");
    }

    // ------------------------------------------------------------------ aiming helpers for the game layer

    /// <summary>
    /// The flight an arrow would take (no spread, no random deflection): points every tick until it hits something.
    /// Returns what it would hit (0 nothing in 200 m, else the ArrowHit kind) and the impact point.
    /// </summary>
    public int PredictArrow(Pawn shooter, Vector3 target, List<Vector3> points, out Vector3 impact)
    {
        points.Clear();
        var p = LaunchPoint(shooter);
        var v = LaunchVelocity(p, target);
        float flown = 0;
        points.Add(p);
        for (int i = 0; i < 400; i++)
        {
            v += new Vector3(0, 0, -Gravity) + WindAccel();
            var q = p + v;
            var hit = Trace(p, q, shooter, flown, real: false);
            if (hit.Kind != 0)
            {
                impact = Vector3.Lerp(p, q, hit.T);
                points.Add(impact);
                return hit.Kind;
            }
            flown += v.Length();
            p = q;
            points.Add(p);
        }
        impact = p;
        return 0;
    }

    /// <summary>
    /// First creature (other than <paramref name="ignore"/>) hit by a ray (sim space), within maxDist metres: used to aim
    /// at the body under the cursor. Marches in 5 cm steps near candidates only.
    /// </summary>
    public bool RaycastCreature(Vector3 origin, Vector3 dir, float maxDist, Pawn ignore, out Vector3 hit)
    {
        dir = Vector3.Normalize(dir);
        float best = float.MaxValue;
        Vector3 bestHit = default;
        void Test(Vector2 c, float radius, Func<Vector3, bool> inside)
        {
            // closest approach of the ray to the creature's vertical axis
            var o2 = new Vector2(origin.X, origin.Y); var d2 = new Vector2(dir.X, dir.Y);
            float l2 = d2.LengthSquared();
            float tc = l2 < 1e-9f ? 0f : Vector2.Dot(c - o2, d2) / l2;
            if (tc < 0 || tc > maxDist) return;
            if (Vector2.Distance(o2 + d2 * tc, c) > radius) return;
            float span = radius / MathF.Max(0.05f, MathF.Sqrt(l2)) + 2.5f;
            for (float t = MathF.Max(0f, tc - span); t < MathF.Min(maxDist, tc + span); t += 0.05f)
            {
                var q = origin + dir * t;
                if (inside(q)) { if (t < best) { best = t; bestHit = q; } return; }
            }
        }
        foreach (var o in Pawns) if (!o.Dead && o != ignore) Test(o.Position, Pawn.Radius, q => PawnPartAtNoRng(o, q));
        foreach (var an in Animals) if (!an.Dead) Test(an.Position, 1.3f * an.Size, q => DeerInside(an, q));
        hit = bestHit;
        return best < float.MaxValue;
    }

    bool PawnPartAtNoRng(Pawn o, Vector3 p)
    {
        var d = new Vector2(p.X, p.Y) - o.Position;
        if (d.Length() > Pawn.Radius) return false;
        float zf = (p.Z - GroundZ(o.Position)) / BodyHeight(o);
        return zf >= 0f && zf <= 1f;
    }

    bool DeerInside(Animal an, Vector3 p)
    {
        // same volumes as DeerPartAt, without choosing a part (no random draw)
        float s = an.Size;
        var d = new Vector2(p.X, p.Y) - an.Position;
        var fwd = new Vector2(MathF.Cos(an.Facing), MathF.Sin(an.Facing));
        var right = new Vector2(-fwd.Y, fwd.X);
        float u = Vector2.Dot(d, fwd), v = Vector2.Dot(d, right), z = p.Z - GroundZ(an.Position);
        var q = new Vector3(u, v, z);
        if ((q - new Vector3(0.88f * s, 0, 1.5f * s)).Length() < 0.15f * s) return true;
        if (DistToSegment(q, new Vector3(0.5f * s, 0, 1.1f * s), new Vector3(0.82f * s, 0, 1.42f * s)) < 0.1f * s) return true;
        float L = 0.55f * s, R = 0.26f * s, zc = 1.0f * s;
        if (new Vector3(u - Math.Clamp(u, -L, L), v, z - zc).Length() < R) return true;
        return z >= 0 && z < zc - R * 0.6f && MathF.Abs(v) < 0.17f * s && MathF.Abs(u) > 0.25f * s && MathF.Abs(u) < L + 0.05f * s;
    }

    /// <summary>
    /// Line of sight between two places for creatures standing there (eyes 1.5 m up, target 1 m up): walls, rock and
    /// the terrain itself block it, so a colonist down in a hollow cannot see (or shoot) across the rim.
    /// </summary>
    public bool LineOfSight(Vector2 a, Vector2 b)
    {
        float len = Vector2.Distance(a, b);
        int n = Math.Max(1, (int)(len / 0.4f));
        float za = GroundZ(a) + 1.5f, zb = GroundZ(b) + 1.0f;
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n;
            var p = Vector2.Lerp(a, b, t);
            int c = Map.CellAt(p);
            if (c < 0 || Map.BlocksProjectiles(c)) return false;
            if (Map.GroundHeight(p.X, p.Y) > za + (zb - za) * t) return false;
        }
        return true;
    }
}
