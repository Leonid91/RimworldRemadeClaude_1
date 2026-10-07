using System;
using System.Collections.Generic;
using Godot;

namespace Remade.Game;

/// <summary>Command-line options (after "--"): --play, --map=N, --seed=N, --auto="...", --verbose, --windowed, --perf, ...</summary>
public static class Args
{
    static Dictionary<string, string> _opts;

    static Dictionary<string, string> Opts
    {
        get
        {
            if (_opts != null) return _opts;
            _opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in OS.GetCmdlineUserArgs())
            {
                if (!raw.StartsWith("--")) continue;
                string a = raw[2..];
                int eq = a.IndexOf('=');
                if (eq < 0) _opts[a] = "";
                else _opts[a[..eq]] = a[(eq + 1)..].Trim('"');
            }
            return _opts;
        }
    }

    public static bool Has(string name) => Opts.ContainsKey(name);
    public static string Get(string name, string def = null) => Opts.TryGetValue(name, out var v) ? v : def;

    public static int GetInt(string name, int def)
    {
        if (!Opts.TryGetValue(name, out var v)) return def;
        if (!int.TryParse(v, out int r)) throw new ArgumentException($"--{name}={v} is not an integer");
        return r;
    }

    public static float GetFloat(string name, float def)
    {
        if (!Opts.TryGetValue(name, out var v)) return def;
        if (!float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r))
            throw new ArgumentException($"--{name}={v} is not a number");
        return r;
    }

    public static string Describe() => string.Join(" ", OS.GetCmdlineUserArgs());
}
