using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Remade.Diagnostics;
using Remade.Game.UI;
using Remade.Map;
using Remade.Pawns;
using Remade.Sim;
using Remade.Things;
using SV2 = System.Numerics.Vector2;

namespace Remade.Game;

/// <summary>
/// Scripted driver for automated runs (<c>--auto="cmd=arg; cmd=arg; ..."</c>), executed in order. It works through
/// the real game: menu buttons are pressed by their text, keys are sent as real input actions, mouse clicks as real
/// mouse events at the screen position of a target. Results are written to the log; <c>quit</c> ends the run with
/// a PASS/FAIL summary and exit code.
///
/// Commands: wait=s · waitfor=game|globe|planet|menu (timeout 120 s) · click=Button text · key=action · hold=action,s ·
/// shot=name · select_tile=start|N · overlay=None|Temperature|Elevation|Precipitation · mapsize=N · pawn=i · control=i ·
/// speed=N · tab=Bio|Equipment|Needs|Health · layer=Skin|… · walkto=item:bow|door|deer|x,y · interact[=label] ·
/// face=deer · aimshoot=n (hold RMB at the nearest deer, click LMB n times) · deer_near=dist · hour=H · world ·
/// cam=dist[,yaw] · expect=bow|meat|deerdead · log=text · quit
/// </summary>
public partial class AutoPilot : Node
{
    readonly Queue<(string cmd, string arg)> _queue = new();
    double _wait;
    string _waitFor;
    double _waitForTimeout;
    readonly List<string> _failures = new();
    readonly List<string> _errors = new();
    int _graniteAtStart = -1;
    int _shots;
    Func<bool> _activity;     // a multi-frame command in progress (returns true when done)
    double _activityTimeout;
    string _activityName;

