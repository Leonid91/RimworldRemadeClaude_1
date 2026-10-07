using System;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Game.Planet3D;
using Remade.World;
using WorldPlanet = Remade.World.Planet;

namespace Remade.Game.UI;

/// <summary>Shared tile description used by the landing-site screen and the in-game planet view.</summary>
public static class TileInfo
{
    public static void Fill(VBoxContainer box, WorldPlanet p, int t, long tick)
    {
        foreach (var c in box.GetChildren()) c.QueueFree();
        var biome = p.Biomes[t];
        box.AddChild(UiKit.Label(BiomeInfo.Label(biome), 26, UiKit.Text, bold: true));
        var d = UiKit.Label(BiomeInfo.Description(biome), 15, UiKit.Muted);
        d.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(d);
        box.AddChild(UiKit.Spacer(0, 6));
        if (biome == Biome.Ocean)
        {
            Row(box, "Depth", $"{-p.Elevation[t]:F0} m");
            Row(box, "Water temperature", UiKit.Temp(p.Climate.Temperature[t]));
            return;
        }
        var sample = p.Climate.Sample(t);
        float lat = p.Grid.Latitude(t), lon = p.Grid.Longitude(t);
        float summer = p.Climate.SeasonalTemperature(t, lat >= 0 ? 0.375f : 0.875f);
        float winter = p.Climate.SeasonalTemperature(t, lat >= 0 ? 0.875f : 0.375f);
        Row(box, "Terrain", BiomeInfo.HillLabel(p.Hills[t]));
        Row(box, "Elevation", $"{p.Elevation[t]:F0} m");
        Row(box, "Temperature now", $"{UiKit.Temp(sample.Temperature)}  ({GameTime.SeasonName(GameTime.SeasonAt(tick, lat)).ToLowerInvariant()})");
        Row(box, "Seasons", $"winter {UiKit.Temp(winter)} · summer {UiKit.Temp(summer)}");
        Row(box, "Annual mean", UiKit.Temp(p.MeanTemp[t]));
        Row(box, "Rainfall", $"{p.AnnualPrecip[t]:F0} mm / year");
        Row(box, "Precipitation now", $"{sample.Precipitation:F1} mm / day");
        Row(box, "Soil moisture", $"{sample.SoilMoisture * 100:F0} %");
        Row(box, "Wind", $"{sample.WindSpeed:F1} m/s");
        Row(box, "River", BiomeInfo.RiverLabel(p.RiverSize[t]));
        Row(box, "Coast", p.Coast[t] ? "Yes" : "No");
        Row(box, "Location", $"{MathF.Abs(lat):F1}°{(lat >= 0 ? "N" : "S")}, {MathF.Abs(lon):F1}°{(lon >= 0 ? "E" : "W")}");
        box.AddChild(UiKit.Spacer(0, 8));
        bool playable = BiomeInfo.Playable(biome) && p.Hills[t] != Hilliness.Impassable;
        box.AddChild(UiKit.Label(playable ? "You can settle here." : biome == Biome.TemperateForest ? "Too mountainous to land." : "Not playable yet — only temperate forests can be settled in this version.",
            15, playable ? UiKit.Good : UiKit.Warn));
    }

    static void Row(VBoxContainer box, string k, string v)
    {
        var r = UiKit.HBox(8);
        var kl = UiKit.Label(k, 15, UiKit.Muted);
        kl.CustomMinimumSize = new Vector2(150, 0);
        r.AddChild(kl);
        r.AddChild(UiKit.Label(v, 15));
        box.AddChild(r);
    }
}

/// <summary>Overlay toggles (biomes, temperature, elevation, precipitation) with a colour legend.</summary>
public partial class OverlayBar : VBoxContainer
{
    readonly PlanetView _view;
    readonly HBoxContainer _buttons;
    readonly Control _legend;
    readonly Label _legendTitle, _legendMin, _legendMax;
    readonly TextureRect _ramp;

    public OverlayBar(PlanetView view)
    {
        _view = view;
        AddThemeConstantOverride("separation", 6);
        _buttons = UiKit.HBox(6);
        AddChild(_buttons);
        foreach (var (label, kind) in new (string, PlanetBaker.OverlayKind)[]
                 {
                     ("Biomes", PlanetBaker.OverlayKind.None), ("Temperature", PlanetBaker.OverlayKind.Temperature),
                     ("Elevation", PlanetBaker.OverlayKind.Elevation), ("Precipitation", PlanetBaker.OverlayKind.Precipitation),
                 })
        {
            var k = kind;
            var b = UiKit.Button(label, () => { _view.SetOverlay(k); Refresh(); }, 15);
            b.Name = "Overlay" + label;
            _buttons.AddChild(b);
        }
        var lp = UiKit.Panel(null, null, 8);
        var lv = UiKit.VBox(3);
        lp.AddChild(lv);
        _legendTitle = UiKit.Label("", 14, UiKit.Muted);
        lv.AddChild(_legendTitle);
        _ramp = new TextureRect { CustomMinimumSize = new Vector2(360, 12), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize };
        lv.AddChild(_ramp);
        var ends = UiKit.HBox(0);
        _legendMin = UiKit.Label("", 13, UiKit.Muted);
        _legendMax = UiKit.Label("", 13, UiKit.Muted, align: HorizontalAlignment.Right);
        _legendMax.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ends.AddChild(_legendMin); ends.AddChild(_legendMax);
        lv.AddChild(ends);
        _legend = lp;
        AddChild(lp);
        Refresh();
    }

