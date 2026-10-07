using System;
using System.Collections.Generic;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Game.Render;
using Remade.Game.UI;
using Remade.Map;
using Remade.Pawns;
using Remade.Sim;
using SV2 = System.Numerics.Vector2;

namespace Remade.Game;

/// <summary>
/// A running colony: owns the simulation, the renderers, the camera, the HUD and all in-game input.
/// <list type="bullet">
/// <item>Left click selects; right click opens the order menu for the selected colonist.</item>
/// <item>Direct control: move keys walk, Shift sprints, E interacts (wheel chooses among several), a quick right
/// click opens the action menu, holding the right button aims (red line to the weapon's reach) and left click shoots
/// or swings.</item>
/// </list>
/// </summary>
public partial class GameView : Node3D
{
    public readonly GameSim Sim;
    readonly CameraRig _cam;
    readonly MapRenderer _map;
    readonly FloraRenderer _flora;
    readonly EntityRenderer _entities;
    readonly Lighting _light;
    readonly WeatherFx _weather;
    readonly Audio3D _audio;
    public readonly Hud Hud;
    long _frame;
    public Pawn SelectedPawn { get; private set; }
    List<Interaction> _nearby = new();
    int _interactionIndex;
    bool _rmbDown, _aiming;
    double _rmbTime;
    Vector2 _rmbPos;
    bool _worldVisible = true;
    double _perfTimer;
    int _lastCursorCell = -1;
    /// <summary>Automation can drive the mouse position without moving the OS cursor.</summary>
    public Vector2? MouseOverride;
    Vector2 MousePos => MouseOverride ?? GetViewport().GetMousePosition();

    public GameView(GameSim sim)
    {
        Sim = sim;
        Name = "GameView";
        using var _t = Log.Time("GameView setup", 3000);
        _light = new Lighting(sim);
        AddChild(_light);
        _map = new MapRenderer(sim);
        AddChild(_map);
        _flora = new FloraRenderer(sim, _map.HeightTexture);
        AddChild(_flora);
        _entities = new EntityRenderer(sim);
        AddChild(_entities);
        _weather = new WeatherFx(sim);
        AddChild(_weather);
        _audio = new Audio3D(sim);
        AddChild(_audio);
        _cam = new CameraRig { MapSize = new Vector2(sim.Map.Width, sim.Map.Height), HeightAt = (x, z) => sim.Map.StandHeight(x, z) };
        AddChild(_cam);
        var first = sim.Pawns.Find(p => !p.Dead) ?? sim.Pawns[0];
        _cam.JumpTo(new Vector3(first.Position.X, 0, first.Position.Y));
        Hud = new Hud(sim, this);
        AddChild(Hud);
        Hud.Inspect.OnModeChanged = OnModeChanged;
        Select(first);
        if (Args.Has("gfx-off")) DisableFeatures(Args.Get("gfx-off"));
        Hud.Message($"Your colonists have landed. {sim.Weather.Describe()}, {sim.Weather.Temperature:F0} °C. An old cabin stands nearby.", UiKit.Accent);
        Log.Info($"GameView ready: map {sim.Map.Width}x{sim.Map.Height}, {sim.Pawns.Count} colonists, {sim.Animals.Count} deer");
    }

    /// <summary>Profiling aid (--gfx-off=shadows,ssao,fog,grass,trees,water,taa,glow,hud): turns features off to measure their cost.</summary>
    void DisableFeatures(string list)
    {
        foreach (var f in list.Split(','))
        {
            switch (f.Trim())
            {
                case "shadows": _light.SetShadows(false); break;
                case "ssao": _light.Env.SsaoEnabled = false; break;
                case "fog": _light.Env.VolumetricFogEnabled = false; _light.Env.FogEnabled = false; break;
                case "glow": _light.Env.GlowEnabled = false; break;
                case "grass": _flora.GrassEnabled = false; break;
                case "trees": _flora.Visible = false; break;
                case "water": _map.WaterVisible = false; break;
                case "taa": Main.I.GetViewport().UseTaa = false; break;
                case "hud": Hud.Visible = false; break;
                default: throw new ArgumentException($"--gfx-off: unknown feature '{f}'");
            }
            Log.Info($"Feature disabled for profiling: {f}");
        }
    }

