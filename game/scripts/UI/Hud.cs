using System;
using System.Collections.Generic;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Game.Planet3D;
using Remade.Pawns;
using Remade.Sim;

namespace Remade.Game.UI;

/// <summary>In-game interface: colonist bar, time/weather panel with speed controls, messages, the direct-control
/// interaction prompt and aim readout, hover info, buttons for the planet view and the pause menu.</summary>
public partial class Hud : CanvasLayer
{
    readonly GameSim _sim;
    readonly GameView _view;
    public readonly InspectPanel Inspect;
    readonly HBoxContainer _colonists;
    readonly Label _clock, _date, _temp, _weather, _fps;
    readonly HBoxContainer _speeds;
    readonly VBoxContainer _messages;
    readonly PanelContainer _prompt;
    readonly VBoxContainer _promptList;
    readonly Label _aimInfo, _readout;
    readonly Crosshair _crosshair;
    double _slow;
    public Control PauseMenu { get; private set; }
    public WorldOverlay World { get; private set; }

    public Hud(GameSim sim, GameView view)
    {
        _sim = sim;
        _view = view;
        Layer = 10;
        var root = UiKit.Fill(new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
        AddChild(root);

        // colonist bar (top centre)
        _colonists = UiKit.HBox(6);
        _colonists.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _colonists.Position = new Vector2(-200, 10);
        root.AddChild(_colonists);

        // top right: planet + menu
        var tr = UiKit.HBox(6);
        tr.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        tr.Position = new Vector2(-330, 10);
        tr.AddChild(UiKit.Button($"Planet [{Settings.KeyName("world")}]", () => ToggleWorld(), 15, 150));
        tr.AddChild(UiKit.Button("Menu [Esc]", () => TogglePause(), 15, 150));
        root.AddChild(tr);

        // time panel (bottom right)
        var tp = UiKit.Panel(null);
        tp.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        tp.CustomMinimumSize = new Vector2(318, 206);
        // pinned by its bottom-right corner: a long weather line widens the panel to the left, never off screen
        tp.GrowHorizontal = Control.GrowDirection.Begin;
        tp.GrowVertical = Control.GrowDirection.Begin;
        tp.OffsetRight = -12; tp.OffsetBottom = -12;
        tp.OffsetLeft = -12 - 318; tp.OffsetTop = -12 - 206;
        var tv = UiKit.VBox(2);
        tp.AddChild(tv);
        _clock = UiKit.Label("", 32, UiKit.Text, bold: true, align: HorizontalAlignment.Right);
        _date = UiKit.Label("", 15, UiKit.Muted, align: HorizontalAlignment.Right);
        _temp = UiKit.Label("", 15, UiKit.Accent, align: HorizontalAlignment.Right);
        _weather = UiKit.Label("", 14, UiKit.Muted, align: HorizontalAlignment.Right);
        tv.AddChild(_clock);
        tv.AddChild(new DayBar(_sim) { CustomMinimumSize = new Vector2(0, 26), Name = "DayBar", TooltipText = "Time of day: night, dawn, day and dusk for this latitude and season." });
        tv.AddChild(_date); tv.AddChild(_temp); tv.AddChild(_weather);
        _speeds = UiKit.HBox(4);
        _speeds.Alignment = BoxContainer.AlignmentMode.End;
        string[] glyphs = { "II", "▶", "▶▶", "▶▶▶", "⚡" };
        for (int i = 0; i < glyphs.Length; i++)
        {
            int s = i;
            var b = UiKit.Button(glyphs[i], () => SetSpeed(s), 15, 46);
            b.TooltipText = GameTime.SpeedNames[i] + (i == 0 ? $" [{Settings.KeyName("pause")}]" : $" ({GameTime.SpeedMultipliers[i]}x) [{i}]");
            _speeds.AddChild(b);
        }
        tv.AddChild(_speeds);
        root.AddChild(tp);
        // above the time panel (RimWorld's corner): what is under the cursor, then the play settings row, then fps
        var corner = UiKit.VBox(4);
        corner.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        corner.GrowHorizontal = Control.GrowDirection.Begin;
        corner.GrowVertical = Control.GrowDirection.Begin;
        corner.OffsetRight = -12; corner.OffsetBottom = -12 - 206 - 6;
        corner.OffsetLeft = -12 - 318; corner.OffsetTop = corner.OffsetBottom - 10;
        corner.MouseFilter = Control.MouseFilterEnum.Ignore;
        _readout = UiKit.Label("", 14, UiKit.Text, align: HorizontalAlignment.Right);
        _readout.Name = "CursorReadout";
        _readout.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _readout.AddThemeConstantOverride("outline_size", 5);
        corner.AddChild(_readout);
        var play = UiKit.HBox(4);
        play.Alignment = BoxContainer.AlignmentMode.End;
        play.Name = "PlaySettings";
        play.AddChild(new GridToggle { Name = "GridToggle" });
        corner.AddChild(play);
        _fps = UiKit.Label("", 12, new Color(UiKit.Muted, 0.7f), align: HorizontalAlignment.Right);
        corner.AddChild(_fps);
        root.AddChild(corner);

        // messages (top left)
        _messages = UiKit.VBox(4);
        _messages.Position = new Vector2(14, 84); // under the colonist bar
        root.AddChild(_messages);

        // direct-control prompt (bottom centre)
        _prompt = UiKit.Panel(null, null, 10);
        _prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _prompt.Position = new Vector2(-170, -200);
        _prompt.CustomMinimumSize = new Vector2(340, 0);
        _promptList = UiKit.VBox(3);
        _prompt.AddChild(_promptList);
        _prompt.Visible = false;
        root.AddChild(_prompt);

        _aimInfo = UiKit.Label("", 15, new Color(1f, 0.5f, 0.45f), bold: true);
        _aimInfo.Visible = false;
        root.AddChild(_aimInfo);

        _crosshair = new Crosshair { Name = "Crosshair", Visible = false };
        _crosshair.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_crosshair);

        Inspect = new InspectPanel(sim);
        root.AddChild(Inspect);
        RebuildColonists();
    }

