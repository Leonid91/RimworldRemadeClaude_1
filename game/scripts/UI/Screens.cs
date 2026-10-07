using System;
using System.Threading.Tasks;
using Godot;
using Remade.Diagnostics;
using Remade.Game.Planet3D;
using Remade.World;

namespace Remade.Game.UI;

/// <summary>Base for full-screen menu pages: space backdrop, a title block, a content area and a footer of buttons.</summary>
public partial class ScreenBase : Control
{
    protected VBoxContainer Body;
    protected HBoxContainer Footer;

    protected ScreenBase(string title, string subtitle, bool backdrop = true)
    {
        UiKit.Fill(this);
        MouseFilter = MouseFilterEnum.Stop;
        if (backdrop)
        {
            var bg = UiKit.Fill(new ColorRect { Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/menu_bg.gdshader") } });
            AddChild(bg);
        }
        var margin = UiKit.Fill(new MarginContainer());
        margin.AddThemeConstantOverride("margin_left", 80);
        margin.AddThemeConstantOverride("margin_right", 80);
        margin.AddThemeConstantOverride("margin_top", 50);
        margin.AddThemeConstantOverride("margin_bottom", 40);
        AddChild(margin);
        var col = UiKit.VBox(14);
        margin.AddChild(col);
        if (title != null)
        {
            var t = UiKit.Label(title.ToUpperInvariant(), 34, UiKit.Text, bold: true);
            t.AddThemeConstantOverride("outline_size", 0);
            col.AddChild(t);
            if (subtitle != null) col.AddChild(UiKit.Label(subtitle, 17, UiKit.Muted));
            col.AddChild(new ColorRect { Color = new Color(UiKit.Accent, 0.5f), CustomMinimumSize = new Vector2(220, 2), SizeFlagsHorizontal = SizeFlags.ShrinkBegin });
        }
        Body = UiKit.VBox(12);
        Body.SizeFlagsVertical = SizeFlags.ExpandFill;
        col.AddChild(Body);
        Footer = UiKit.HBox(12);
        col.AddChild(Footer);
    }

    protected void FooterSpacer() => Footer.AddChild(UiKit.Expander());
}

/// <summary>Main menu over an orbital dawn: the planet's limb glows in the lower half while the sun rises behind it.</summary>
public partial class MainMenu : Control
{
    static Remade.World.Planet _menuPlanet;

    public MainMenu()
    {
        UiKit.Fill(this);
        var view = UiKit.Fill(new PlanetView(768) { Interactive = false, Mode = PlanetView.CameraMode.MenuLimb });
        view.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(view);
        var veil = UiKit.Fill(new ColorRect { Color = new Color(0, 0, 0, 1) });
        veil.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(veil);
        _menuPlanet ??= new Remade.World.Planet(new WorldParams { Seed = 2026, Frequency = 40 });
        view.SetPlanet(_menuPlanet, () => CreateTween().TweenProperty(veil, "color:a", 0f, 2.5f));

        // gradient so the text stays readable over the sky
        var shade = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient { Colors = new[] { new Color(0, 0, 0, 0.65f), new Color(0, 0, 0, 0) }, Offsets = new[] { 0f, 1f } },
                FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1), Width = 4, Height = 256,
            },
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        shade.SetAnchorsPreset(LayoutPreset.TopWide);
        shade.OffsetBottom = 520;
        AddChild(shade);

        var col = UiKit.VBox(10);
        col.SetAnchorsPreset(LayoutPreset.CenterTop);
        col.Position = new Vector2(-260, 120);
        col.CustomMinimumSize = new Vector2(520, 0);
        AddChild(col);
        var title = UiKit.Label("RIMWORLD", 76, UiKit.Text, bold: true, align: HorizontalAlignment.Center);
        title.AddThemeConstantOverride("outline_size", 0);
        col.AddChild(title);
        var sub = UiKit.Label("R  E  M  A  D  E", 22, UiKit.Accent, align: HorizontalAlignment.Center);
        col.AddChild(sub);
        col.AddChild(UiKit.Spacer(0, 44));
        foreach (var (text, act) in new (string, Action)[]
                 {
                     ("Play", () => Main.I.StartNewGameFlow()),
                     ("Load game", () => Main.I.ShowLoadMenu()),
                     ("Options", () => Main.I.ShowOptions(this)),
                     ("Exit", () => Main.I.Quit()),
                 })
        {
            var b = UiKit.Button(text, act, 21);
            b.CustomMinimumSize = new Vector2(340, 50);
            b.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            col.AddChild(b);
        }
        var ver = UiKit.Label($"build {Log.BuildId}", 13, new Color(UiKit.Muted, 0.7f), align: HorizontalAlignment.Right);
        ver.SetAnchorsPreset(LayoutPreset.BottomRight);
        ver.Position = new Vector2(-420, -34);
        ver.CustomMinimumSize = new Vector2(400, 0);
        AddChild(ver);
    }
}