    public void ApplyGraphicsSettings()
    {
        _light.ApplyQuality();
        _flora.SetGrassRadius(42);
    }

    public void SetWorldVisible(bool v)
    {
        _worldVisible = v;
        Visible = v;
        _cam.Camera.Current = v;
    }

    // ------------------------------------------------------------------ selection

    public void Select(Pawn p)
    {
        SelectedPawn = p;
        _entities.Selected = p;
        Hud.Inspect.Show(p);
        if (p != null) Log.Action($"select {p.Name}");
    }

    public void FocusOn(Pawn p) => _cam.JumpTo(new Vector3(p.Position.X, 0, p.Position.Y));

    void OnModeChanged(Pawn p)
    {
        _aiming = false;
        Sim.Input.Aim = false;
        Sim.Input.Move = SV2.Zero;
        if (p.Mode == ControlMode.Direct)
        {
            _cam.Follow = true;
            Hud.Message($"You control {p.Name}. {Settings.KeyName("move_up")}{Settings.KeyName("move_left")}{Settings.KeyName("move_down")}{Settings.KeyName("move_right")} to move, " +
                        $"{Settings.KeyName("interact")} to interact, hold right mouse to aim, left click to shoot.", UiKit.Accent);
        }
        else _cam.Follow = false;
    }

    // ------------------------------------------------------------------ frame

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _frame++;
        var controlled = Sim.Controlled;
        UpdateDirectInput(controlled);
        if (Hud.PauseMenu == null) Sim.Advance(delta);
        DispatchEvents();
        if (Sim.FrameChanges.Count > 0)
        {
            _map.OnCellsChanged(Sim.FrameChanges);
            _flora.OnCellsChanged(Sim.FrameChanges);
            Sim.FrameChanges.Clear();
        }