    // ------------------------------------------------------------------ colonist bar

    public void RebuildColonists()
    {
        foreach (var c in _colonists.GetChildren()) c.QueueFree();
        foreach (var p in _sim.Pawns)
        {
            var pawn = p;
            var card = UiKit.Panel(null, null, 6);
            card.CustomMinimumSize = new Vector2(124, 0);
            card.MouseFilter = Control.MouseFilterEnum.Stop;
            var v = UiKit.VBox(2);
            card.AddChild(v);
            var top = UiKit.HBox(4);
            top.Name = "Top";
            top.AddChild(UiKit.Label(p.Name, 15, UiKit.Text, bold: true));
            top.AddChild(UiKit.Expander());
            var star = UiKit.Label("★", 17, new Color(1f, 0.84f, 0.2f), bold: true);
            star.Name = "Star";
            star.TooltipText = "Under your direct control";
            star.Visible = false;
            top.AddChild(star);
            v.AddChild(top);
            var mode = UiKit.Label("", 12, UiKit.Muted);
            mode.Name = "Mode";
            v.AddChild(mode);
            foreach (var (key, col) in new[] { ("F", UiKit.Warn), ("T", new Color(0.35f, 0.65f, 0.95f)), ("S", new Color(0.55f, 0.55f, 0.95f)) })
            {
                var bar = UiKit.Bar(1, col, 4);
                bar.Name = "Need" + key;
                v.AddChild(bar);
            }
            card.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
                {
                    _view.Select(pawn);
                    if (mb.DoubleClick) _view.FocusOn(pawn);
                }
            };
            card.SetMeta("pawn", p.Id);
            _colonists.AddChild(card);
        }
        _colonists.Position = new Vector2(-(_sim.Pawns.Count * 130) / 2f, 10);
    }

    void RefreshColonists()
    {
        int i = 0;
        foreach (var c in _colonists.GetChildren())
        {
            if (c is not PanelContainer card || i >= _sim.Pawns.Count) continue;
            var p = _sim.Pawns[i++];
            var v = card.GetChild(0);
            ((Label)v.GetNode("Mode")).Text = p.Dead ? "dead" : p.Mode switch { ControlMode.Direct => "controlled", ControlMode.Drafted => "drafted", _ => p.LastJobLabel };
            ((Label)v.GetNode("Top/Star")).Visible = !p.Dead && p.Mode == ControlMode.Direct;
            ((ProgressBar)v.GetNode("NeedF")).Value = p.Needs.Food;
            ((ProgressBar)v.GetNode("NeedT")).Value = p.Needs.Thirst;
            ((ProgressBar)v.GetNode("NeedS")).Value = p.Needs.Rest;
            bool sel = _view.SelectedPawn == p;
            card.AddThemeStyleboxOverride("panel", UiKit.PanelStyle(sel ? new Color(0.08f, 0.18f, 0.18f, 0.95f) : UiKit.Ink, sel ? UiKit.Accent : UiKit.Line, 4, 6));
        }
    }

    // ------------------------------------------------------------------ time

    public void SetSpeed(int s)
    {
        _sim.SpeedIndex = s;
        Log.Action($"speed {GameTime.SpeedNames[s]}");
    }

    public void Message(string text, Color? color = null)
    {
        var l = UiKit.Label(text, 15, color ?? UiKit.Text);
        var p = UiKit.Panel(l, new Color(0.03f, 0.05f, 0.07f, 0.8f), 6);
        _messages.AddChild(p);
        if (_messages.GetChildCount() > 6) _messages.GetChild(0).QueueFree();
        var tw = p.CreateTween();
        tw.TweenInterval(6.0);
        tw.TweenProperty(p, "modulate:a", 0f, 1.5);
        tw.TweenCallback(Callable.From(p.QueueFree));
    }

    public override void _Process(double delta)
    {
        var w = _sim.Weather;
        _clock.Text = GameTime.ClockString(_sim.Tick);
        _date.Text = GameTime.DateString(_sim.Tick, _sim.Latitude);
        _temp.Text = $"Outdoors {w.Temperature:F0} °C  ·  {_sim.ColonyName}";
        _weather.Text = $"{w.Describe()}  ·  wind {w.WindSpeed:F0} m/s" + (w.SnowCover > 0.05f ? "  ·  snow on the ground" : w.Wetness > 0.3f ? "  ·  wet ground" : "");
        for (int i = 0; i < _speeds.GetChildCount(); i++)
            ((Button)_speeds.GetChild(i)).AddThemeColorOverride("font_color", i == _sim.SpeedIndex ? UiKit.Accent : UiKit.Text);
        _slow -= delta;
        if (_slow <= 0)
        {
            _slow = 0.25;
            RefreshColonists();
            _fps.Text = $"{Engine.GetFramesPerSecond():F0} fps · sim {_sim.LastStepMs:F1} ms";
        }
    }

    // ------------------------------------------------------------------ direct control prompt & aim

    public void ShowInteractions(List<Interaction> list, int selected)
    {
        _prompt.Visible = list.Count > 0;
        foreach (var c in _promptList.GetChildren()) c.QueueFree();
        if (list.Count == 0) return;
        _promptList.AddChild(UiKit.Label(list.Count > 1 ? $"[{Settings.KeyName("interact")}] interact  ·  mouse wheel to choose" : $"[{Settings.KeyName("interact")}] interact", 13, UiKit.Muted));
        for (int i = 0; i < list.Count && i < 7; i++)
        {
            bool on = i == selected;
            _promptList.AddChild(UiKit.Label((on ? "▶ " : "   ") + list[i].Label, on ? 17 : 15, on ? UiKit.Accent : UiKit.Text, bold: on));
        }
    }

    public void ShowAim(bool on, Vector2 screen, string text)
    {
        _aimInfo.Visible = on;
        if (!on) return;
        _aimInfo.Text = text;
        _aimInfo.Position = screen + new Vector2(22, 10);
    }

    /// <summary>First-person reticle at the screen centre: a small cross, a ring with range ticks while aiming.</summary>
    public void SetCrosshair(bool visible, bool aiming)
    {
        _crosshair.Visible = visible;
        if (_crosshair.Aiming != aiming) { _crosshair.Aiming = aiming; _crosshair.QueueRedraw(); }
    }

    /// <summary>What lies under the cursor, shown at a fixed place (it only changes when the cursor moves to another cell).</summary>
    public void SetReadout(string text) => _readout.Text = text;
    public string Readout => _readout.Text;

    // ------------------------------------------------------------------ pause menu & planet view

    public void TogglePause()
    {
        if (World != null) { ToggleWorld(); return; }
        if (PauseMenu != null) { PauseMenu.QueueFree(); PauseMenu = null; return; }
        PauseMenu = new PauseMenu(_sim, () => { PauseMenu?.QueueFree(); PauseMenu = null; });
        AddChild(PauseMenu);
    }

    public void ToggleWorld()
    {
        if (World != null) { World.QueueFree(); World = null; _view.SetWorldVisible(true); return; }
        World = new WorldOverlay(_sim, () => ToggleWorld());
        AddChild(World);
        _view.SetWorldVisible(false);
    }
}

