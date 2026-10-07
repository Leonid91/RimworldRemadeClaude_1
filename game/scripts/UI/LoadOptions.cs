using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Remade.Core;
using Remade.Diagnostics;
using Remade.Save;

namespace Remade.Game.UI;

public static class SaveFiles
{
    public static string Dir
    {
        get
        {
            string d = ProjectSettings.GlobalizePath("user://saves");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>Headers of all saves, newest first. Unreadable files are listed with their error instead of hidden.</summary>
    public static List<(string path, SaveHeader header, string error)> List()
    {
        var list = new List<(string, SaveHeader, string)>();
        foreach (var f in Directory.GetFiles(Dir, "*.rsav"))
        {
            try { list.Add((f, SaveGame.ReadHeader(f), null)); }
            catch (SaveFormatException ex)
            {
                Log.Warn($"Unreadable save {f}: {ex.Message}");
                list.Add((f, null, ex.Message));
            }
        }
        return list.OrderByDescending(e => File.GetLastWriteTimeUtc(e.Item1)).ToList();
    }

    public static string PathFor(string name)
    {
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        if (safe.Length == 0) safe = "colony";
        return System.IO.Path.Combine(Dir, safe + ".rsav");
    }
}

/// <summary>Lists saved games (colony, date in game, colonists, real save time) and loads the chosen one.</summary>
public partial class LoadScreen : ScreenBase
{
    readonly VBoxContainer _rows;
    string _selected;
    Button _load, _delete;

    public LoadScreen() : base("Load game", "Pick up where you left off.")
    {
        var panel = UiKit.Panel(null);
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rows = UiKit.VBox(6);
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_rows);
        panel.AddChild(scroll);
        Body.AddChild(panel);
        Footer.AddChild(UiKit.Button("Back", () => Main.I.ShowMainMenu(), 18, 160));
        FooterSpacer();
        _delete = UiKit.Button("Delete", DeleteSelected, 17, 140);
        _delete.Disabled = true;
        Footer.AddChild(_delete);
        _load = UiKit.Button("Load", () => { if (_selected != null) Main.I.LoadGame(_selected); }, 20, 200);
        _load.Disabled = true;
        Footer.AddChild(_load);
        Fill();
    }

    void Fill()
    {
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        var saves = SaveFiles.List();
        if (saves.Count == 0) _rows.AddChild(UiKit.Label("No saved games yet. Save from the in-game menu (Esc).", 17, UiKit.Muted));
        foreach (var (path, h, err) in saves)
        {
            string text = h == null
                ? $"{System.IO.Path.GetFileNameWithoutExtension(path)}   —   unreadable: {err}"
                : $"{h.ColonyName}   ·   {GameTime.DateString(h.Tick, h.Latitude)}, {GameTime.ClockString(h.Tick)}   ·   {h.Colonists}   ·   map {h.MapSize}   ·   saved {h.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
            var b = UiKit.Button(text, null, 16);
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(0, 46);
            b.Disabled = h == null;
            string p = path;
            b.Pressed += () =>
            {
                _selected = p;
                _load.Disabled = _delete.Disabled = false;
                foreach (var c in _rows.GetChildren()) if (c is Button bb) bb.RemoveThemeStyleboxOverride("normal");
                b.AddThemeStyleboxOverride("normal", UiKit.PanelStyle(new Color(0.10f, 0.20f, 0.20f, 0.95f), UiKit.Accent, 3, 8));
            };
            b.GuiInput += e => { if (e is InputEventMouseButton { DoubleClick: true }) Main.I.LoadGame(p); };
            _rows.AddChild(b);
        }
    }

    void DeleteSelected()
    {
        if (_selected == null) return;
        var dlg = new ConfirmationDialog { DialogText = $"Delete {System.IO.Path.GetFileName(_selected)}? This cannot be undone.", Title = "Delete save" };
        dlg.Confirmed += () =>
        {
            File.Delete(_selected);
            Log.Info($"Deleted save {_selected}");
            _selected = null;
            _load.Disabled = _delete.Disabled = true;
            Fill();
        };
        AddChild(dlg);
        dlg.PopupCentered();
    }
}

/// <summary>Options overlay: rebindable controls (with AZERTY remap), graphics, audio and gameplay.</summary>
public partial class OptionsScreen : Control
{
    readonly TabContainer _tabs;
    string _waitingFor;
    readonly Dictionary<string, Button> _bindButtons = new();