    public AutoPilot(string script)
    {
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.IndexOf('=');
            _queue.Enqueue(eq < 0 ? (raw, "") : (raw[..eq].Trim(), raw[(eq + 1)..].Trim()));
        }
        ProcessMode = ProcessModeEnum.Always;
        Log.Info($"AutoPilot: {_queue.Count} commands");
        // any error logged during an automated run fails it
        Log.ErrorWritten += (lvl, msg) => { if (!msg.StartsWith("AutoPilot FAILURE")) lock (_errors) _errors.Add(msg); };
    }

    GameView Game => Main.I.Game;

    public override void _Process(double delta)
    {
        if (_activity != null)
        {
            _activityTimeout -= delta;
            if (_activity()) { _activity = null; ReleaseAll(); }
            else if (_activityTimeout <= 0) { Fail($"{_activityName} timed out"); _activity = null; ReleaseAll(); }
            return;
        }
        if (_waitFor != null)
        {
            _waitForTimeout -= delta;
            if (StateReached(_waitFor)) { Log.Info($"AutoPilot: reached '{_waitFor}'"); _waitFor = null; _wait = 0.5; }
            else if (_waitForTimeout <= 0) { Fail($"waitfor {_waitFor} timed out"); _waitFor = null; }
            return;
        }
        if (_wait > 0) { _wait -= delta; return; }
        if (_queue.Count == 0) return;
        var (cmd, arg) = _queue.Dequeue();
        Log.Info($"AutoPilot: {cmd}{(arg.Length > 0 ? "=" + arg : "")}");
        Log.Action($"autopilot {cmd}={arg}");
        Run(cmd, arg);
    }

    bool StateReached(string s) => s switch
    {
        "game" => Game != null,
        "menu" => Main.I.CurrentScreen is MainMenu,
        "planet" => Main.I.CurrentScreen is PlanetScreen ps && ps.View.Ready3D,
        "globe" => (Main.I.CurrentScreen is PlanetScreen p2 && p2.View.Ready3D) || (Game?.Hud.World?.View.Ready3D ?? false),
        _ => throw new ArgumentException($"unknown waitfor state '{s}'"),
    };

    void Run(string cmd, string arg)
    {
        switch (cmd)
        {
            case "wait": _wait = F(arg); break;
            case "waitfor": _waitFor = arg; _waitForTimeout = 120; break;
            case "shot": Main.I.Screenshot($"{++_shots:00}_{arg}"); break;
            case "log": Log.Info("AutoPilot note: " + arg); break;
            case "click": ClickButton(arg.StartsWith("=") ? arg[1..] : arg, exact: arg.StartsWith("=")); _wait = 0.3; break;
            case "key": PressAction(arg); _wait = 0.2; break;
            case "hold":
            {
                var parts = arg.Split(',');
                Input.ActionPress(parts[0]);
                double until = Time.GetTicksMsec() / 1000.0 + F(parts[1]);
                Begin("hold", () => Time.GetTicksMsec() / 1000.0 >= until, F(parts[1]) + 5);
                break;
            }
            case "select_tile":
            {
                var ps = Main.I.CurrentScreen as PlanetScreen ?? throw new InvalidOperationException("select_tile outside the planet screen");
                int t = arg == "start" ? Main.I.Setup.Planet.FindStartTile() : int.Parse(arg);
                ps.SelectTile(t);
                _wait = 1.5;
                break;
            }
            case "overlay":
            {
                var view = (Main.I.CurrentScreen as PlanetScreen)?.View ?? Game?.Hud.World?.View ?? throw new InvalidOperationException("no planet view");
                view.SetOverlay(Enum.Parse<Remade.Game.Planet3D.PlanetBaker.OverlayKind>(arg));
                _wait = 1.5;
                break;
            }
            case "mapsize": Main.I.Setup.MapSize = int.Parse(arg); break;
            case "pawn": Game.Select(Game.Sim.Pawns[int.Parse(arg)]); Game.FocusOn(Game.Sim.Pawns[int.Parse(arg)]); _wait = 0.3; break;
            case "control":
            {
                var p = Game.Sim.Pawns[int.Parse(arg)];
                Game.Select(p);
                ClickButton("Direct control");
                _wait = 0.5;
                break;
            }
            case "speed": Game.Hud.SetSpeed(int.Parse(arg)); break;
            case "tab": Game.Hud.Inspect.OpenTabByName(arg); _wait = 0.4; break;
            case "layer": ClickButton(arg, exact: true); _wait = 0.3; break;
            case "world": Game.Hud.ToggleWorld(); _wait = 0.5; break;
            case "cam":
            {
                var p = arg.Split(',');
                SetCamDistance(F(p[0]));
                if (p.Length > 1) Game.Camera.Rotate(Mathf.DegToRad(F(p[1])) - Game.Camera.Yaw);
                _wait = 0.8;
                break;
            }
            case "hour": AdvanceToHour(F(arg)); _wait = 0.5; break;
            case "camfind":
            {
                // centre the camera on the nearest cell of a kind (water, rock, tree, bush, cabin)
                var sim = Game.Sim;
                var f = Game.Camera.Focus;
                Func<int, bool> match = arg switch
                {
                    "water" => c => sim.Map.TerrainAt(c).Water,
                    "rock" => c => sim.Map.Buildings[c] == Building.Granite,
                    "tree" => c => sim.Map.Plants[c] == Plant.Oak,
                    "bush" => c => sim.Map.Plants[c] == Plant.BerryBush,
                    "cabin" => c => sim.Map.Buildings[c] == Building.Door,
                    _ => throw new ArgumentException($"camfind: unknown kind '{arg}'"),
                };
                int best = -1; float bestD = float.MaxValue;
                for (int c = 0; c < sim.Map.CellCount; c++)
                {
                    if (!match(c)) continue;
                    float d = SV2.DistanceSquared(sim.Map.CellCenter(c), new SV2(f.X, f.Z));
                    if (d < bestD) { bestD = d; best = c; }
                }
                if (best < 0) { Fail($"camfind: no {arg} on this map"); break; }
                var cc = sim.Map.CellCenter(best);
                Game.Camera.Follow = false;
                Game.Camera.JumpTo(new Vector3(cc.X, 0, cc.Y));
                _wait = 1.0;
                break;
            }
            case "walkto": WalkTo(arg); break;
            case "interact": Interact(arg); break;
            case "deer_near": DeerNear(F(arg)); _wait = 0.3; break;
            case "aimshoot": AimShoot(int.Parse(arg)); break;
            case "expect": Expect(arg); break;
            case "quit": Finish(); break;
            default: Fail($"unknown command '{cmd}'"); break;
        }
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    void Begin(string name, Func<bool> step, double timeout)
    {
        _activityName = name;
        _activity = step;
        _activityTimeout = timeout;
    }

    void Fail(string why)
    {
        _failures.Add(why);
        Log.Error("AutoPilot FAILURE: " + why);
    }

    void SetCamDistance(float d)
    {
        // zoom by repeated factors (uses the rig's own clamping)
        float cur = Game.Camera.Distance;
        Game.Camera.Zoom(d / Math.Max(1f, cur));
    }

    // ------------------------------------------------------------------ UI

    void ClickButton(string text, bool exact = false)
    {
        var b = FindButton(Main.I, text, exact) ?? FindButton(Main.I.GetTree().Root, text, exact);
        if (b == null) { Fail($"no visible button '{text}'"); return; }
        if (b.Disabled) { Fail($"button '{text}' is disabled"); return; }
        b.EmitSignal(BaseButton.SignalName.Pressed);
    }

    static Button FindButton(Node n, string text, bool exact)
    {
        if (n is Button b && b.IsVisibleInTree() && (exact ? b.Text == text : b.Text.Contains(text, StringComparison.OrdinalIgnoreCase))) return b;
        foreach (var c in n.GetChildren())
        {
            var r = FindButton(c, text, exact);
            if (r != null) return r;
        }
        return null;
    }

    static void PressAction(string action)
    {
        if (!InputMap.HasAction(action)) throw new ArgumentException($"no input action '{action}'");
        var events = InputMap.ActionGetEvents(action);
        if (events.Count == 0) throw new InvalidOperationException($"action '{action}' has no binding");
        var k = (InputEventKey)events[0].Duplicate();
        k.Pressed = true;
        Input.ParseInputEvent(k);
        var up = (InputEventKey)k.Duplicate();
        up.Pressed = false;
        Input.ParseInputEvent(up);
    }

    static void ReleaseAll()
    {
        foreach (var a in new[] { "move_up", "move_down", "move_left", "move_right", "sprint" }) Input.ActionRelease(a);
    }

    // ------------------------------------------------------------------ game actions

    Pawn Controlled => Game?.Sim.Controlled ?? throw new InvalidOperationException("no colonist under direct control");

    SV2 ResolveTarget(string what)
    {
        var sim = Game.Sim;
        var p = Controlled;
        if (what.StartsWith("item:"))
        {
            var def = Defs.Get(what[5..]);
            var it = sim.Items.Where(i => i.Def == def).OrderBy(i => SV2.Distance(i.Position, p.Position)).FirstOrDefault()
                     ?? throw new InvalidOperationException($"no {def.Id} on the ground");
            return it.Position;
        }
        if (what == "door")
        {
            int door = Array.IndexOf(sim.Map.Buildings, Building.Door);
            return sim.Map.CellCenter(sim.InteractionSpot(door, p.Position));
        }
        if (what == "deer")
        {
            var a = sim.Animals.Where(x => !x.Dead).OrderBy(x => SV2.Distance(x.Position, p.Position)).First();
            return a.Position;
        }
        if (what == "rock")
        {
            // the nearest exposed granite face: walk to its interaction spot
            int best = -1; float bestD = float.MaxValue;
            for (int c = 0; c < sim.Map.CellCount; c++)
            {
                if (sim.Map.Buildings[c] != Building.Granite) continue;
                float d = SV2.DistanceSquared(sim.Map.CellCenter(c), p.Position);
                if (d < bestD && sim.InteractionSpot(c, p.Position) >= 0) { bestD = d; best = c; }
            }
            if (best < 0) throw new InvalidOperationException("no exposed granite on this map");
            _graniteAtStart = sim.Map.Buildings.Count(b => b == Building.Granite);
            return sim.Map.CellCenter(sim.InteractionSpot(best, p.Position));
        }
        if (what == "outside") return sim.FindStandableNear(new SV2(sim.Map.Width / 2f, sim.Map.Height / 2f), 10);
        var parts = what.Split(',');
        return new SV2(F(parts[0]), F(parts[1]));
    }

    /// <summary>Walks the controlled colonist with the real move keys along an A* route (like a player steering).</summary>
    void WalkTo(string what)
    {
        var sim = Game.Sim;
        var pawn = Controlled;
        var target = ResolveTarget(what);
        int goal = sim.Map.CellAt(target);
        if (sim.Paths.Cost[goal] == 0 || sim.Map.Buildings[goal] == Building.Door) goal = sim.InteractionSpot(goal, pawn.Position);
        var path = new List<SV2>();
        var res = sim.Pathfinder.FindPath(sim.Map.CellAt(pawn.Position), goal, path);
        if (res != PathResult.Found) { Fail($"walkto {what}: {res}"); return; }
        path.Add(target);
        Log.Debug($"walkto {what}: from {pawn.Position} to {target}, path {string.Join(" ", path.Select(v => $"({v.X:F1},{v.Y:F1})"))}");
        int idx = 0;
        float stop = what.StartsWith("item:") ? 0.8f : what is "rock" or "door" ? 0.25f : 0.5f;
        SV2 lastPos = pawn.Position; double stuck = 0;
        int replans = 0;
        Begin($"walkto {what}", () =>
        {
            if (SV2.Distance(pawn.Position, target) <= stop) return true;
            while (idx < path.Count - 1 && SV2.Distance(pawn.Position, path[idx]) < 0.2f) idx++;
            if (stuck > 1.0 && replans < 3)
            {
                // blocked: plan again from where we are (like a player would)
                replans++; stuck = 0;
                path.Clear();
                if (sim.Pathfinder.FindPath(sim.Map.CellAt(pawn.Position), goal, path) == PathResult.Found)
                {
                    path.Insert(0, sim.Map.CellCenter(sim.Map.CellAt(pawn.Position)));
                    path.Add(target);
                    idx = 0;
                    Log.Debug($"walkto {what}: re-planned from {pawn.Position}");
                }
            }
            var d = path[idx] - pawn.Position;
            SteerKeys(d);
            if (SV2.Distance(lastPos, pawn.Position) < 0.001f && sim.SpeedIndex > 0) stuck += GetProcessDeltaTime(); else stuck = 0;
            lastPos = pawn.Position;
            if (stuck > 3)
            {
                int c = sim.Map.CellAt(pawn.Position);
                sim.Map.XY(c, out int cx, out int cy);
                var around = new System.Text.StringBuilder();
                for (int yy = cy - 1; yy <= cy + 1; yy++)
                    for (int xx = cx - 1; xx <= cx + 1; xx++)
                        around.Append(sim.Map.InBounds(xx, yy) ? (sim.Map.Blocked(sim.Map.Index(xx, yy)) ? '#' : sim.Map.Plants[sim.Map.Index(xx, yy)] == Plant.Oak ? 'T' : '.') : 'X').Append(xx == cx + 1 ? "/" : "");
                Fail($"walkto {what}: stuck at {pawn.Position} heading to waypoint {idx} {path[idx]} (cells around {cx},{cy}: {around}, job {pawn.Job?.Kind.ToString() ?? "none"})");
                return true;
            }
            return false;
        }, 90);
    }

    /// <summary>Presses the move keys that best match a map-space direction, given the camera yaw.</summary>
    void SteerKeys(SV2 mapDir)
    {
        ReleaseAll();
        if (mapDir.LengthSquared() < 1e-6f) return;
        float yaw = Game.Camera.Yaw;
        // map → screen (inverse of CameraRig.ScreenToMapDir)
        var right = new SV2(MathF.Cos(yaw), -MathF.Sin(yaw));
        var down = new SV2(MathF.Sin(yaw), MathF.Cos(yaw));
        var dir = SV2.Normalize(mapDir);
        float sx = SV2.Dot(dir, right), sy = SV2.Dot(dir, down);
        // analog strengths, like a gamepad stick, through the same input actions the keys use
        if (sx > 0.01f) Input.ActionPress("move_right", sx);
        if (sx < -0.01f) Input.ActionPress("move_left", -sx);
        if (sy > 0.01f) Input.ActionPress("move_down", sy);
        if (sy < -0.01f) Input.ActionPress("move_up", -sy);
    }

    void Interact(string label)
    {
        var list = Game.NearbyInteractions;
        if (list.Count == 0) { Fail($"interact '{label}': nothing in reach"); return; }
        if (label.Length > 0)
        {
            int idx = list.FindIndex(i => i.Label.Contains(label, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { Fail($"interact '{label}': not among [{string.Join(", ", list.Select(i => i.Label))}]"); return; }
            // scroll the wheel until it is selected (real wheel events)
            for (int k = 0; k < idx; k++) Wheel(false);
        }
        PressAction("interact");
        var pawn = Controlled;
        Begin("interact", () => pawn.Job == null, 20);
    }

    void Wheel(bool up)
    {
        var e = new InputEventMouseButton { ButtonIndex = up ? MouseButton.WheelUp : MouseButton.WheelDown, Pressed = true, Position = Game.MouseOverride ?? Vector2.Zero };
        Input.ParseInputEvent(e);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = e.ButtonIndex, Pressed = false, Position = e.Position });
    }

    /// <summary>Moves the nearest living deer into the open at a distance in front of the controlled colonist.</summary>
    void DeerNear(float dist)
    {
        var sim = Game.Sim;
        var p = Controlled;
        for (int k = 0; k < 64; k++)
        {
            float ang = k * 0.4f;
            var s = sim.FindStandableNear(p.Position + new SV2(MathF.Cos(ang), MathF.Sin(ang)) * dist, 4);
            if (!sim.LineOfSight(p.Position, s) || SV2.Distance(s, p.Position) < dist * 0.6f) continue;
            var deer = sim.Animals.Where(a => !a.Dead).OrderBy(a => SV2.Distance(a.Position, p.Position)).First();
            deer.Position = s;
            deer.Path.Clear(); deer.PathIndex = 0;
            deer.State = AnimalState.Graze; deer.StateTimer = 2000;
            Log.Info($"AutoPilot: placed {deer} at {s} ({SV2.Distance(s, p.Position):F1} m away)");
            return;
        }
        Fail("deer_near: no open spot");
    }

    /// <summary>Holds the right mouse button aimed at the nearest deer and clicks the left button up to n times.</summary>
    void AimShoot(int n)
    {
        var sim = Game.Sim;
        var pawn = Controlled;
        var deer = sim.Animals.Where(a => !a.Dead).OrderBy(a => SV2.Distance(a.Position, pawn.Position)).First();
        int shots = 0;
        double next = 0;
        bool pressed = false;
        Begin("aimshoot", () =>
        {
            var g = Game;
            var screen = g.Camera.Camera.UnprojectPosition(new Vector3(deer.Position.X, sim.Map.StandHeight(deer.Position.X, deer.Position.Y) + 0.9f, deer.Position.Y));
            g.MouseOverride = screen;
            if (!pressed)
            {
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = screen, ButtonMask = MouseButtonMask.Right });
                pressed = true;
                next = Time.GetTicksMsec() / 1000.0 + 0.5;
                return false;
            }
            if (deer.Dead || shots >= n || pawn.Arrows == 0)
            {
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = screen });
                g.MouseOverride = null;
                Log.Info($"AutoPilot: aimshoot done after {shots} shots, deer {(deer.Dead ? "dead" : "alive")}, arrows left {pawn.Arrows}");
                return true;
            }
            double now = Time.GetTicksMsec() / 1000.0;
            if (now >= next && pawn.WeaponCooldown == 0)
            {
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = screen, ButtonMask = MouseButtonMask.Left | MouseButtonMask.Right });
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = screen, ButtonMask = MouseButtonMask.Right });
                shots++;
                next = now + 1.4;
                // a fleeing deer that runs out of range is brought back into view (we test shooting, not tracking)
                if (SV2.Distance(deer.Position, pawn.Position) > 18f) DeerNear(8f);
            }
            return false;
        }, 120);
    }

    void AdvanceToHour(float hour)
    {
        var sim = Game.Sim;
        long target = sim.Tick - sim.Tick % Remade.Core.GameTime.TicksPerDay + (long)(hour * Remade.Core.GameTime.TicksPerHour);
        if (target <= sim.Tick) target += Remade.Core.GameTime.TicksPerDay;
        int n = 0;
        while (sim.Tick < target) { sim.Step(); n++; }
        Log.Info($"AutoPilot: advanced {n} ticks to {Remade.Core.GameTime.ClockString(sim.Tick)}");
    }

    void Expect(string what)
    {
        var sim = Game.Sim;
        var p = sim.Controlled ?? Game.SelectedPawn;
        bool ok = what switch
        {
            "bow" => p.Weapon?.Def == Defs.Bow,
            "arrows" => p.Arrows > 0,
            "meat" => p.CountInInventory(Defs.Venison) > 0,
            "deerdead" => sim.Animals.Any(a => a.Dead) || sim.Items.Any(i => i.Def == Defs.Venison),
            "dooropen" => sim.Map.DoorOpen.Values.Any(v => v),
            "mined" => _graniteAtStart > 0 && sim.Map.Buildings.Count(b => b == Building.Granite) < _graniteAtStart,
            _ => throw new ArgumentException($"unknown expectation '{what}'"),
        };
        if (ok) Log.Info($"AutoPilot: expectation '{what}' met");
        else Fail($"expectation '{what}' not met");
    }

    void Finish()
    {
        ReleaseAll();
        lock (_errors) foreach (var e in _errors) _failures.Add("logged error: " + e);
        bool pass = _failures.Count == 0;
        Log.Info($"AUTOPILOT RESULT: {(pass ? "PASS" : "FAIL")} ({_failures.Count} failures){(pass ? "" : ": " + string.Join(" | ", _failures))}");
        Log.Flush();
        Log.Shutdown();
        GetTree().Quit(pass ? 0 : 1);
    }
}
