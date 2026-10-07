using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Remade.Diagnostics;
using Remade.Game.UI;
using Remade.Pawns;
using Remade.Sim;
using Remade.World;

namespace Remade.Game;

/// <summary>Choices made while setting up a new colony (scenario → colonists → world → landing site → map size).</summary>
public sealed class NewGameSetup
{
    public string Scenario = "Crash landing";
    public List<Pawn> Colonists = new();
    public int NextPawnId = 1;
    public WorldParams World;
    public Planet Planet;
    public int Tile = -1;
    public int MapSize = 400;
    public string ColonyName = "New Hope";
    public int Seed = System.Environment.TickCount & 0x7fffffff;
}

/// <summary>
/// Root of the game: boots logging/settings/input, owns the current screen (menus, planet, colony) and switches
/// between them. One instance, reachable through <see cref="I"/>.
/// </summary>
public partial class Main : Node
{
    public static Main I { get; private set; }
    public NewGameSetup Setup;
    Node _screen;
    CanvasLayer _overlay;
    Label _errorBanner;
    double _errorTimer;
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingErrors = new();
    public AutoPilot Auto { get; private set; }
    public Node CurrentScreen => _screen;
    public GameView Game => _screen as GameView;

    public static string LogDir { get; private set; }
    public static string ScreenshotDir { get; private set; }

    public override void _Ready()
    {
        I = this;
        Boot();
        _overlay = new CanvasLayer { Layer = 100 };
        AddChild(_overlay);
        _errorBanner = UiKit.Label("", 16, UiKit.Bad, bold: true);
        _errorBanner.Visible = false;
        var bannerPanel = UiKit.Panel(_errorBanner, new Color(0.18f, 0.04f, 0.03f, 0.92f), 10);
        bannerPanel.Name = "ErrorBanner";
        bannerPanel.Visible = false;
        bannerPanel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        bannerPanel.Position = new Vector2(560, 8);
        bannerPanel.CustomMinimumSize = new Vector2(800, 0);
        _overlay.AddChild(bannerPanel);
        Log.ErrorWritten += (lvl, msg) => _pendingErrors.Enqueue(msg);

        if (Args.Has("auto"))
        {
            Auto = new AutoPilot(Args.Get("auto"));
            AddChild(Auto);
        }
        if (Args.Has("play")) QuickStart();
        else ShowMainMenu();
    }

    void Boot()
    {
        string root = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');
        string projectRoot = System.IO.Path.GetDirectoryName(root);
        bool fromSource = System.IO.File.Exists(System.IO.Path.Combine(projectRoot, "DEVLOG.md"));
        LogDir = fromSource ? System.IO.Path.Combine(projectRoot, "logs") : ProjectSettings.GlobalizePath("user://logs");
        ScreenshotDir = fromSource ? System.IO.Path.Combine(projectRoot, "screenshots") : ProjectSettings.GlobalizePath("user://screenshots");
        System.IO.Directory.CreateDirectory(ScreenshotDir);
        Log.Init(new LogConfig
        {
            Directory = LogDir,
            Verbose = Args.Has("verbose") || Args.Has("auto"),
            MinConsoleLevel = Args.Has("verbose") ? LogLevel.Debug : LogLevel.Info,
            MirrorToConsole = true,
        });
        Log.ConsoleSink = (lvl, line) => Console.WriteLine(line);
        OS.AddLogger(new EngineLogBridge());
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Exception(ex, "Unhandled exception (process terminating)", LogLevel.Fatal);
            else Log.Fatal("Unhandled non-exception object: " + e.ExceptionObject);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Exception(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Log.Info($"Godot {Engine.GetVersionInfo()["string"]} | {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterVendor()}) | " +
                 $"{OS.GetName()} | args: {Args.Describe()} | user dir {OS.GetUserDataDir()}");
        Settings.Load();
        if (Args.Has("shadow-quality")) Settings.ShadowQuality = int.Parse(Args.Get("shadow-quality")); // profiling override
        Settings.ApplyInput();
        Settings.ApplyDisplay(GetViewport());
        GetTree().Root.Theme = UiKit.Theme;
        GetTree().Root.CloseRequested += () => { Log.Info("Window closed by the user"); Log.Shutdown(); };
    }

    public override void _Process(double delta)
    {
        while (_pendingErrors.TryDequeue(out var msg))
        {
            _errorBanner.Text = "Error: " + Trim(msg, 140) + "  (details in the log)";
            _errorBanner.Visible = true;
            ((Control)_errorBanner.GetParent()).Visible = true;
            _errorTimer = 8;
        }
        if (_errorTimer > 0 && (_errorTimer -= delta) <= 0) ((Control)_errorBanner.GetParent()).Visible = false;
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) { Log.Info("Quit requested"); Log.Flush(); }
    }