/// <summary>Scenario selection. The list and editor are prepared but empty in this version.</summary>
public partial class ScenarioScreen : ScreenBase
{
    public ScenarioScreen() : base("Select scenario", "Scenarios set the starting conditions of a colony.")
    {
        var row = UiKit.HBox(16);
        row.SizeFlagsVertical = SizeFlags.ExpandFill;
        Body.AddChild(row);
        var listPanel = UiKit.Panel(null);
        listPanel.CustomMinimumSize = new Vector2(420, 0);
        var listCol = UiKit.VBox(8);
        listPanel.AddChild(listCol);
        listCol.AddChild(UiKit.Heading("Scenarios"));
        listCol.AddChild(UiKit.Label("No scenarios yet.", 17, UiKit.Muted));
        listCol.AddChild(UiKit.Label("Custom scenarios will appear here.", 15, new Color(UiKit.Muted, 0.7f)));
        listCol.AddChild(UiKit.Expander());
        var tools = UiKit.HBox(8);
        var create = UiKit.Button("New scenario", null); create.Disabled = true; create.TooltipText = "The scenario editor comes in a later version.";
        var edit = UiKit.Button("Edit", null); edit.Disabled = true; edit.TooltipText = "The scenario editor comes in a later version.";
        tools.AddChild(create); tools.AddChild(edit);
        listCol.AddChild(tools);
        row.AddChild(listPanel);

        var detail = UiKit.Panel(null);
        detail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var dcol = UiKit.VBox(10);
        detail.AddChild(dcol);
        dcol.AddChild(UiKit.Heading("Scenario details"));
        dcol.AddChild(UiKit.Label("Default landing", 22, UiKit.Text, bold: true));
        var desc = UiKit.Label("Three colonists arrive on a wild, temperate world. An abandoned cabin stands near the landing site. " +
                               "What happens next is up to you.", 17, UiKit.Muted);
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        dcol.AddChild(desc);
        row.AddChild(detail);

        Footer.AddChild(UiKit.Button("Back", () => Main.I.ShowMainMenu(), 18, 160));
        FooterSpacer();
        Footer.AddChild(UiKit.Button("Next", () => Main.I.ShowColonists(), 18, 200));
    }
}

/// <summary>World parameters (prepared, empty for now) and the Generate button.</summary>
public partial class WorldGenScreen : ScreenBase
{
    Task<Remade.World.Planet> _task;
    Label _status;
    Button _gen;
    double _elapsed;

    public WorldGenScreen() : base("Generate world", "Shape the planet your colonists will land on.")
    {
        var panel = UiKit.Panel(null);
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        var col = UiKit.VBox(12);
        panel.AddChild(col);
        col.AddChild(UiKit.Heading("World parameters"));
        var note = UiKit.Label("Planet coverage, seed, rainfall, temperature and population settings will be available here in a later version. " +
                               "For now a default world is generated.", 17, UiKit.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(note);
        col.AddChild(UiKit.Expander());
        _status = UiKit.Label("", 18, UiKit.Accent);
        col.AddChild(_status);
        Body.AddChild(panel);

        Footer.AddChild(UiKit.Button("Back", () => Main.I.ShowColonists(), 18, 160));
        FooterSpacer();
        _gen = UiKit.Button("Generate", Generate, 20, 240);
        Footer.AddChild(_gen);
    }

    void Generate()
    {
        if (_task != null) return;
        var setup = Main.I.Setup;
        setup.World = new WorldParams { Seed = setup.Seed };
        _gen.Disabled = true;
        _status.Text = "Generating the planet…";
        _task = Task.Run(() => new Remade.World.Planet(setup.World));
    }

    public override void _Process(double delta)
    {
        if (_task == null) return;
        _elapsed += delta;
        _status.Text = "Generating the planet" + new string('.', 1 + (int)(_elapsed * 3) % 3);
        if (!_task.IsCompleted) return;
        var t = _task;
        _task = null;
        if (t.IsFaulted)
        {
            var ex = t.Exception.InnerException ?? t.Exception;
            Log.Exception(ex, $"World generation failed (seed {Main.I.Setup.World.Seed})");
            _status.Text = "World generation failed: " + ex.Message;
            _status.AddThemeColorOverride("font_color", UiKit.Bad);
            _gen.Disabled = false;
            return;
        }
        Main.I.Setup.Planet = t.Result;
        Main.I.ShowPlanet();
    }
}

/// <summary>Waits for a background task (map generation, loading) with a calm animated status line.</summary>
public partial class LoadingScreen : ScreenBase
{
    readonly Label _label;
    readonly string _text;
    double _t;
    Func<bool> _poll;

    public LoadingScreen(string text) : base(null, null)
    {
        _text = text;
        _label = UiKit.Label(text, 26, UiKit.Text, align: HorizontalAlignment.Center);
        _label.SetAnchorsPreset(LayoutPreset.Center);
        _label.Position = new Vector2(-500, -20);
        _label.CustomMinimumSize = new Vector2(1000, 0);
        AddChild(_label);
    }

    public void Await<T>(Task<T> task, Action<T> onDone, Action onError = null)
    {
        _poll = () =>
        {
            if (!task.IsCompleted) return false;
            if (task.IsFaulted)
            {
                var ex = task.Exception.InnerException ?? task.Exception;
                Log.Exception(ex, $"Background task for '{_text}' failed");
                _label.Text = "Failed: " + ex.Message;
                _label.AddThemeColorOverride("font_color", UiKit.Bad);
                var back = UiKit.Button("Back to menu", () => (onError ?? Main.I.ShowMainMenu)(), 18, 220);
                back.SetAnchorsPreset(LayoutPreset.Center);
                back.Position = new Vector2(-110, 40);
                AddChild(back);
                return true;
            }
            onDone(task.Result);
            return true;
        };
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_poll != null && _poll()) { _poll = null; return; }
        if (_poll != null) _label.Text = _text + new string('.', 1 + (int)(_t * 3) % 3);
    }
}