/// <summary>Esc menu: resume, save (named), load, options, main menu, quit.</summary>
public partial class PauseMenu : Control
{
    public PauseMenu(GameSim sim, Action close)
    {
        UiKit.Fill(this);
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(UiKit.Fill(new ColorRect { Color = new Color(0, 0, 0, 0.55f) }));
        var panel = UiKit.Panel(null, UiKit.InkSolid, 20);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.Position = new Vector2(-180, -250);
        panel.CustomMinimumSize = new Vector2(360, 0);
        AddChild(panel);
        var col = UiKit.VBox(10);
        panel.AddChild(col);
        col.AddChild(UiKit.Label("PAUSED", 26, UiKit.Text, bold: true, align: HorizontalAlignment.Center));
        var name = new LineEdit { Text = sim.ColonyName, PlaceholderText = "save name" };
        Label status = UiKit.Label("", 14, UiKit.Good);
        col.AddChild(UiKit.Button("Resume", close, 18));
        col.AddChild(name);
        col.AddChild(UiKit.Button("Save game", () =>
        {
            string path = SaveFiles.PathFor(name.Text);
            Remade.Save.SaveGame.Write(sim, path);
            status.Text = "Saved: " + System.IO.Path.GetFileName(path);
        }, 18));
        col.AddChild(status);
        col.AddChild(UiKit.Button("Load game", () => Main.I.ShowLoadMenu(), 18));
        col.AddChild(UiKit.Button("Options", () => Main.I.ShowOptions(this), 18));
        col.AddChild(UiKit.Button("Main menu", () => Main.I.ShowMainMenu(), 18));
        col.AddChild(UiKit.Button("Quit to desktop", () => Main.I.Quit(), 18));
    }
}

