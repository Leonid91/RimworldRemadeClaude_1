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
    readonly Label _aimInfo, _hoverInfo;
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
        tp.Position = new Vector2(-330, -186);
        tp.CustomMinimumSize = new Vector2(318, 174);
        var tv = UiKit.VBox(2);
        tp.AddChild(tv);
        _clock = UiKit.Label("", 32, UiKit.Text, bold: true, align: HorizontalAlignment.Right);
        _date = UiKit.Label("", 15, UiKit.Muted, align: HorizontalAlignment.Right);
        _temp = UiKit.Label("", 15, UiKit.Accent, align: HorizontalAlignment.Right);
        _weather = UiKit.Label("", 14, UiKit.Muted, align: HorizontalAlignment.Right);
        tv.AddChild(_clock); tv.AddChild(_date); tv.AddChild(_temp); tv.AddChild(_weather);
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
        _fps = UiKit.Label("", 12, new Color(UiKit.Muted, 0.7f), align: HorizontalAlignment.Right);
        _fps.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _fps.Position = new Vector2(-330, -206);
        _fps.CustomMinimumSize = new Vector2(318, 0);
        root.AddChild(_fps);

        // messages (top left)
        _messages = UiKit.VBox(4);
        _messages.Position = new Vector2(14, 14);
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
        _hoverInfo = UiKit.Label("", 14, UiKit.Text);
        _hoverInfo.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        _hoverInfo.AddThemeConstantOverride("outline_size", 5);
        root.AddChild(_hoverInfo);

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
            var name = UiKit.Label(p.Name, 15, UiKit.Text, bold: true);
            v.AddChild(name);
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
            ((Label)v.GetNode("Mode")).Text = p.Dead ? "dead" : p.Mode switch { ControlMode.Direct => "◆ controlled", ControlMode.Drafted => "drafted", _ => p.LastJobLabel };
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

    public void ShowHover(string text, Vector2 screen)
    {
        _hoverInfo.Text = text ?? "";
        _hoverInfo.Position = screen + new Vector2(18, -26);
    }

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
        _view = UiKit.Fill(new PlanetView(640));
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