    PlanetBaker.OverlayKind _shown = (PlanetBaker.OverlayKind)(-1);

    public override void _Process(double delta)
    {
        if (_view.Overlay != _shown) Refresh();
    }

    void Refresh()
    {
        var k = _view.Overlay;
        _shown = k;
        int idx = (int)k;
        for (int i = 0; i < _buttons.GetChildCount(); i++)
        {
            var b = (Button)_buttons.GetChild(i);
            bool on = i == idx;
            b.AddThemeColorOverride("font_color", on ? UiKit.Accent : UiKit.Text);
        }
        _legend.Visible = k != PlanetBaker.OverlayKind.None;
        if (!_legend.Visible) return;
        Color[] stops = k switch
        {
            PlanetBaker.OverlayKind.Temperature => new[] { C(0.30f, 0.10f, 0.55f), C(0.15f, 0.35f, 0.85f), C(0.55f, 0.85f, 0.95f), C(0.45f, 0.80f, 0.35f), C(0.98f, 0.85f, 0.25f), C(0.95f, 0.45f, 0.15f), C(0.75f, 0.10f, 0.10f) },
            PlanetBaker.OverlayKind.Precipitation => new[] { C(0.78f, 0.66f, 0.45f), C(0.70f, 0.78f, 0.40f), C(0.25f, 0.65f, 0.40f), C(0.15f, 0.50f, 0.80f), C(0.25f, 0.20f, 0.70f) },
            _ => new[] { C(0.03f, 0.08f, 0.30f), C(0.35f, 0.65f, 0.85f), C(0.20f, 0.55f, 0.25f), C(0.75f, 0.75f, 0.35f), C(0.55f, 0.35f, 0.20f), C(0.95f, 0.95f, 0.97f) },
        };
        var g = new Gradient();
        var offs = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++) offs[i] = i / (float)(stops.Length - 1);
        g.Colors = stops; g.Offsets = offs;
        _ramp.Texture = new GradientTexture1D { Gradient = g, Width = 256 };
        (_legendTitle.Text, _legendMin.Text, _legendMax.Text) = k switch
        {
            PlanetBaker.OverlayKind.Temperature => ("Natural temperature now (daily mean)", "-45 °C", "+45 °C"),
            PlanetBaker.OverlayKind.Precipitation => ("Precipitation now", "dry", "30+ mm/day"),
            _ => ("Elevation", "-6500 m", "+6500 m"),
        };
    }

    static Color C(float r, float g, float b) => new(r, g, b);
}

/// <summary>Landing-site selection on the rotating globe, then map size and Play.</summary>
public partial class PlanetScreen : Control
{
    readonly PlanetView _view;
    readonly PanelContainer _info;
    readonly VBoxContainer _infoBox;
    readonly Button _next;
    readonly PanelContainer _sizePanel;
    Button _play;
    Label _hoverLabel;
    readonly PanelContainer _hoverTip;

    public static readonly (int size, string name, string note)[] MapSizes =
    {
        (300, "Standard", "300 × 300 — slightly larger than RimWorld's largest"),
        (450, "Large", "450 × 450 — about twice the area"),
        (600, "Huge", "600 × 600 — 3.4× RimWorld's largest"),
        (800, "Vast", "800 × 800"),
        (1000, "Colossal", "1000 × 1000 — nearly ten times RimWorld's largest"),
        (1500, "Titanic", "1500 × 1500 — for powerful machines"),
    };

    public PlanetScreen()
    {
        UiKit.Fill(this);
        var setup = Main.I.Setup;
        _view = UiKit.Fill(new PlanetView(768));
        AddChild(_view);
        _view.SetPlanet(setup.Planet, () =>
        {
            int start = setup.Planet.FindStartTile();
            if (start >= 0) _view.FocusTile(start, 2.9f);
        });
        _view.TileSelected += OnSelected;
        _view.TileHovered += OnHovered;

        var title = UiKit.Label("CHOOSE A LANDING SITE", 30, UiKit.Text, bold: true);
        title.Position = new Vector2(40, 28);
        AddChild(title);
        var help = UiKit.Label("Drag to turn the planet · wheel to zoom · hover to see the hexagonal regions · click to select", 15, UiKit.Muted);
        help.Position = new Vector2(42, 72);
        AddChild(help);

        _info = UiKit.Panel(null);
        _info.Position = new Vector2(40, 120);
        _info.CustomMinimumSize = new Vector2(420, 0);
        _info.Visible = false;
        _infoBox = UiKit.VBox(5);
        _info.AddChild(_infoBox);
        AddChild(_info);

        var overlays = new OverlayBar(_view);
        overlays.SetAnchorsPreset(LayoutPreset.TopRight);
        overlays.Position = new Vector2(-560, 30);
        AddChild(overlays);

        _hoverTip = UiKit.Panel(null, null, 6);
        _hoverLabel = UiKit.Label("", 14);
        _hoverTip.AddChild(_hoverLabel);
        _hoverTip.Visible = false;
        _hoverTip.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_hoverTip);