        if (!_worldVisible) return;
        if (controlled != null) { _cam.Follow = _cam.Follow || controlled == SelectedPawn; _cam.FollowTarget = _entities.PawnVisualPos(controlled); }
        else UpdateCameraKeys(dt);
        _cam.UpdateRig(dt);
        var focus = _cam.Focus;
        Sim.Focus = new SV2(focus.X, focus.Z);
        var vpSize = GetViewport().GetVisibleRect().Size;
        var rect = _cam.VisibleRect(vpSize);
        _map.UpdateStreaming(rect, _frame);
        _flora.UpdateStreaming(rect, _frame);
        _flora.UpdateGrass(focus, _cam.Distance);
        _map.AnimateDoors(dt);
        _light.Update(dt, focus, _cam.Distance);
        _weather.Update(focus);
        _audio.Update(dt, focus, _cam.Camera.GlobalPosition, _light.Daylight);
        // canopy reveal around the controlled (or selected) colonist
        var reveal = controlled ?? SelectedPawn;
        if (reveal != null)
        {
            var rp = _entities.PawnVisualPos(reveal);
            RenderingServer.GlobalShaderParameterSet("reveal_pos", new Vector4(rp.X, rp.Y, rp.Z, controlled != null ? 7.5f : 4.5f));
        }
        _entities.ShowAim = controlled != null && _aiming;
        _entities.AimRange = controlled != null ? GameSim.AttackRange(controlled) : 0;
        _entities.Update(dt);
        UpdateHudOverlays(controlled);
        Perf(delta);
    }

    void Perf(double delta)
    {
        if (!Args.Has("perf")) return;
        _perfTimer += delta;
        if (_perfTimer < 1) return;
        _perfTimer = 0;
        var rs = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
        var prims = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
        var vmem = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);
        Log.Info($"[perf] fps {Engine.GetFramesPerSecond():F0} | frame {delta * 1000:F1} ms | sim {Sim.LastStepMs:F2} ms ({Sim.LastTicksThisFrame} ticks) | draws {rs} | prims {prims / 1000}k | " +
                 $"vram {vmem / 1048576} MB | mem {GC.GetTotalMemory(false) / 1048576} MB managed, {OS.GetStaticMemoryUsage() / 1048576} MB static | chunks {_map.BuiltChunks} | flora {_flora.Instances}");
    }

    Vector3 ToWorld(SV2 p) => new(p.X, Sim.Map.StandHeight(p.X, p.Y), p.Y);

    void DispatchEvents()
    {
        foreach (var e in Sim.Events)
        {
            _audio.OnEvent(e, ToWorld);
            switch (e.Kind)
            {
                case SimEventKind.Message: Hud.Message(e.Text); break;
                case SimEventKind.AnimalKilled: Hud.Message(e.Text, UiKit.Good); break;
                case SimEventKind.PawnDied: Hud.Message(e.Text, UiKit.Bad); break;
                case SimEventKind.ItemPickedUp when e.Text != null: Hud.Message(e.Text); break;
                case SimEventKind.Gathered: Hud.Message(e.Text); break;
            }
        }
        Sim.Events.Clear();
    }

    // ------------------------------------------------------------------ camera keys

    void UpdateCameraKeys(float dt)
    {
        var v = Vector2.Zero;
        if (Input.IsActionPressed("move_up")) v.Y -= 1;
        if (Input.IsActionPressed("move_down")) v.Y += 1;
        if (Input.IsActionPressed("move_left")) v.X -= 1;
        if (Input.IsActionPressed("move_right")) v.X += 1;
        if (Settings.EdgePan && DisplayServer.WindowIsFocused())
        {
            var m = GetViewport().GetMousePosition();
            var s = GetViewport().GetVisibleRect().Size;
            if (m.X < 4) v.X -= 1; if (m.X > s.X - 4) v.X += 1;
            if (m.Y < 4) v.Y -= 1; if (m.Y > s.Y - 4) v.Y += 1;
        }
        if (v != Vector2.Zero) _cam.Pan(v.Normalized() * _cam.PanSpeed * dt);
        if (Input.IsActionPressed("cam_rotate_left")) _cam.Rotate(-dt * 1.5f);
        if (Input.IsActionPressed("cam_rotate_right")) _cam.Rotate(dt * 1.5f);
    }

    // ------------------------------------------------------------------ direct control

    void UpdateDirectInput(Pawn controlled)
    {
        var inp = Sim.Input;
        if (controlled == null || Hud.PauseMenu != null || !_worldVisible)
        {
            inp.Move = SV2.Zero; inp.Aim = false;
            Hud.ShowInteractions(new List<Interaction>(), 0);
            _entities.Target = null;
            return;
        }
        // analog-capable (gamepad sticks, automation); keyboard gives the usual 8 directions
        var v = Input.GetVector("move_left", "move_right", "move_up", "move_down");
        var md = v == Vector2.Zero ? Vector2.Zero : _cam.ScreenToMapDir(v.Normalized());
        inp.Move = new SV2(md.X, md.Y);
        inp.Sprint = Input.IsActionPressed("sprint");
        // holding the right button past a short click = aiming
        if (_rmbDown && !_aiming && Time.GetTicksMsec() / 1000.0 - _rmbTime > 0.18) _aiming = true;
        inp.Aim = _aiming;
        var gp = _cam.GroundPoint(MousePos);
        if (gp.HasValue) inp.AimPoint = new SV2(gp.Value.X, gp.Value.Z);

        // interactions in reach
        _nearby = Interactions.Nearby(Sim, controlled);
        if (_interactionIndex >= _nearby.Count) _interactionIndex = 0;
        bool busy = controlled.Job != null;
        Hud.ShowInteractions(busy || _aiming ? new List<Interaction>() : _nearby, _interactionIndex);
        _entities.Target = !busy && !_aiming && _nearby.Count > 0 ? _nearby[_interactionIndex] : null;
    }

    // ------------------------------------------------------------------ input events

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_worldVisible)
        {
            if (e.IsActionPressed("world") || e.IsActionPressed("menu")) { Hud.ToggleWorld(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (e.IsActionPressed("menu")) { Hud.TogglePause(); Handled(); return; }
        if (Hud.PauseMenu != null) return;
        if (e.IsActionPressed("world")) { Hud.ToggleWorld(); Handled(); return; }
        var controlled = Sim.Controlled;

        if (e is InputEventKey { Pressed: true, Echo: false })
        {
            if (e.IsActionPressed("pause")) { Hud.SetSpeed(Sim.SpeedIndex == 0 ? _lastSpeed : 0); if (Sim.SpeedIndex != 0) _lastSpeed = Sim.SpeedIndex; Handled(); return; }
            for (int s = 1; s <= 4; s++) if (e.IsActionPressed("speed_" + s)) { Hud.SetSpeed(s); _lastSpeed = s; Handled(); return; }
            if (e.IsActionPressed("interact")) { Interact(controlled); Handled(); return; }
            if (e.IsActionPressed("direct_control") && SelectedPawn != null) { Hud.Inspect.ToggleDirect(); Handled(); return; }
            if (e.IsActionPressed("draft") && SelectedPawn != null) { Hud.Inspect.ToggleDraftExternal(); Handled(); return; }
            if (e.IsActionPressed("focus") && SelectedPawn != null) { FocusOn(SelectedPawn); Handled(); return; }
            if (e.IsActionPressed("next_pawn")) { NextPawn(); Handled(); return; }
        }

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && mb.Pressed)
            {
                // with several interactions in reach, the wheel chooses; otherwise it zooms (Ctrl+wheel always zooms)
                if (controlled != null && _nearby.Count > 1 && !mb.CtrlPressed && !_aiming)
                {
                    _interactionIndex = (_interactionIndex + (mb.ButtonIndex == MouseButton.WheelDown ? 1 : _nearby.Count - 1)) % _nearby.Count;
                }
                else _cam.Zoom(mb.ButtonIndex == MouseButton.WheelUp ? 0.88f : 1.13f);
                Handled();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed) { _rmbDown = true; _rmbTime = Time.GetTicksMsec() / 1000.0; _rmbPos = mb.Position; }
                else
                {
                    bool wasAiming = _aiming;
                    _rmbDown = false; _aiming = false;
                    if (!wasAiming) RightClick(mb.Position);
                }
                Handled();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                if (controlled != null && _aiming) { Sim.Input.Fire = true; Log.Action("fire"); }
                else LeftClick(mb.Position);
                Handled();
                return;
            }
        }
        if (e is InputEventMouseMotion mm)
        {
            if ((mm.ButtonMask & MouseButtonMask.Middle) != 0)
            {
                if (mm.CtrlPressed) _cam.Rotate(mm.Relative.X * 0.008f);
                else _cam.Pan(-mm.Relative * (_cam.Distance / 700f));
                Handled();
            }
        }
    }

    int _lastSpeed = 1;

    void Handled() => GetViewport().SetInputAsHandled();

    void NextPawn()
    {
        if (Sim.Pawns.Count == 0) return;
        int i = SelectedPawn == null ? 0 : (Sim.Pawns.IndexOf(SelectedPawn) + 1) % Sim.Pawns.Count;
        Select(Sim.Pawns[i]);
        FocusOn(Sim.Pawns[i]);
    }

    void Interact(Pawn controlled)
    {
        if (controlled != null)
        {
            if (controlled.Job != null || _nearby.Count == 0) return;
            Interactions.Execute(Sim, controlled, _nearby[_interactionIndex]);
            return;
        }
        // not in direct control: E on a door under the cursor sends the selected colonist to open/close it
        if (SelectedPawn == null || SelectedPawn.Dead) return;
        int cell = CursorCell();
        if (cell >= 0 && Sim.Map.Buildings[cell] == Building.Door)
        {
            var opts = Interactions.ForCell(Sim, SelectedPawn, cell);
            var door = opts.Find(o => o.Kind is InteractionKind.OpenDoor or InteractionKind.CloseDoor);
            if (door != null) Interactions.Execute(Sim, SelectedPawn, door);
        }
    }

    int CursorCell()
    {
        var gp = _cam.GroundPoint(MousePos);
        return gp.HasValue ? Sim.Map.CellAt(new SV2(gp.Value.X, gp.Value.Z)) : -1;
    }

    void LeftClick(Vector2 screen)
    {
        var gp = _cam.GroundPoint(screen);
        if (!gp.HasValue) return;
        var p = new SV2(gp.Value.X, gp.Value.Z);
        Pawn best = null; float bestD = 1.1f;
        foreach (var pawn in Sim.Pawns)
        {
            float d = SV2.Distance(pawn.Position, p);
            if (d < bestD) { bestD = d; best = pawn; }
        }
        if (best != null) Select(best);
        else if (Sim.Controlled == null) Select(null);
    }

    void RightClick(Vector2 screen)
    {
        var pawn = Sim.Controlled ?? SelectedPawn;
        if (pawn == null || pawn.Dead) return;
        var gp = _cam.GroundPoint(screen);
        if (!gp.HasValue) return;
        int cell = Sim.Map.CellAt(new SV2(gp.Value.X, gp.Value.Z));
        if (cell < 0) return;
        var opts = Interactions.ForCell(Sim, pawn, cell);
        if (pawn.Mode == ControlMode.Direct) opts.RemoveAll(o => o.Kind == InteractionKind.GoTo);
        if (opts.Count == 0) return;
        var menu = new PopupMenu();
        for (int i = 0; i < opts.Count; i++)
        {
            menu.AddItem(opts[i].Disabled ? $"{opts[i].Label} ({opts[i].DisabledReason})" : opts[i].Label, i);
            menu.SetItemDisabled(i, opts[i].Disabled);
        }
        menu.IdPressed += id => Interactions.Execute(Sim, pawn, opts[(int)id]);
        menu.PopupHide += () => menu.QueueFree();
        Hud.AddChild(menu);
        menu.Position = (Vector2I)screen;
        menu.Popup();
        Log.Action($"right-click menu at cell {cell}: {string.Join(" | ", opts.ConvertAll(o => o.Label))}");
    }

    // ------------------------------------------------------------------ HUD overlays

    void UpdateHudOverlays(Pawn controlled)
    {
        var mouse = MousePos;
        if (controlled != null && _aiming)
        {
            var aimAt = new SV2(Sim.Input.AimPoint.X, Sim.Input.AimPoint.Y);
            float dist = SV2.Distance(controlled.Position, aimAt);
            float range = GameSim.AttackRange(controlled);
            string weapon = controlled.HasRangedWeapon && controlled.Arrows > 0 ? $"{controlled.Arrows} arrows" : "melee";
            Hud.ShowAim(true, mouse, $"{dist:F1} m / {range:F0} m  ·  {weapon}{(dist > range ? "  ·  out of range" : "")}");
        }
        else Hud.ShowAim(false, mouse, null);

        int cell = CursorCell();
        if (cell != _lastCursorCell)
        {
            _lastCursorCell = cell;
            Hud.ShowHover(cell >= 0 ? DescribeCell(cell) : "", mouse);
        }
        else Hud.ShowHover(null, mouse);
    }

    string DescribeCell(int cell)
    {
        var m = Sim.Map;
        var parts = new List<string>();
        foreach (var a in Sim.Animals) if (!a.Dead && SV2.Distance(a.Position, m.CellCenter(cell)) < 1f) parts.Add(a.Label);
        foreach (var it in Sim.ItemsAt(cell)) parts.Add(it.Label);
        if (m.Plants[cell] == Plant.Oak) parts.Add("Oak tree");
        if (m.Plants[cell] == Plant.BerryBush) parts.Add($"Berry bush ({m.Berries[cell]} berries)");
        if (m.Buildings[cell] != Building.None) parts.Add(BuildingInfo.Label(m.Buildings[cell]) + (m.Buildings[cell] == Building.Door ? (m.DoorOpen[cell] ? " (open)" : " (closed)") : ""));
        parts.Add(m.TerrainAt(cell).Label);
        return string.Join("\n", parts);
    }

    // ------------------------------------------------------------------ automation hooks (AutoPilot)

    public CameraRig Camera => _cam;
    public EntityRenderer Entities => _entities;
    public List<Interaction> NearbyInteractions => _nearby;
    public void SetAiming(bool on) { _aiming = on; Sim.Input.Aim = on; }
}
