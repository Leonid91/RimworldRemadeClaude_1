using System;
using System.Runtime.CompilerServices;

namespace Remade.Diagnostics;

/// <summary>Thrown when internal state violates an invariant. Never caught to keep the game going.</summary>
public sealed class InvariantException : Exception
{
    public InvariantException(string message) : base(message) { }
}

/// <summary>
/// Explicit invariants: a violated invariant is logged at ERROR (with context and breadcrumbs) and then thrown,
/// so the bug fails close to its source instead of being silently "repaired".
/// </summary>
public static class Invariant
{
    public static void Check(bool condition, string message,
        [CallerArgumentExpression(nameof(condition))] string expr = "",
        [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (condition) return;
        Fail($"{message} [failed: {expr}]", file, member, line);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Fail(string message,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        Log.WriteRaw(LogLevel.Error, "Invariant", "INVARIANT VIOLATED: " + message + "\n" + new System.Diagnostics.StackTrace(1, true),
            $"{System.IO.Path.GetFileName(file)}:{line} {member}");
        throw new InvariantException(message);
    }
}
