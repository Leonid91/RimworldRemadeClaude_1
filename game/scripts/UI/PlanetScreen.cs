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
        if (p.Water[t] != WaterBody.None)
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
        Row(box, "Water", WaterLabel(p, t), "Water on this region's map: the sea (coast), a lake shore, a river or creek crossing it,\nor an estuary — a large river widening into the sea.");
        Row(box, "Temperature now", $"{UiKit.Temp(sample.Temperature)}  ({GameTime.SeasonName(GameTime.SeasonAt(tick, lat)).ToLowerInvariant()})");
        Row(box, "Seasons", $"winter {UiKit.Temp(winter)} · summer {UiKit.Temp(summer)}");
        Row(box, "Annual mean", UiKit.Temp(p.MeanTemp[t]));
        Row(box, "Rainfall", $"{p.AnnualPrecip[t]:F0} mm / year");
        Row(box, "Precipitation now", $"{sample.Precipitation:F1} mm / day");
        Row(box, "Ground moisture", $"{sample.SoilMoisture * 100:F0} %",
            "How wet the ground is (rain soaks in, heat and wind dry it out). On the map it sets how lush the\n" +
            "vegetation is, how many ponds and marshy patches there are, how wet the ground looks, and\nmorning fog on calm days.");
        Row(box, "Wind", $"{p.TypicalWind(t):F0} m/s average  ·  {sample.WindSpeed:F0} m/s now",
            "Average: the prevailing wind of this latitude (trade winds, westerlies, polar easterlies), weaker on\n" +
            "high ground, plus passing weather systems. 'Now' changes hour by hour with the weather.\n" +
            "On the map wind sways trees and grass, slants rain and snow, clears fog and dries the ground.");
        Row(box, "Location", $"{MathF.Abs(lat):F1}°{(lat >= 0 ? "N" : "S")}, {MathF.Abs(lon):F1}°{(lon >= 0 ? "E" : "W")}");
        box.AddChild(UiKit.Spacer(0, 8));
        bool playable = BiomeInfo.Playable(biome) && p.Hills[t] != Hilliness.Impassable;
        box.AddChild(UiKit.Label(playable ? "You can settle here." : biome == Biome.TemperateForest ? "Too mountainous to land." : "Not playable yet — only temperate forests can be settled in this version.",
            15, playable ? UiKit.Good : UiKit.Warn));
    }

    /// <summary>The water features of a land tile: Estuary, River / Creek, Coast, Lake shore — or None.</summary>
    public static string WaterLabel(WorldPlanet p, int t)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (p.Estuary[t]) parts.Add("Estuary");
        else if (p.RiverSize[t] > 0) parts.Add(BiomeInfo.RiverLabel(p.RiverSize[t]));
        if (p.Coast[t] && !p.Estuary[t]) parts.Add("Coast");
        if (p.LakeShore[t]) parts.Add("Lake shore");
        return parts.Count == 0 ? "None" : string.Join(" · ", parts);
    }

    static void Row(VBoxContainer box, string k, string v, string tip = null)
    {
        var r = UiKit.HBox(8);
        var kl = UiKit.Label(k, 15, UiKit.Muted);
        kl.CustomMinimumSize = new Vector2(150, 0);
        r.AddChild(kl);
        r.AddChild(UiKit.Label(v, 15));
        if (tip != null)
        {
            r.TooltipText = tip;
            r.MouseFilter = Control.MouseFilterEnum.Stop;
            kl.Text += "  ⓘ";
        }
        box.AddChild(r);
    }
}

/// <summary>The small label that follows the cursor over the globe: the hovered tile and the active overlay's value.</summary>
public partial class PlanetHoverTip : PanelContainer
{
    readonly PlanetView _view;
    readonly Label _label;

    public PlanetHoverTip(PlanetView view)
    {
        _view = view;
        var sb = new StyleBoxFlat { BgColor = new Color(0.04f, 0.06f, 0.08f, 0.92f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4 };
        sb.SetCornerRadiusAll(4);
        AddThemeStyleboxOverride("panel", sb);
        _label = UiKit.Label("", 14);
        AddChild(_label);
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        Name = "HoverTip";
        view.TileHovered += _ => Refresh();
    }

    public string Text => _label.Text;

    void Refresh()
    {
        int t = _view.HoverTile;
        if (t < 0) { Visible = false; return; }
        _label.Text = Describe(_view.Planet, t, _view.Overlay);
        Visible = true;
        ResetSize();
    }

    /// <summary>Hover text: the biome, then the value of the active overlay in its own unit.</summary>
    public static string Describe(WorldPlanet p, int t, PlanetBaker.OverlayKind overlay)
    {
        string name = BiomeInfo.Label(p.Biomes[t]);
        bool water = p.Water[t] != WaterBody.None;
        string value = overlay switch
        {
            PlanetBaker.OverlayKind.Temperature => UiKit.Temp(p.Climate.Temperature[t]),
            PlanetBaker.OverlayKind.Elevation => water ? $"{-p.Elevation[t]:F0} m deep" : $"{p.Elevation[t]:F0} m",
            PlanetBaker.OverlayKind.Precipitation => $"{p.Climate.Precipitation[t]:F1} mm/day",
            _ => water ? null : TileInfo.WaterLabel(p, t) is var w && w != "None" ? w.ToLowerInvariant() : null,
        };
        return value == null ? name : $"{name}  ·  {value}";
    }

    public override void _Process(double delta)
    {
        if (Visible) Position = GetParent<Control>().GetLocalMousePosition() + new Vector2(18, 18);
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
    readonly Control _biomeLegend;

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
        _biomeLegend = BiomeLegend();
        AddChild(_biomeLegend);
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
        _biomeLegend.Visible = !_legend.Visible;
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

    /// <summary>Colour chips for every biome, in the exact colours of the globe.</summary>
    static Control BiomeLegend()
    {
        var lp = UiKit.Panel(null, null, 8);
        lp.Name = "BiomeLegend";
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var b in new[] { Biome.Ocean, Biome.Lake, Biome.IceSheet, Biome.Tundra, Biome.BorealForest, Biome.TemperateForest,
                                  Biome.Grassland, Biome.Savanna, Biome.AridShrubland, Biome.Desert, Biome.TropicalRainforest })
        {
            var row = UiKit.HBox(6);
            var c = PlanetBaker.BiomeColor(b);
            row.AddChild(new ColorRect { Color = new Color(c.X, c.Y, c.Z), CustomMinimumSize = new Vector2(16, 14) });
            row.AddChild(UiKit.Label(BiomeInfo.Label(b), 13, UiKit.Text));
            grid.AddChild(row);
        }
        lp.AddChild(grid);
        return lp;
    }
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
    readonly PlanetHoverTip _hoverTip;

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
        _view = UiKit.Fill(new PlanetView(1024));
        AddChild(_view);
        _view.SetPlanet(setup.Planet, () =>
        {
            int start = setup.Planet.FindStartTile();
            if (start >= 0) _view.FocusTile(start, 2.9f);
        });
        _view.TileSelected += OnSelected;

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

        _hoverTip = new PlanetHoverTip(_view);
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

    public PlanetHoverTip HoverTip => _hoverTip;

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
