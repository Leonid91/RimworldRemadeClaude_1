using System;
using System.Collections.Generic;
using System.Numerics;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Map;
using Remade.Pawns;
using Remade.Things;

namespace Remade.Sim;

public sealed partial class GameSim
{
    const int NeedsInterval = 30;
    public const float Reach = 1.6f;

    void TickPawn(Pawn p)
    {
        if (p.Dead) { p.Anim = PawnAnim.Dead; return; }
        p.AnimTime++;
        if (p.WeaponCooldown > 0) p.WeaponCooldown--;

        if (Tick % NeedsInterval == p.Id % NeedsInterval)
        {
            float hours = NeedsInterval / (float)GameTime.TicksPerHour;
            bool asleep = p.Job?.Kind == JobKind.Sleep && p.Job.Stage == 1;
            float exertion = p.Anim == PawnAnim.Run ? 1.6f : p.Anim == PawnAnim.Work ? 1.3f : 1f;
            p.Needs.Tick(hours, asleep, Weather.Temperature, exertion, p.StatFactor(Stat.HungerRate), p.StatFactor(Stat.ThirstRate), p.StatFactor(Stat.FatigueRate));
            if (p.Health.TickBleeding(hours)) Die(p);
        }

        Vector2 before = p.Position;
        if (p.Mode == ControlMode.Direct && (p.Job == null || !p.Job.Forced || Input.Move != Vector2.Zero)) DirectControl(p);
        else
        {
            p.Aiming = false;
            if (p.Job == null && p.Mode == ControlMode.Autonomous) Think(p);
            if (p.Job == null) { p.Anim = PawnAnim.Idle; p.LastJobLabel = p.Mode == ControlMode.Drafted ? "Drafted, standing by" : "Idle"; }
            else RunJob(p);
        }
        p.Velocity = p.Position - before;
    }

    // ------------------------------------------------------------------ direct control

    void DirectControl(Pawn p)
    {
        var inp = Input;
        if (inp.Move != Vector2.Zero && p.Job != null) ClearJob(p); // moving cancels an interaction
        if (p.Job != null) { RunJob(p); return; }

        p.Aiming = inp.Aim;
        Vector2 aimVec = inp.AimPoint - p.Position;
        if (aimVec.LengthSquared() > 1e-4f) p.AimDir = Vector2.Normalize(aimVec);

        float speed = p.MoveSpeed(inp.Sprint && !inp.Aim);
        if (inp.Aim) speed *= 0.55f; // careful steps while aiming
        Vector2 move = inp.Move.LengthSquared() > 1f ? Vector2.Normalize(inp.Move) : inp.Move;
        if (move != Vector2.Zero)
        {
            int cell = Map.CellAt(p.Position);
            if (cell >= 0) speed *= 10f / Math.Max(10, (int)Map.TerrainAt(cell).Cost);
            MoveWithCollision(p, move * speed);
            if (!inp.Aim) p.Facing = MathF.Atan2(move.Y, move.X);
            p.Anim = inp.Sprint && !inp.Aim ? PawnAnim.Run : PawnAnim.Walk;
            p.LastJobLabel = "Under your control";
        }
        else p.Anim = inp.Aim ? PawnAnim.Aim : PawnAnim.Idle;

        if (inp.Aim)
        {
            p.Facing = MathF.Atan2(p.AimDir.Y, p.AimDir.X);
            if (move == Vector2.Zero) p.Anim = PawnAnim.Aim;
            if (inp.Fire && p.WeaponCooldown == 0 && Vector2.DistanceSquared(inp.AimPoint, p.Position) > 1e-4f)
            {
                // aim at the 3D point under the cursor (a body, or the ground); without a height, at chest height
                float z = float.IsNaN(inp.AimZ) ? GroundZ(inp.AimPoint) + 1.0f : inp.AimZ;
                Attack(p, new Vector3(inp.AimPoint, z), moving: move != Vector2.Zero);
            }
        }
        else if (inp.Fire)
        {
            // left click without aiming does nothing in direct control (aim with the right mouse button first)
        }
    }