    public OptionsScreen()
    {
        UiKit.Fill(this);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = UiKit.Fill(new ColorRect { Color = new Color(0, 0, 0, 0.6f) });
        AddChild(dim);
        var panel = UiKit.Panel(null, UiKit.InkSolid, 18);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.CustomMinimumSize = new Vector2(980, 720);
        panel.Position = new Vector2(-490, -360);
        AddChild(panel);
        var col = UiKit.VBox(10);
        panel.AddChild(col);
        col.AddChild(UiKit.Label("OPTIONS", 28, UiKit.Text, bold: true));
        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        col.AddChild(_tabs);
        _tabs.AddChild(Controls());
        _tabs.AddChild(Graphics());
        _tabs.AddChild(Audio());
        _tabs.AddChild(Gameplay());
        var foot = UiKit.HBox(10);
        foot.AddChild(UiKit.Expander());
        foot.AddChild(UiKit.Button("Close", Close, 18, 180));
        col.AddChild(foot);
    }

    void Close()
    {
        Settings.Save();
        QueueFree();
    }

    Control Page(string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var v = UiKit.VBox(8);
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(v);
        return scroll;
    }

    static VBoxContainer Inner(Control page) => (VBoxContainer)page.GetChild(0);

    Control Controls()
    {
        var page = Page("Controls");
        var v = Inner(page);
        var presets = UiKit.HBox(8);
        presets.AddChild(UiKit.Button("AZERTY keyboard (ZQSD)", () => { Settings.ApplyAzerty(); Settings.ApplyInput(); RefreshBinds(); }, 16));
        presets.AddChild(UiKit.Button("QWERTY defaults (WASD)", () => { Settings.ResetBindings(); Settings.ApplyInput(); RefreshBinds(); }, 16));
        v.AddChild(presets);
        v.AddChild(UiKit.Label("Click a key, then press the new key (Esc cancels).", 14, UiKit.Muted));
        foreach (var a in Settings.Actions)
        {
            var row = UiKit.HBox(10);
            var l = UiKit.Label(a.Label, 16);
            l.CustomMinimumSize = new Vector2(420, 0);
            row.AddChild(l);
            var b = UiKit.Button(Settings.KeyName(a.Id), null, 16, 200);
            string id = a.Id;
            b.Pressed += () => { _waitingFor = id; b.Text = "press a key…"; };
            _bindButtons[a.Id] = b;
            row.AddChild(b);
            v.AddChild(row);
        }
        return page;
    }

    void RefreshBinds()
    {
        foreach (var kv in _bindButtons) kv.Value.Text = Settings.KeyName(kv.Key);
    }

    public override void _Input(InputEvent e)
    {
        if (_waitingFor == null || e is not InputEventKey { Pressed: true } k) return;
        GetViewport().SetInputAsHandled();
        if (k.Keycode != Key.Escape || _waitingFor == "menu")
        {
            Settings.Bindings[_waitingFor] = k.Keycode;
            Settings.ApplyInput();
            Log.Info($"Rebound {_waitingFor} to {OS.GetKeycodeString(k.Keycode)}");
        }
        _waitingFor = null;
        RefreshBinds();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("menu") && _waitingFor == null) { Close(); GetViewport().SetInputAsHandled(); }
    }