        var back = UiKit.Button("Back", () => Main.I.ShowWorldGen(), 18, 160);
        back.SetAnchorsPreset(LayoutPreset.BottomLeft);
        back.Position = new Vector2(40, -80);
        AddChild(back);
        _next = UiKit.Button("Next", ShowSizes, 20, 220);
        _next.SetAnchorsPreset(LayoutPreset.BottomRight);
        _next.Position = new Vector2(-260, -80);
        _next.Disabled = true;
        AddChild(_next);

        _sizePanel = UiKit.Panel(null);
        _sizePanel.SetAnchorsPreset(LayoutPreset.BottomRight);
        _sizePanel.Position = new Vector2(-660, -600);
        _sizePanel.CustomMinimumSize = new Vector2(620, 0);
        _sizePanel.Visible = false;
        AddChild(_sizePanel);
    }

    void OnHovered(int t)
    {
        if (t < 0) { _hoverTip.Visible = false; return; }
        var p = Main.I.Setup.Planet;
        _hoverLabel.Text = $"{BiomeInfo.Label(p.Biomes[t])}  ·  {UiKit.Temp(p.Climate.Temperature[t])}" + (p.RiverSize[t] > 0 ? "  ·  river" : "");
        _hoverTip.Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_hoverTip.Visible) _hoverTip.Position = GetLocalMousePosition() + new Vector2(18, 18);
    }

    void OnSelected(int t)
    {
        var setup = Main.I.Setup;
        setup.Tile = t;
        _info.Visible = true;
        TileInfo.Fill(_infoBox, setup.Planet, t, 0);
        bool playable = BiomeInfo.Playable(setup.Planet.Biomes[t]) && setup.Planet.Hills[t] != Hilliness.Impassable;
        _next.Disabled = false;
        if (_play != null) _play.Disabled = !playable;
    }

    void ShowSizes()
    {
        var setup = Main.I.Setup;
        _next.Visible = false;
        _sizePanel.Visible = true;
        foreach (var c in _sizePanel.GetChildren()) c.QueueFree();
        var col = UiKit.VBox(8);
        _sizePanel.AddChild(col);
        col.AddChild(UiKit.Heading("Map size"));
        var group = new ButtonGroup();
        foreach (var (size, name, note) in MapSizes)
        {
            var b = new CheckBox { Text = $"{name}   {note}", ButtonGroup = group, ButtonPressed = size == setup.MapSize, FocusMode = FocusModeEnum.None };
            b.AddThemeFontSizeOverride("font_size", 16);
            b.AddThemeColorOverride("font_pressed_color", UiKit.Accent);
            b.AddThemeColorOverride("font_hover_pressed_color", UiKit.Accent);
            int s = size;
            b.Toggled += on => { if (on) { setup.MapSize = s; Log.Action($"map size {s}"); } };
            col.AddChild(b);
        }
        col.AddChild(UiKit.Spacer(0, 6));
        col.AddChild(UiKit.Heading("Colony name"));
        var nameEdit = new LineEdit { Text = setup.ColonyName, MaxLength = 28 };
        nameEdit.TextChanged += t => { if (!string.IsNullOrWhiteSpace(t)) setup.ColonyName = t.Trim(); };
        col.AddChild(nameEdit);
        col.AddChild(UiKit.Spacer(0, 6));
        var row = UiKit.HBox(8);
        row.AddChild(UiKit.Button("Back", () => { _sizePanel.Visible = false; _next.Visible = true; }, 17, 120));
        row.AddChild(UiKit.Expander());
        _play = UiKit.Button("Play", () => Main.I.StartColony(), 20, 200);
        bool playable = setup.Tile >= 0 && BiomeInfo.Playable(setup.Planet.Biomes[setup.Tile]) && setup.Planet.Hills[setup.Tile] != Hilliness.Impassable;
        _play.Disabled = !playable;
        _play.TooltipText = playable ? "" : "Only temperate forests can be settled in this version.";
        row.AddChild(_play);
        col.AddChild(row);
    }

    /// <summary>Selects a tile programmatically (autopilot / tests).</summary>
    public void SelectTile(int t)
    {
        _view.FocusTile(t, 2.4f);
        _view.Select(t);
    }

    public PlanetView View => _view;
}