    /// <summary>
    /// Moves a circle through the grid, sliding along walls, rock, closed doors, deep water and tree trunks.
    /// Doorway assist: if a move is mostly blocked by a corner, try sidestepping a little so the pawn slips through
    /// openings (doors, gaps between rocks) instead of sticking on the jamb.
    /// </summary>
    void MoveWithCollision(Pawn p, Vector2 delta)
    {
        Vector2 start = p.Position;
        MoveWithCollisionRaw(p, delta);
        float want = delta.Length();
        if (want < 1e-5f || Vector2.Dot(p.Position - start, delta / want) > want * 0.5f) return;
        // mostly blocked: look for the nearest sideways offset from which the way ahead is clear
        Vector2 dir = delta / want, side = new(-dir.Y, dir.X);
        for (float k = 0.05f; k <= 0.6f; k += 0.05f)
            foreach (float sgn in new[] { 1f, -1f })
            {
                Vector2 q = start + side * (k * sgn);
                if (!CircleFree(q, Pawn.Radius) || !CircleFree(q + dir * 0.4f, Pawn.Radius)) continue;
                p.Position = start;
                MoveWithCollisionRaw(p, side * (sgn * MathF.Min(want, k)) + dir * want * 0.3f);
                return;
            }
    }

    /// <summary>True if a circle overlaps no blocked cell (walls, rock, closed doors, deep water).</summary>
    bool CircleFree(Vector2 c, float r)
    {
        int x0 = (int)MathF.Floor(c.X - r), x1 = (int)MathF.Floor(c.X + r);
        int y0 = (int)MathF.Floor(c.Y - r), y1 = (int)MathF.Floor(c.Y + r);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (!Map.InBounds(x, y)) return false;
                if (!Map.Blocked(Map.Index(x, y))) continue;
                float cx = Math.Clamp(c.X, x, x + 1), cy = Math.Clamp(c.Y, y, y + 1);
                if ((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy) < r * r) return false;
            }
        return true;
    }

    void MoveWithCollisionRaw(Pawn p, Vector2 delta)
    {
        Vector2 pos = p.Position;
        pos.X += delta.X;
        pos = ResolveCells(pos, Pawn.Radius, axisX: true, delta.X);
        pos.Y += delta.Y;
        pos = ResolveCells(pos, Pawn.Radius, axisX: false, delta.Y);
        pos = ResolveTrunks(pos, Pawn.Radius);
        pos.X = Math.Clamp(pos.X, Pawn.Radius, Map.Width - Pawn.Radius);
        pos.Y = Math.Clamp(pos.Y, Pawn.Radius, Map.Height - Pawn.Radius);
        p.Position = pos;
    }

    Vector2 ResolveCells(Vector2 pos, float r, bool axisX, float d)
    {
        int x0 = (int)MathF.Floor(pos.X - r), x1 = (int)MathF.Floor(pos.X + r);
        int y0 = (int)MathF.Floor(pos.Y - r), y1 = (int)MathF.Floor(pos.Y + r);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (!Map.InBounds(x, y)) continue;
                if (!Map.Blocked(Map.Index(x, y))) continue;
                // closest point of the cell box to the circle centre
                float cx = Math.Clamp(pos.X, x, x + 1), cy = Math.Clamp(pos.Y, y, y + 1);
                float dx = pos.X - cx, dy = pos.Y - cy;
                if (dx * dx + dy * dy >= r * r) continue;
                if (axisX) pos.X = d > 0 ? x - r - 0.0005f : d < 0 ? x + 1 + r + 0.0005f : pos.X;
                else pos.Y = d > 0 ? y - r - 0.0005f : d < 0 ? y + 1 + r + 0.0005f : pos.Y;
            }
        return pos;
    }

    Vector2 ResolveTrunks(Vector2 pos, float r)
    {
        int cx = (int)pos.X, cy = (int)pos.Y;
        for (int y = cy - 1; y <= cy + 1; y++)
            for (int x = cx - 1; x <= cx + 1; x++)
            {
                if (!Map.InBounds(x, y)) continue;
                int c = Map.Index(x, y);
                if (Map.Plants[c] != Plant.Oak) continue;
                float tr = TrunkRadius(c);
                Vector2 tc = LocalMap.CellCenter(x, y);
                Vector2 d = pos - tc;
                float dist = d.Length(), min = r + tr;
                if (dist < min && dist > 1e-5f) pos = tc + d / dist * min;
            }
        return pos;
    }

    public float TrunkRadius(int cell) => 0.08f + Map.PlantGrowth[cell] / 255f * 0.22f;

    // ------------------------------------------------------------------ AI

    void Think(Pawn p)
    {
        var n = p.Needs;
        float hour = GameTime.HourOfDay(Tick);
        bool night = hour >= 22f || hour < 6f;
                if (n.Rest < Needs.TiredThreshold || (night && n.Rest < 0.85f))
        {
            StartJob(p, new Job { Kind = JobKind.Sleep, Label = "Sleeping" });
            return;
        }
        if (n.Thirst < Needs.ThirstyThreshold && TryDrinkJob(p, forced: false)) return;
        if (n.Food < Needs.HungryThreshold)
        {
            var food = BestFoodInInventory(p);
            if (food != null) { StartJob(p, new Job { Kind = JobKind.Eat, TargetItem = food, Label = "Eating " + food.Def.Label, WorkTicks = 240 }); return; }
            int bush = FindNearest(p.Position, 60, c => Map.Plants[c] == Plant.BerryBush && Map.Berries[c] > 0);
            if (bush >= 0) { StartJob(p, new Job { Kind = JobKind.Gather, TargetCell = bush, Label = "Gathering berries", WorkTicks = 160 }); return; }
        }
        // wander near where they are
        for (int tries = 0; tries < 6; tries++)
        {
            var target = p.Position + new Vector2(Rng.Range(-9f, 9f), Rng.Range(-9f, 9f));
            int cell = Map.CellAt(target);
            if (cell < 0 || Paths.Cost[cell] == 0 || Map.TerrainAt(cell).Water) continue;
            StartJob(p, new Job { Kind = JobKind.Wander, TargetCell = cell, TargetPos = Map.CellCenter(cell), Label = "Wandering", WorkTicks = Rng.Range(120, 420) });
            return;
        }
        StartJob(p, new Job { Kind = JobKind.Wait, Label = "Idle", WorkTicks = 120 });
    }

    internal bool TryDrinkJob(Pawn p, bool forced)
    {
        int water = FindNearest(p.Position, 90, c => Map.IsFreshWater(c));
        if (water < 0)
        {
            if (forced) Message($"{p.Name} cannot find fresh water nearby.", p.Position);
            return false;
        }
        StartJob(p, new Job { Kind = JobKind.Drink, TargetCell = water, Label = "Drinking", WorkTicks = 90, Forced = forced });
        return true;
    }

    Item BestFoodInInventory(Pawn p)
    {
        foreach (var e in p.Inventory.Entries)
            if (e.Item.Def.Kind == ThingKind.Food) return e.Item;
        return p.Held?.Def.Kind == ThingKind.Food ? p.Held : null;
    }

    /// <summary>
    /// Nearest cell matching a predicate whose interaction spot is reachable, by expanding square rings
    /// (cheap, no flood fill). Returns -1 if none within the radius.
    /// </summary>
    public int FindNearest(Vector2 from, int radius, Func<int, bool> match)
    {
        int cx = (int)from.X, cy = (int)from.Y;
        int start = Map.CellAt(from);
        int best = -1; float bestD = float.MaxValue;
        for (int r = 0; r <= radius; r++)
        {
            if (best >= 0 && r > bestD + 1.5f) break;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = cx + dx, y = cy + dy;
                    if (!Map.InBounds(x, y)) continue;
                    int c = Map.Index(x, y);
                    if (!match(c)) continue;
                    int spot = InteractionSpot(c, from);
                    if (spot < 0 || (start >= 0 && !Paths.Reachable(start, spot))) continue;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d < bestD) { bestD = d; best = c; }
                }
        }
        return best;
    }

    /// <summary>A walkable cell from which a pawn can interact with the target cell (the cell itself or a neighbour).</summary>
    public int InteractionSpot(int target, Vector2 from)
    {
        if (Paths.Cost[target] != 0 && !Map.TerrainAt(target).Water && Map.Plants[target] != Plant.BerryBush && Map.Buildings[target] != Building.Door)
            return target;
        Map.XY(target, out int tx, out int ty);
        int best = -1; float bestD = float.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if ((dx | dy) == 0) continue;
                int x = tx + dx, y = ty + dy;
                if (!Map.InBounds(x, y)) continue;
                int c = Map.Index(x, y);
                if (Paths.Cost[c] == 0 || Map.Buildings[c] == Building.Door) continue;
                // shallow water is fine to stand in for drinking, but prefer dry ground
                float d = Vector2.DistanceSquared(LocalMap.CellCenter(x, y), from) + (Map.TerrainAt(c).Water ? 6f : 0f);
                if (d < bestD) { bestD = d; best = c; }
            }
        return best;
    }

    // ------------------------------------------------------------------ jobs

    public void StartJob(Pawn p, Job job)
    {
        Invariant.Check(job.Kind != JobKind.Goto || (job.TargetCell >= 0 && job.TargetCell < Map.CellCount), $"{p.Name}: Goto job without a valid target cell ({job.TargetCell})");
        ClearJob(p);
        p.Job = job;
        p.LastJobLabel = job.Label ?? job.Kind.ToString();
        if (job.Forced || p.Mode != ControlMode.Autonomous) Log.Debug($"{p.Name}: job {job.Kind} {job.Label} target cell {job.TargetCell} item {job.TargetItem}");
    }

    public void ClearJob(Pawn p)
    {
        p.Job = null;
        p.Path.Clear();
        p.PathIndex = 0;
    }

    void EndJob(Pawn p, string reason = null)
    {
        if (reason != null && p.Job != null && p.Job.Forced) Message($"{p.Name}: {reason}", p.Position);
        ClearJob(p);
    }

    void RunJob(Pawn p)
    {
        var j = p.Job;
        switch (j.Kind)
        {
            case JobKind.Wait:
                p.Anim = PawnAnim.Idle;
                if (++j.Timer >= j.WorkTicks) EndJob(p);
                break;

            case JobKind.Goto:
                if (FollowPathTo(p, j.TargetCell, out bool failed)) { EndJob(p); p.Anim = PawnAnim.Idle; }
                else if (failed) EndJob(p, "cannot reach that place.");
                break;

            case JobKind.Wander:
                if (j.Stage == 0)
                {
                    if (FollowPathTo(p, j.TargetCell, out bool f)) j.Stage = 1;
                    else if (f) EndJob(p);
                }
                else { p.Anim = PawnAnim.Idle; if (++j.Timer >= j.WorkTicks) EndJob(p); }
                break;

            case JobKind.Sleep:
                if (j.Stage == 0) { j.Stage = 1; p.Anim = PawnAnim.Sleep; }
                p.Anim = PawnAnim.Sleep;
                p.LastJobLabel = "Sleeping";
                float hour = GameTime.HourOfDay(Tick);
                bool day = hour >= 6.5f && hour < 21.5f;
                if (p.Needs.Rest >= 0.999f || (day && p.Needs.Rest > 0.75f && !j.Forced)) EndJob(p);
                break;

            case JobKind.PickUp:
            case JobKind.Gather:
            case JobKind.Drink:
            case JobKind.ToggleDoor:
                RunInteractionJob(p, j);
                break;

            case JobKind.Eat:
                p.Anim = PawnAnim.Eat;
                if (j.TargetItem == null || j.TargetItem.Count <= 0) { EndJob(p); break; }
                if (++j.Timer >= j.WorkTicks) { EatFromInventory(p, j.TargetItem); EndJob(p); }
                break;

            case JobKind.Hunt:
            case JobKind.Melee:
                RunHuntJob(p, j);
                break;

            case JobKind.Mine:
                RunMineJob(p, j);
                break;

            default:
                throw new InvalidOperationException($"Unhandled job kind {j.Kind}");
        }
    }

    /// <summary>Walks toward a cell along an A* path. Returns true on arrival; sets failed when no path exists.</summary>
    bool FollowPathTo(Pawn p, int targetCell, out bool failed, float arriveDist = 0.15f)
    {
        failed = false;
        Vector2 target = Map.CellCenter(targetCell);
        if (Vector2.Distance(p.Position, target) <= arriveDist) return true;
        if (p.Path.Count == 0)
        {
            int start = Map.CellAt(p.Position);
            var res = Pathfinder.FindPath(start, targetCell, p.Path);
            if (res != PathResult.Found)
            {
                failed = true;
                Log.Debug($"{p.Name}: no path {start}->{targetCell}: {res}");
                return false;
            }
            p.PathIndex = 0;
        }
        StepAlongPath(p, p.MoveSpeed(false), p.Path, ref p.PathIndex);
        p.Anim = PawnAnim.Walk;
        return Vector2.Distance(p.Position, target) <= arriveDist && p.PathIndex >= p.Path.Count;
    }

    void StepAlongPath(Pawn p, float speed, List<Vector2> path, ref int index)
    {
        int cell = Map.CellAt(p.Position);
        if (cell >= 0) speed *= 10f / Math.Max(10, (int)Paths.Cost[cell]);
        float budget = speed;
        while (budget > 0 && index < path.Count)
        {
            Vector2 wp = path[index];
            // open doors ahead (AI and drafted pawns open doors automatically)
            int next = Map.CellAt(p.Position + Vector2.Normalize(wp - p.Position + new Vector2(1e-5f, 0)) * 0.6f);
            if (next >= 0 && Map.Buildings[next] == Building.Door) AutoOpenDoor(next);
            Vector2 d = wp - p.Position;
            float len = d.Length();
            if (len <= budget) { p.Position = wp; budget -= len; index++; }
            else
            {
                p.Position += d / len * budget;
                budget = 0;
            }
            if (len > 1e-4f) p.Facing = MathF.Atan2(d.Y, d.X);
        }
    }

    void RunInteractionJob(Pawn p, Job j)
    {
        // validate target
        switch (j.Kind)
        {
            case JobKind.PickUp when j.TargetItem == null || !j.TargetItem.Spawned:
                EndJob(p, "the item is gone."); return;
            case JobKind.Gather when Map.Plants[j.TargetCell] != Plant.BerryBush || Map.Berries[j.TargetCell] == 0:
                EndJob(p, "there are no berries left."); return;
            case JobKind.ToggleDoor when Map.Buildings[j.TargetCell] != Building.Door:
                EndJob(p, "the door is gone."); return;
        }

        Vector2 targetPos = j.Kind == JobKind.PickUp ? j.TargetItem.Position : Map.CellCenter(j.TargetCell);
        if (j.Stage == 0)
        {
            if (Vector2.Distance(p.Position, targetPos) <= Reach) { j.Stage = 1; p.Path.Clear(); p.PathIndex = 0; }
            else
            {
                int goal = j.Kind == JobKind.PickUp ? Map.CellAt(targetPos) : InteractionSpot(j.TargetCell, p.Position);
                if (goal < 0 || FollowPathTo(p, goal, out bool failed, Reach * 0.5f)) { if (goal < 0) { EndJob(p, "cannot get there."); return; } j.Stage = 1; p.Path.Clear(); }
                else if (failed) { EndJob(p, "cannot reach it."); return; }
                if (j.Stage == 0 && Vector2.Distance(p.Position, targetPos) <= Reach * 0.9f) { j.Stage = 1; p.Path.Clear(); p.PathIndex = 0; }
                if (j.Stage == 0) return;
            }
        }

        // working
        Vector2 look = targetPos - p.Position;
        if (look.LengthSquared() > 1e-4f) p.Facing = MathF.Atan2(look.Y, look.X);
        p.Anim = j.Kind switch { JobKind.Drink => PawnAnim.Drink, JobKind.PickUp => PawnAnim.PickUp, _ => PawnAnim.Work };
        int work = j.WorkTicks > 0 ? j.WorkTicks : j.Kind switch { JobKind.PickUp => 25, JobKind.ToggleDoor => 12, JobKind.Gather => 160, _ => 90 };
        if (j.Kind == JobKind.Gather) work = (int)(work / p.StatFactor(Stat.GatherSpeed));
        if (++j.Timer < work) return;

        switch (j.Kind)
        {
            case JobKind.PickUp:
            {
                var def = j.TargetItem.Def;
                bool ok = PickUp(p, j.TargetItem);
                EndJob(p);
                if (ok && j.EatAfter)
                {
                    Item food = null;
                    foreach (var e in p.Inventory.Entries) if (e.Item.Def == def) food = e.Item;
                    if (food == null && p.Held?.Def == def) food = p.Held;
                    if (food != null) EatFromInventory(p, food);
                }
                break;
            }
            case JobKind.Gather:
            {
                int n = Map.Berries[j.TargetCell];
                var berries = new Item(NewId(), Defs.Berries, n);
                int taken = 0;
                taken += p.Inventory.Absorb(berries, NewId);
                Map.SetBerries(j.TargetCell, (byte)(n - taken));
                if (berries.Count > 0) PlaceOnGround(berries, Map.CellCenter(InteractionSpot(j.TargetCell, p.Position)));
                Events.Add(new SimEvent(SimEventKind.Gathered, targetPos, $"+{n} berries"));
                Log.Info($"{p.Name} gathered {n} berries (cell {j.TargetCell})");
                EndJob(p);
                if (p.Mode == ControlMode.Autonomous && p.Needs.Food < Needs.HungryThreshold) Think(p);
                break;
            }
            case JobKind.Drink:
                p.Needs.Thirst = MathF.Min(1f, p.Needs.Thirst + 0.25f);
                Events.Add(new SimEvent(SimEventKind.Drank, targetPos));
                if (p.Needs.Thirst >= 0.98f || p.Mode == ControlMode.Direct) { Log.Debug($"{p.Name} drank, thirst {p.Needs.Thirst:P0}"); EndJob(p); }
                else j.Timer = 0;
                break;
            case JobKind.ToggleDoor:
            {
                bool open = !Map.DoorOpen[j.TargetCell];
                if (!open && DoorOccupied(j.TargetCell)) { EndJob(p, "something is in the doorway."); break; }
                Map.SetDoor(j.TargetCell, open);
                _autoDoors.Remove(j.TargetCell);
                Events.Add(new SimEvent(SimEventKind.DoorToggled, targetPos, open ? "open" : "close", j.TargetCell));
                Log.Info($"{p.Name} {(open ? "opened" : "closed")} the door at cell {j.TargetCell}");
                EndJob(p);
                break;
            }
        }
    }

    /// <summary>Mining speed in rock hit points per tick: a novice clears a granite cell in ~12 s at 1x, a master in ~4 s.</summary>
    public static float MiningRate(Pawn p) => (2f + p.Skills[(int)SkillId.Mining] * 0.25f) * p.StatFactor(Stat.WorkSpeed);

    void RunMineJob(Pawn p, Job j)
    {
        if (Map.Buildings[j.TargetCell] != Building.Granite) { EndJob(p); return; }
        Vector2 target = Map.CellCenter(j.TargetCell);
        if (j.Stage == 0)
        {
            int spot = InteractionSpot(j.TargetCell, p.Position);
            if (spot < 0) { EndJob(p, "cannot reach the rock face."); return; }
            if (Vector2.Distance(p.Position, target) <= Reach || FollowPathTo(p, spot, out bool failed, 0.3f)) { j.Stage = 1; p.Path.Clear(); p.PathIndex = 0; }
            else { if (failed) EndJob(p, "cannot reach the rock face."); return; }
        }
        Vector2 look = target - p.Position;
        if (look.LengthSquared() > 1e-4f) p.Facing = MathF.Atan2(look.Y, look.X);
        p.Anim = PawnAnim.Swing;
        if (++j.Timer % 45 == 0) { p.AnimTime = 0; Events.Add(new SimEvent(SimEventKind.MiningHit, target, null, j.TargetCell)); }
        float hp = Map.BuildingHp[j.TargetCell] - MiningRate(p);
        if (hp > 0) { Map.BuildingHp[j.TargetCell] = (ushort)hp; return; }
        Map.MineOut(j.TargetCell);
        Events.Add(new SimEvent(SimEventKind.Mined, target, "granite mined", j.TargetCell));
        Log.Info($"{p.Name} mined granite at cell {j.TargetCell}");
        if (Rng.Chance(0.4f)) SpawnItem(Defs.GraniteChunk, 1, target);
        EndJob(p);
    }

    bool DoorOccupied(int cell)
    {
        var c = Map.CellCenter(cell);
        foreach (var o in Pawns) if (!o.Dead && MathF.Abs(o.Position.X - c.X) < 0.5f + Pawn.Radius && MathF.Abs(o.Position.Y - c.Y) < 0.5f + Pawn.Radius) return true;
        foreach (var a in Animals) if (Vector2.Distance(a.Position, c) < 0.8f) return true;
        foreach (var it in ItemsAt(cell)) return true;
        return false;
    }

    /// <summary>
    /// Picks an item up: a weapon goes to the hands if they are free, everything else into the inventory; when the
    /// inventory has no room, free hands carry it. Respects the maximum load; takes part of a stack when only part fits.
    /// </summary>
    public bool PickUp(Pawn p, Item it)
    {
        if (!it.Spawned) throw new InvalidOperationException($"{it} is not on the ground");
        float free = p.CarryBasisKg * Pawn.MaxLoad - p.CarriedMass;
        int canCarry = it.Def.Mass <= 0 ? it.Count : Math.Min(it.Count, (int)MathF.Floor(free / it.Def.Mass + 1e-4f));
        if (canCarry <= 0)
        {
            Message($"{p.Name} cannot carry any more weight ({p.CarriedMass:F1} kg).", p.Position);
            return false;
        }
        if (it.Def.Kind == ThingKind.Weapon && p.Held == null)
        {
            Despawn(it);
            p.Held = it;
            Events.Add(new SimEvent(SimEventKind.ItemPickedUp, it.Position, $"{p.Name} takes the {it.Def.Label} in hand", it.Id));
            Log.Info($"{p.Name} picked up {it} into the hands");
            return true;
        }
        int before = it.Count;
        int taken = 0;
        if (it.Def.StackLimit == 1)
        {
            if (p.Inventory.FindSpot(it.Def, out _, out _, out _)) { Despawn(it); p.Inventory.TryInsert(it); taken = 1; }
        }
        else
        {
            var src = canCarry < it.Count ? new Item(NewId(), it.Def, canCarry) : it;
            taken = p.Inventory.Absorb(src, NewId);
            if (src != it) it.Count -= taken;
            if (it.Count == 0) Despawn(it);
        }
        if (taken == 0 && p.Held == null && canCarry == it.Count)
        {
            // no room in the pack: carry it in the hands
            Despawn(it);
            p.Held = it;
            Events.Add(new SimEvent(SimEventKind.ItemPickedUp, it.Position, $"{p.Name} carries {it.Label} in hand", it.Id));
            Log.Info($"{p.Name} picked up {it} into the hands (inventory full)");
            return true;
        }
        if (taken == 0)
        {
            Message($"{p.Name} has no room for {it.Def.Label}.", p.Position);
            return false;
        }
        Events.Add(new SimEvent(SimEventKind.ItemPickedUp, it.Position, $"+{taken} {it.Def.Label}", it.Id));
        Log.Info($"{p.Name} picked up {taken}/{before} {it.Def.Id} (#{it.Id}); carrying {p.CarriedMass:F1} kg");
        return true;
    }

    /// <summary>Drops an item from the inventory or the hands onto the ground in front of the colonist.</summary>
    public void DropFromInventory(Pawn p, Item it)
    {
        bool removed = p.Inventory.Remove(it);
        if (!removed && p.Held == it) { p.Held = null; removed = true; }
        Invariant.Check(removed, $"{p.Name} does not carry {it}");
        PlaceOnGround(it, p.Position + new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing)) * 0.6f);
        Events.Add(new SimEvent(SimEventKind.ItemDropped, it.Position, null, it.Id));
        Log.Info($"{p.Name} dropped {it}");
    }

    /// <summary>Takes an item from the inventory into the hands; whatever was held goes into the inventory (or the ground).</summary>
    public void EquipFromInventory(Pawn p, Item item)
    {
        bool removed = p.Inventory.Remove(item);
        Invariant.Check(removed, $"{p.Name} does not carry {item} in the inventory");
        if (p.Held != null) Unequip(p);
        p.Held = item;
        Log.Info($"{p.Name} took {item} in hand");
    }

    /// <summary>Puts what is held into the inventory (or drops it if there is no room).</summary>
    public void Unequip(Pawn p)
    {
        if (p.Held == null) return;
        var w = p.Held;
        p.Held = null;
        if (w.Def.StackLimit == 1 ? p.Inventory.TryInsert(w) : TryAbsorbWhole(p.Inventory, w)) { Log.Info($"{p.Name} put {w} away"); return; }
        PlaceOnGround(w, p.Position);
        Message($"{p.Name} has no room for the {w.Def.Label} and drops it.", p.Position);
    }

    /// <summary>Moves a whole stack into a grid, or nothing (no partial moves out of the hands).</summary>
    bool TryAbsorbWhole(InventoryGrid g, Item it)
    {
        if (!g.FindSpot(it.Def, out int x, out int y, out bool rot)) return false;
        g.Place(it, x, y, rot);
        return true;
    }

    /// <summary>
    /// Takes off a worn garment into the inventory: at the given grid spot (drag and drop) or, with x &lt; 0, at the
    /// first free spot. Returns false (and tells the player) when it does not fit.
    /// </summary>
    public bool TakeOff(Pawn p, Item worn, int x = -1, int y = -1, bool rotated = false)
    {
        Invariant.Check(p.Apparel.Contains(worn), $"{p.Name} is not wearing {worn}");
        if (x < 0 && !p.Inventory.FindSpot(worn.Def, out x, out y, out rotated))
        {
            Message($"{p.Name} has no room in the inventory for the {worn.Def.Label}.", p.Position);
            return false;
        }
        var e = new GridEntry { Item = worn, X = x, Y = y, Rotated = rotated };
        if (!p.Inventory.Fits(x, y, e.W, e.H)) return false;
        p.Apparel.Remove(worn);
        p.Inventory.Place(worn, x, y, rotated);
        Log.Info($"{p.Name} took off {worn} into the inventory at {x},{y}");
        return true;
    }

    /// <summary>Takes off a worn garment and drops it on the ground.</summary>
    public void TakeOffAndDrop(Pawn p, Item worn)
    {
        bool removed = p.Apparel.Remove(worn);
        Invariant.Check(removed, $"{p.Name} is not wearing {worn}");
        PlaceOnGround(worn, p.Position + new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing)) * 0.6f);
        Events.Add(new SimEvent(SimEventKind.ItemDropped, worn.Position, null, worn.Id));
        Log.Info($"{p.Name} took off and dropped {worn}");
    }

    /// <summary>Puts on a garment from the inventory (or the hands). Refuses, with a message, when a worn item covers the same layer and body part.</summary>
    public bool WearFromInventory(Pawn p, Item it)
    {
        if (it.Def.Kind != ThingKind.Apparel) throw new ArgumentException($"{it} is not apparel");
        var clash = p.WearConflicts(it.Def);
        if (clash.Count > 0)
        {
            Message($"{p.Name} must take off the {string.Join(", ", clash.ConvertAll(c => c.Def.Label))} first.", p.Position);
            return false;
        }
        bool removed = p.Inventory.Remove(it);
        if (!removed && p.Held == it) { p.Held = null; removed = true; }
        Invariant.Check(removed, $"{p.Name} does not carry {it}");
        p.Wear(it);
        Log.Info($"{p.Name} put on {it}");
        return true;
    }

    /// <summary>Moves an item between the inventory and the hands at an explicit grid spot (drag and drop).</summary>
    public bool MoveHeldToGrid(Pawn p, int x, int y, bool rotated)
    {
        var it = p.Held ?? throw new InvalidOperationException($"{p.Name} holds nothing");
        var e = new GridEntry { Item = it, X = x, Y = y, Rotated = rotated };
        if (!p.Inventory.Fits(x, y, e.W, e.H)) return false;
        p.Held = null;
        p.Inventory.Place(it, x, y, rotated);
        Log.Info($"{p.Name} put {it} into the inventory at {x},{y}");
        return true;
    }

    public void EatFromInventory(Pawn p, Item food)
    {
        if (food.Def.Kind != ThingKind.Food) throw new ArgumentException($"{food} is not food");
        // eat until fed or the stack runs out (one unit per bite)
        int bites = 0;
        while (food.Count > 0 && p.Needs.Food < 0.97f && bites < 40)
        {
            p.Needs.Food = MathF.Min(1f, p.Needs.Food + food.Def.Nutrition);
            p.Needs.Thirst = MathF.Min(1f, p.Needs.Thirst + food.Def.Hydration);
            food.Count--; bites++;
        }
        if (food.Count == 0 && !p.Inventory.Remove(food) && p.Held == food) p.Held = null;
        Events.Add(new SimEvent(SimEventKind.Ate, p.Position, $"{p.Name} ate {bites} {food.Def.Label}"));
        Log.Info($"{p.Name} ate {bites}x {food.Def.Id}; food {p.Needs.Food:P0}");
    }

    void Die(Pawn p)
    {
        p.Anim = PawnAnim.Dead;
        if (p.Mode == ControlMode.Direct) p.Mode = ControlMode.Autonomous;
        ClearJob(p);
        Events.Add(new SimEvent(SimEventKind.PawnDied, p.Position, $"{p.FullName} has died: {p.Health.DeathCause}", p.Id));
        Log.Warn($"Colonist {p.FullName} #{p.Id} died: {p.Health.DeathCause}");
    }
}