    Control Graphics()
    {
        var page = Page("Graphics");
        var v = Inner(page);
        v.AddChild(Check("Fullscreen", Settings.Fullscreen, on => { Settings.Fullscreen = on; Settings.ApplyDisplay(GetViewport()); }));
        v.AddChild(Check("Vertical sync", Settings.VSync, on => { Settings.VSync = on; Settings.ApplyDisplay(GetViewport()); }));
        v.AddChild(Slider("Render scale", Settings.RenderScale, 0.5f, 1f, 0.05f, x => { Settings.RenderScale = x; Settings.ApplyDisplay(GetViewport()); }, x => $"{x * 100:F0} %"));
        v.AddChild(Slider("Shadow quality", Settings.ShadowQuality, 0, 3, 1, x => { Settings.ShadowQuality = (int)x; GraphicsChanged(); }, x => new[] { "Off", "Low", "Medium", "High" }[(int)x]));
        v.AddChild(Check("Ambient occlusion (SSAO)", Settings.Ssao, on => { Settings.Ssao = on; GraphicsChanged(); }));
        v.AddChild(Check("Volumetric fog and light shafts", Settings.VolumetricFog, on => { Settings.VolumetricFog = on; GraphicsChanged(); }));
        v.AddChild(Slider("Grass density", Settings.GrassDensity, 0f, 1.5f, 0.1f, x => { Settings.GrassDensity = x; GraphicsChanged(); }, x => $"{x * 100:F0} %"));
        v.AddChild(Slider("Frame rate limit", Settings.MaxFps, 0, 240, 30, x => { Settings.MaxFps = (int)x; Settings.ApplyDisplay(GetViewport()); }, x => x == 0 ? "Unlimited" : $"{x:F0} fps"));
        return page;
    }

    void GraphicsChanged() => Main.I.Game?.ApplyGraphicsSettings();

    Control Audio()
    {
        var page = Page("Audio");
        var v = Inner(page);
        v.AddChild(Slider("Master volume", Settings.MasterVolume, 0, 1, 0.05f, x => { Settings.MasterVolume = x; Audio3D.ApplyVolumes(); }, x => $"{x * 100:F0} %"));
        v.AddChild(Slider("Ambience (wind, rain, birds, water)", Settings.AmbienceVolume, 0, 1, 0.05f, x => { Settings.AmbienceVolume = x; Audio3D.ApplyVolumes(); }, x => $"{x * 100:F0} %"));
        v.AddChild(Slider("Effects", Settings.EffectsVolume, 0, 1, 0.05f, x => { Settings.EffectsVolume = x; Audio3D.ApplyVolumes(); }, x => $"{x * 100:F0} %"));
        return page;
    }

    Control Gameplay()
    {
        var page = Page("Gameplay");
        var v = Inner(page);
        v.AddChild(Check("Pan the camera at screen edges", Settings.EdgePan, on => Settings.EdgePan = on));
        v.AddChild(Slider("Interface scale", Settings.UiScale, 0.75f, 1.5f, 0.05f, x => { Settings.UiScale = x; Settings.ApplyDisplay(GetViewport()); }, x => $"{x * 100:F0} %"));
        return page;
    }

    static Control Check(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Text = label, ButtonPressed = value, FocusMode = FocusModeEnum.None };
        c.AddThemeFontSizeOverride("font_size", 16);
        c.Toggled += on => { Log.Action($"option '{label}' = {on}"); set(on); };
        return c;
    }

    static Control Slider(string label, float value, float min, float max, float step, Action<float> set, Func<float, string> fmt)
    {
        var row = UiKit.HBox(12);
        var l = UiKit.Label(label, 16);
        l.CustomMinimumSize = new Vector2(380, 0);
        row.AddChild(l);
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(320, 24), FocusMode = FocusModeEnum.None };
        row.AddChild(s);
        var val = UiKit.Label(fmt(value), 16, UiKit.Accent);
        row.AddChild(val);
        s.ValueChanged += x => { val.Text = fmt((float)x); set((float)x); };
        s.DragEnded += changed => { if (changed) Log.Action($"option '{label}' = {s.Value}"); };
        return row;
    }
}
