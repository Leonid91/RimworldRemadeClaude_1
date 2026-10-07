using System;
using System.Text;
using Godot;
using Remade.Diagnostics;

namespace Remade.Game;

/// <summary>
/// Routes Godot's own error/warning stream (engine errors, shader compile errors, resource loading failures,
/// unhandled C# exceptions in callbacks) into the persistent game log, with the script backtraces Godot provides.
/// </summary>
public partial class EngineLogBridge : Logger
{
    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify,
        int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        // errorType: 0 error, 1 warning, 2 script, 3 shader
        var level = errorType == 1 ? LogLevel.Warning : LogLevel.Error;
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrEmpty(rationale) ? code : rationale);
        if (!string.IsNullOrEmpty(rationale) && !string.IsNullOrEmpty(code) && code != rationale) sb.Append(" | ").Append(code);
        string kind = errorType switch { 2 => "script", 3 => "shader", 1 => "warning", _ => "engine" };
        if (scriptBacktraces != null)
            foreach (var bt in scriptBacktraces)
                if (bt != null && !bt.IsEmpty()) sb.Append('\n').Append(bt.Format());
        Log.WriteRaw(level, "Godot." + kind, sb.ToString(), $"{file}:{line} {function}");
    }

    public override void _LogMessage(string message, bool error)
    {
        // Our own console output goes through Console.WriteLine, so these are engine/GD.Print messages.
        if (error) Log.WriteRaw(LogLevel.Warning, "Godot.stderr", message.TrimEnd(), "-");
    }

}