    // ------------------------------------------------------------------ screens

    public void SetScreen(Node screen, string state)
    {
        if (_screen != null) { RemoveChild(_screen); _screen.QueueFree(); }
        _screen = screen;
        AddChild(screen);
        MoveChild(screen, 0);
        Log.GameState = state;
        Log.Info($"Screen -> {state}");
    }

    public void ShowMainMenu() => SetScreen(new MainMenu(), "menu");

    public void StartNewGameFlow()
    {
        Setup = new NewGameSetup();
        SetScreen(new ScenarioScreen(), "scenario");
    }

    public void ShowColonists()
    {
        if (Setup.Colonists.Count == 0)
            Setup.Colonists = PawnGenerator.GenerateGroup(3, Setup.Seed, () => Setup.NextPawnId++);
        SetScreen(new ColonistScreen(), "colonists");
    }

    public void ShowWorldGen() => SetScreen(new WorldGenScreen(), "worldgen");

    public void ShowPlanet() => SetScreen(new PlanetScreen(), "planet");

    public void ShowLoadMenu() => SetScreen(new LoadScreen(), "load menu");

    public void ShowOptions(Node parent = null)
    {
        var opts = new OptionsScreen();
        (parent ?? _screen).AddChild(opts);
    }

    /// <summary>Generates the colony (map generation runs on a worker thread while a loading screen shows).</summary>
    public void StartColony()
    {
        if (Setup?.Planet == null || Setup.Tile < 0) throw new InvalidOperationException("StartColony without a planet and landing site");
        var setup = Setup;
        var loading = new LoadingScreen($"Landing at {BiomeInfo.Label(setup.Planet.Biomes[setup.Tile])}…");
        SetScreen(loading, "generating");
        long startTick = Remade.Core.GameTime.TicksPerHour * 7; // landing at dawn, day 1 of spring
        var task = Task.Run(() =>
        {
            Log.Tick = startTick;
            return GameSim.NewColony(setup.Planet, setup.Tile, setup.MapSize, setup.Colonists, setup.Seed, startTick);
        });
        loading.Await(task, sim =>
        {
            sim.ColonyName = setup.ColonyName;
            SetScreen(new GameView(sim), "playing");
        });
    }

    public void LoadGame(string path)
    {
        var loading = new LoadingScreen("Loading " + System.IO.Path.GetFileNameWithoutExtension(path) + "…");
        SetScreen(loading, "loading");
        var task = Task.Run(() => Remade.Save.SaveGame.Read(path));
        loading.Await(task, sim => SetScreen(new GameView(sim), "playing"), onError: ShowMainMenu);
    }

    /// <summary>--play: skips the menus with default choices (for quick testing).</summary>
    void QuickStart()
    {
        Setup = new NewGameSetup { Seed = Args.GetInt("seed", 1234), MapSize = Args.GetInt("map", 250) };
        Setup.Colonists = PawnGenerator.GenerateGroup(3, Setup.Seed, () => Setup.NextPawnId++);
        Setup.World = new WorldParams { Seed = Setup.Seed };
        Log.Info($"Quick start: seed {Setup.Seed}, map {Setup.MapSize}");
        var loading = new LoadingScreen("Generating world…");
        SetScreen(loading, "generating");
        var setup = Setup;
        var task = Task.Run(() =>
        {
            setup.Planet = new Planet(setup.World);
            setup.Tile = Args.Has("tile") ? Args.GetInt("tile", 0) : setup.Planet.FindStartTile();
            if (setup.Tile < 0) throw new InvalidOperationException("No playable temperate tile on this planet");
            long startTick = (long)(Remade.Core.GameTime.TicksPerHour * Args.GetFloat("hour", 7f));
            return GameSim.NewColony(setup.Planet, setup.Tile, setup.MapSize, setup.Colonists, setup.Seed, (long)startTick);
        });
        loading.Await(task, sim =>
        {
            if (Args.Has("weather")) { sim.Weather.DebugOverride = Args.Get("weather"); sim.Weather.Update(sim.Planet, sim.Map.PlanetTile, sim.Tick, true); }
            SetScreen(new GameView(sim), "playing");
        });
    }

    public void Quit()
    {
        Log.Info("Quitting");
        Log.Shutdown();
        GetTree().Quit();
    }

    /// <summary>Saves a screenshot of the main viewport to the screenshots folder.</summary>
    public string Screenshot(string name)
    {
        var img = GetViewport().GetTexture().GetImage();
        string path = System.IO.Path.Combine(ScreenshotDir, name + ".png");
        var err = img.SavePng(path);
        if (err != Error.Ok) throw new InvalidOperationException($"Screenshot {path} failed: {err}");
        Log.Info($"Screenshot saved: {path}");
        return path;
    }
}