/// <summary>
/// The planet during play: the same globe and overlays as at the start, showing the live climate simulation;
/// the colony's tile is selected and its current conditions are listed (they drive the local map).
/// </summary>
public partial class WorldOverlay : Control
{
    readonly PlanetView _view;
    readonly VBoxContainer _info;
    readonly GameSim _sim;
    double _t;

    public WorldOverlay(GameSim sim, Action close)
    {
        _sim = sim;
        UiKit.Fill(this);
        MouseFilter = MouseFilterEnum.Stop;
        _view = UiKit.Fill(new PlanetView(1024));
        AddChild(_view);
        _view.SetPlanet(sim.Planet, () =>
        {
            _view.Select(sim.Map.PlanetTile);
            _view.FocusTile(sim.Map.PlanetTile, 2.2f);
        });
        _view.TileSelected += t => TileInfo.Fill(_info, sim.Planet, t, sim.Tick);
        var title = UiKit.Label("PLANET", 28, UiKit.Text, bold: true);
        title.Position = new Vector2(40, 28);
        AddChild(title);
        AddChild(new Label { Text = "The colony's region is highlighted. Temperatures and precipitation here are the live values that drive your map.", Position = new Vector2(42, 70) });
        var panel = UiKit.Panel(null);
        panel.Position = new Vector2(40, 110);
        panel.CustomMinimumSize = new Vector2(420, 0);
        _info = UiKit.VBox(5);
        panel.AddChild(_info);
        AddChild(panel);
        var overlays = new OverlayBar(_view);
        overlays.SetAnchorsPreset(LayoutPreset.TopRight);
        overlays.Position = new Vector2(-560, 30);
        AddChild(overlays);
        AddChild(new PlanetHoverTip(_view));
        var back = UiKit.Button($"Back to the colony [{Settings.KeyName("world")}]", close, 18, 300);
        back.SetAnchorsPreset(LayoutPreset.BottomRight);
        back.Position = new Vector2(-340, -80);
        AddChild(back);
        TileInfo.Fill(_info, sim.Planet, sim.Map.PlanetTile, sim.Tick);
    }

    public override void _Process(double delta)
    {
        _t -= delta;
        if (_t > 0 || _view.SelectedTile < 0) return;
        _t = 1.0;
        TileInfo.Fill(_info, _sim.Planet, _view.SelectedTile, _sim.Tick);
    }

    public PlanetView View => _view;
}

/// <summary>
/// The day as a strip (from the first prototype): night blue, dawn and dusk orange, day sky blue, computed from the
/// real sun height for the colony's latitude and season (long summer days, short winter days), ticks every six
/// hours, and a sun or moon marker at the current hour.
/// </summary>
public partial class DayBar : Control
{
    readonly GameSim _sim;
    const int Slices = 48;
    readonly float[] _elev = new float[Slices + 1];
    long _day = -1;

