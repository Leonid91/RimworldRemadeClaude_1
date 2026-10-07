using System;
using System.Collections.Generic;
using Godot;
using Remade.Diagnostics;

namespace Remade.Game;

/// <summary>
/// Player settings stored in user://settings.cfg: rebindable keys (with a QWERTY → AZERTY remap), graphics, audio
/// and gameplay options. <see cref="ApplyInput"/> rebuilds Godot's InputMap from the bindings.
/// </summary>
public static class Settings
{
    public sealed class ActionDef
    {
        public string Id, Label;
        public Key Default;
        public ActionDef(string id, string label, Key def) { Id = id; Label = label; Default = def; }
    }

    public static readonly ActionDef[] Actions =
    {
        new("move_up", "Move / pan up", Key.W),
        new("move_left", "Move / pan left", Key.A),
        new("move_down", "Move / pan down", Key.S),
        new("move_right", "Move / pan right", Key.D),
        new("sprint", "Sprint (direct control)", Key.Shift),
        new("interact", "Interact (doors, pick up, gather, drink)", Key.E),
        new("direct_control", "Toggle direct control", Key.C),
        new("draft", "Draft / undraft", Key.R),
        new("pause", "Pause", Key.Space),
        new("speed_1", "Normal speed", Key.Key1),
        new("speed_2", "Fast speed", Key.Key2),
        new("speed_3", "Faster speed", Key.Key3),
        new("speed_4", "Ultra speed", Key.Key4),
        new("cam_rotate_left", "Rotate camera left", Key.Bracketleft),
        new("cam_rotate_right", "Rotate camera right", Key.Bracketright),
        new("focus", "Focus selected", Key.F),
        new("world", "Planet view", Key.F2),
        new("next_pawn", "Next colonist", Key.Tab),
        new("menu", "Menu", Key.Escape),
    };

    public static readonly Dictionary<string, Key> Bindings = new();

    // graphics
    public static bool Fullscreen = true;
    public static bool VSync = true;
    public static float RenderScale = 1f;
    public static int ShadowQuality = 2;        // 0 off, 1 low, 2 medium, 3 high
    public static bool Ssao = true;
    public static bool VolumetricFog = true;
    public static float GrassDensity = 1f;
    public static int MaxFps = 0;
    // audio
    public static float MasterVolume = 0.8f, AmbienceVolume = 0.8f, EffectsVolume = 0.8f;
    // gameplay
    public static bool EdgePan = false;
    public static float UiScale = 1f;

    const string Path = "user://settings.cfg";

    public static void ResetBindings()
    {
        Bindings.Clear();
        foreach (var a in Actions) Bindings[a.Id] = a.Default;
    }

    /// <summary>Remaps the movement keys for an AZERTY keyboard (ZQSD) and moves keys that would collide.</summary>
    public static void ApplyAzerty()
    {
        Bindings["move_up"] = Key.Z;
        Bindings["move_left"] = Key.Q;
        Bindings["move_down"] = Key.S;
        Bindings["move_right"] = Key.D;
        Log.Info("Key bindings remapped for AZERTY (ZQSD)");
    }

