using System;
using System.IO;
using System.Linq;
using Remade.Core;
using Remade.Diagnostics;

namespace Remade.Tests;

public class RngTests
{
    [Fact]
    public void SameSeedSameSequence()
    {
        var a = new Rng(123); var b = new Rng(123);
        for (int i = 0; i < 1000; i++) Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void RangeStaysInBounds()
    {
        var r = new Rng(5);
        for (int i = 0; i < 10000; i++)
        {
            int v = r.Range(-3, 7);
            Assert.InRange(v, -3, 6);
            float f = r.NextFloat();
            Assert.InRange(f, 0f, 0.99999994f);
        }
    }

    [Fact]
    public void EmptyRangeThrows() => Assert.Throws<ArgumentException>(() => new Rng(1).Range(4, 4));

    [Fact]
    public void StateRoundTrips()
    {
        var a = new Rng(99);
        a.NextULong();
        a.GetState(out var s0, out var s1, out var s2, out var s3);
        var b = Rng.FromState(s0, s1, s2, s3);
        Assert.Equal(a.NextULong(), b.NextULong());
    }
}

public class NoiseTests
{
    [Fact]
    public void NoiseIsBoundedAndDeterministic()
    {
        var n1 = new Noise(4); var n2 = new Noise(4);
        var r = new Rng(1);
        for (int i = 0; i < 20000; i++)
        {
            float x = r.Range(-100f, 100f), y = r.Range(-100f, 100f), z = r.Range(-100f, 100f);
            float a = n1.Get(x, y, z);
            Assert.Equal(a, n2.Get(x, y, z));
            Assert.InRange(a, -1.05f, 1.05f);
            Assert.InRange(n1.Get(x, y), -1.05f, 1.05f);
            Assert.InRange(n1.Ridged(x, y, z, 4), 0f, 1f);
        }
    }

    [Fact]
    public void DifferentSeedsDiffer()
    {
        var a = new Noise(1); var b = new Noise(2);
        int diff = 0;
        for (int i = 0; i < 100; i++) if (a.Get(i * 0.37f, i * 0.11f) != b.Get(i * 0.37f, i * 0.11f)) diff++;
        Assert.True(diff > 90);
    }
}

public class GameTimeTests
{
    [Fact]
    public void CalendarMath()
    {
        Assert.Equal(2500, GameTime.TicksPerHour);
        Assert.Equal(6f, GameTime.HourOfDay(GameTime.TicksPerHour * 6), 3);
        Assert.Equal(Season.Summer, GameTime.SeasonAt(GameTime.TicksPerDay * 16, 45f));
        Assert.Equal(Season.Winter, GameTime.SeasonAt(GameTime.TicksPerDay * 16, -45f));
        Assert.Equal(2, GameTime.Year(GameTime.TicksPerYear + 5));
        Assert.Equal("06:30", GameTime.ClockString(GameTime.TicksPerHour * 6 + GameTime.TicksPerHour / 2));
    }
}

[Collection("Log")]
public class LogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "remade_logtest_" + Guid.NewGuid().ToString("N")[..6]);

    public void Dispose()
    {
        Log.Shutdown();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void ErrorsAreFlushedImmediatelyWithContext()
    {
        Log.Init(new LogConfig { Directory = _dir, MirrorToConsole = false });
        Log.Tick = 1234;
        Log.MapId = "tile77";
        Log.Debug("breadcrumb before the failure");
        Log.Action("pressed E on door");
        Log.Error("something broke");
        // no Flush/Shutdown: the error must already be on disk
        string text = ReadShared(Log.CurrentFile);
        Assert.Contains("ERROR", text);
        Assert.Contains("something broke", text);
        Assert.Contains("t:1234", text);
        Assert.Contains("map:tile77", text);
        Assert.Contains("breadcrumb before the failure", text);
        Assert.Contains("pressed E on door", text);
        Assert.Contains("CoreTests.cs", text); // caller file
        Log.Tick = -1;
        Log.MapId = "-";
    }

    [Fact]
    public void ExceptionsIncludeTypeMessageAndStack()
    {
        Log.Init(new LogConfig { Directory = _dir, MirrorToConsole = false });
        try { ThrowSomething(); }
        catch (InvalidOperationException ex) { Log.Exception(ex, "while testing"); }
        string text = ReadShared(Log.CurrentFile);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("boom 42", text);
        Assert.Contains(nameof(ThrowSomething), text);
    }

    static void ThrowSomething() => throw new InvalidOperationException("boom 42");

    [Fact]
    public void InfoIsWrittenButDebugOnlyInVerbose()
    {
        Log.Init(new LogConfig { Directory = _dir, MirrorToConsole = false });
        Log.Info("info line");
        Log.Debug("debug line");
        Log.Flush();
        string text = ReadShared(Log.CurrentFile);
        Assert.Contains("info line", text);
        Assert.DoesNotContain("debug line", text);
        Log.Init(new LogConfig { Directory = _dir, MirrorToConsole = false, Verbose = true });
        Log.Debug("verbose debug line");
        Log.Flush();
        Assert.Contains("verbose debug line", ReadShared(Log.CurrentFile));
    }

    [Fact]
    public void FilesRotateAndArePruned()
    {
        Log.Init(new LogConfig { Directory = _dir, MirrorToConsole = false, MaxFileBytes = 4000, MaxFiles = 3 });
        for (int i = 0; i < 400; i++) Log.Info("line " + i + new string('x', 60));
        Log.Flush();
        Log.Shutdown();
        var files = Directory.GetFiles(_dir, "*.log");
        Assert.InRange(files.Length, 1, 4);
        Assert.True(files.Sum(f => new FileInfo(f).Length) < 4000 * 5);
    }

    [Fact]
    public void InvariantViolationThrowsAndLogs()
    {
        Log.Init(new LogConfig { Directory = _dir, MirrorToConsole = false });
        int x = 3;
        var ex = Assert.Throws<InvariantException>(() => Invariant.Check(x == 4, "x must be four"));
        Assert.Contains("x == 4", ex.Message);
        Assert.Contains("INVARIANT VIOLATED", ReadShared(Log.CurrentFile));
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}

[CollectionDefinition("Log", DisableParallelization = true)]
public class LogCollection { }