    public DayBar(GameSim sim) { _sim = sim; MouseFilter = MouseFilterEnum.Pass; }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        long day = _sim.Tick / GameTime.TicksPerDay;
        if (day != _day)
        {
            _day = day;
            for (int i = 0; i <= Slices; i++)
                _elev[i] = Remade.Game.Render.Lighting.SolarDirection(_sim.Latitude, day * GameTime.TicksPerDay + (long)((i + 0.5f) / Slices * GameTime.TicksPerDay)).Y;
        }
        float w = Size.X, h = Size.Y - 8;
        var night = new Color(0.08f, 0.1f, 0.22f);
        var dayC = new Color(0.45f, 0.7f, 0.95f);
        var dawn = new Color(0.98f, 0.55f, 0.3f);
        for (int i = 0; i < Slices; i++)
        {
            float sun = _elev[i];
            var c = night.Lerp(dayC, Mathf.SmoothStep(-0.1f, 0.3f, sun));
            c = c.Lerp(dawn, Mathf.Max(0f, 1f - Mathf.Abs(sun) / 0.16f) * 0.75f);
            DrawRect(new Rect2(w * i / Slices, 4, w / Slices + 1, h), c);
        }
        DrawRect(new Rect2(0, 4, w, h), new Color(1, 1, 1, 0.25f), false, 1f);
        for (int k = 0; k <= 24; k += 6)
            DrawLine(new Vector2(w * k / 24f, 4 + h - 4), new Vector2(w * k / 24f, 4 + h), new Color(1, 1, 1, 0.5f), 1f);
        float hour = GameTime.HourOfDay(_sim.Tick);
        float x = hour / 24f * w;
        bool isDay = Remade.Game.Render.Lighting.SolarDirection(_sim.Latitude, _sim.Tick).Y > -0.02f;
        DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), Colors.White, 2f);
        var mc = new Vector2(x, 4 + h / 2);
        if (isDay) DrawCircle(mc, 7, new Color(1f, 0.85f, 0.35f));
        else { DrawCircle(mc, 7, new Color(0.9f, 0.92f, 1f)); DrawCircle(mc + new Vector2(3, -2), 6, night); }
        DrawCircle(mc, 7.5f, new Color(0, 0, 0, 0.5f), false, 1.5f);
    }
}

/// <summary>
/// Play-settings button (RimWorld's bottom-right row): the cell grid, cycling Off → Always → Around the cursor.
/// Drawn as a small grid glyph; the state is shown by colour and a dot.
/// </summary>
public partial class GridToggle : Button
{
    public GridToggle()
    {
        CustomMinimumSize = new Vector2(34, 30);
        FocusMode = FocusModeEnum.None;
        Pressed += () =>
        {
            Settings.GridMode = (Settings.GridMode + 1) % 3;
            Settings.Save();
            Remade.Diagnostics.Log.Action($"grid mode {Settings.GridMode}");
            UpdateTip();
            QueueRedraw();
        };
        UpdateTip();
    }

    void UpdateTip() => TooltipText = "Grid: " + Settings.GridMode switch { 0 => "off", 1 => "always shown", _ => "shown around the cursor" } + "\n(click to change)";

    public override void _Draw()
    {
        var c = Settings.GridMode == 0 ? new Color(UiKit.Muted, 0.6f) : UiKit.Accent;
        var r = new Rect2(8, 6, 18, 18);
        for (int k = 0; k <= 3; k++)
        {
            DrawLine(new Vector2(r.Position.X + k * 6, r.Position.Y), new Vector2(r.Position.X + k * 6, r.End.Y), c, 1.5f);
            DrawLine(new Vector2(r.Position.X, r.Position.Y + k * 6), new Vector2(r.End.X, r.Position.Y + k * 6), c, 1.5f);
        }
        if (Settings.GridMode == 2) DrawCircle(new Vector2(r.End.X + 2, r.End.Y + 2), 3.5f, new Color(1f, 0.84f, 0.2f));
    }
}

/// <summary>The first-person reticle (drawn at the centre of the screen).</summary>
public partial class Crosshair : Control
{
    public bool Aiming;
    public Crosshair() { MouseFilter = MouseFilterEnum.Ignore; }

    public override void _Draw()
    {
        var c = Size * 0.5f;
        var col = Aiming ? new Color(1f, 0.35f, 0.3f, 0.95f) : new Color(1f, 1f, 1f, 0.8f);
        float gap = Aiming ? 5 : 3, len = Aiming ? 12 : 7;
        foreach (var d in new[] { Vector2.Up, Vector2.Down, Vector2.Left, Vector2.Right })
        {
            DrawLine(c + d * gap + new Vector2(1, 1), c + d * (gap + len) + new Vector2(1, 1), new Color(0, 0, 0, 0.6f), 2.5f);
            DrawLine(c + d * gap, c + d * (gap + len), col, 2f);
        }
        if (Aiming) DrawArc(c, 22, 0, Mathf.Tau, 48, new Color(col, 0.6f), 1.5f, true);
        DrawCircle(c, 1.5f, col);
    }
}