    /// <summary>True when the OS keyboard layout looks French/Belgian (AZERTY).</summary>
    public static bool LayoutLooksAzerty()
    {
        int cur = DisplayServer.KeyboardGetCurrentLayout();
        if (cur < 0) return false;
        string lang = DisplayServer.KeyboardGetLayoutLanguage(cur) ?? "";
        string name = DisplayServer.KeyboardGetLayoutName(cur) ?? "";
        return lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase) || name.Contains("AZERTY", StringComparison.OrdinalIgnoreCase)
               || name.Contains("French", StringComparison.OrdinalIgnoreCase) || name.Contains("Belg", StringComparison.OrdinalIgnoreCase);
    }

    public static string KeyName(string action) => Bindings.TryGetValue(action, out var k) ? OS.GetKeycodeString(k) : "?";

    public static void Load()
    {
        ResetBindings();
        var cfg = new ConfigFile();
        var err = cfg.Load(Path);
        if (err == Error.FileNotFound)
        {
            Log.Info("No settings file yet; using defaults");
            if (LayoutLooksAzerty()) ApplyAzerty();
            return;
        }
        if (err != Error.Ok) throw new InvalidOperationException($"Could not read {Path}: {err}");
        foreach (var a in Actions)
        {
            var v = cfg.GetValue("keys", a.Id, (long)a.Default);
            Bindings[a.Id] = (Key)(long)v;
        }
        Fullscreen = (bool)cfg.GetValue("graphics", "fullscreen", Fullscreen);
        VSync = (bool)cfg.GetValue("graphics", "vsync", VSync);
        RenderScale = (float)(double)cfg.GetValue("graphics", "render_scale", RenderScale);
        ShadowQuality = (int)(long)cfg.GetValue("graphics", "shadows", ShadowQuality);
        Ssao = (bool)cfg.GetValue("graphics", "ssao", Ssao);
        VolumetricFog = (bool)cfg.GetValue("graphics", "volumetric_fog", VolumetricFog);
        GrassDensity = (float)(double)cfg.GetValue("graphics", "grass", GrassDensity);
        MaxFps = (int)(long)cfg.GetValue("graphics", "max_fps", MaxFps);
        MasterVolume = (float)(double)cfg.GetValue("audio", "master", MasterVolume);
        AmbienceVolume = (float)(double)cfg.GetValue("audio", "ambience", AmbienceVolume);
        EffectsVolume = (float)(double)cfg.GetValue("audio", "effects", EffectsVolume);
        EdgePan = (bool)cfg.GetValue("gameplay", "edge_pan", EdgePan);
        UiScale = (float)(double)cfg.GetValue("gameplay", "ui_scale", UiScale);
        Validate();
        Log.Info($"Settings loaded from {ProjectSettings.GlobalizePath(Path)}");
    }

    static void Validate()
    {
        RenderScale = Math.Clamp(RenderScale, 0.5f, 1f);
        ShadowQuality = Math.Clamp(ShadowQuality, 0, 3);
        GrassDensity = Math.Clamp(GrassDensity, 0f, 1.5f);
        UiScale = Math.Clamp(UiScale, 0.75f, 1.5f);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        foreach (var kv in Bindings) cfg.SetValue("keys", kv.Key, (long)kv.Value);
        cfg.SetValue("graphics", "fullscreen", Fullscreen);
        cfg.SetValue("graphics", "vsync", VSync);
        cfg.SetValue("graphics", "render_scale", RenderScale);
        cfg.SetValue("graphics", "shadows", ShadowQuality);
        cfg.SetValue("graphics", "ssao", Ssao);
        cfg.SetValue("graphics", "volumetric_fog", VolumetricFog);
        cfg.SetValue("graphics", "grass", GrassDensity);
        cfg.SetValue("graphics", "max_fps", MaxFps);
        cfg.SetValue("audio", "master", MasterVolume);
        cfg.SetValue("audio", "ambience", AmbienceVolume);
        cfg.SetValue("audio", "effects", EffectsVolume);
        cfg.SetValue("gameplay", "edge_pan", EdgePan);
        cfg.SetValue("gameplay", "ui_scale", UiScale);
        var err = cfg.Save(Path);
        if (err != Error.Ok) throw new InvalidOperationException($"Could not write {Path}: {err}");
        Log.Info("Settings saved");
    }

    /// <summary>Rebuilds the InputMap actions from the bindings.</summary>
    public static void ApplyInput()
    {
        foreach (var a in Actions)
        {
            if (!InputMap.HasAction(a.Id)) InputMap.AddAction(a.Id);
            InputMap.ActionEraseEvents(a.Id);
            InputMap.ActionAddEvent(a.Id, new InputEventKey { Keycode = Bindings[a.Id] });
        }
        // arrow keys always pan the camera as well
        AddExtra("move_up", Key.Up); AddExtra("move_down", Key.Down); AddExtra("move_left", Key.Left); AddExtra("move_right", Key.Right);
    }

    static void AddExtra(string action, Key k)
    {
        if (Bindings[action] != k) InputMap.ActionAddEvent(action, new InputEventKey { Keycode = k });
    }

    /// <summary>Applies window, vsync, fps and render-scale settings.</summary>
    public static void ApplyDisplay(Viewport root)
    {
        if (!Args.Has("windowed") && !Args.Has("headless"))
            DisplayServer.WindowSetMode(Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetVsyncMode(VSync && !Args.Has("novsync") ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = MaxFps;
        root.Scaling3DScale = RenderScale;
        root.GetWindow().ContentScaleFactor = UiScale;
    }
}
