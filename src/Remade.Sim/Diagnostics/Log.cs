using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Remade.Diagnostics;

public enum LogLevel : byte { Trace, Debug, Info, Warning, Error, Fatal }

public sealed class LogConfig
{
    public string Directory = "logs";
    public string FilePrefix = "game";
    public LogLevel MinFileLevel = LogLevel.Info;
    /// <summary>Breadcrumbs keep recent entries at or above this level in memory, even if they are not written.</summary>
    public LogLevel MinBreadcrumbLevel = LogLevel.Debug;
    public LogLevel MinConsoleLevel = LogLevel.Info;
    public long MaxFileBytes = 8L * 1024 * 1024;
    public int MaxFiles = 24;
    public int MaxAgeDays = 7;
    public int BreadcrumbCapacity = 256;
    /// <summary>Verbose diagnostic mode (automated tests): writes Debug entries and timings to the file.</summary>
    public bool Verbose;
    public bool MirrorToConsole = true;
}

/// <summary>
/// Persistent, rotating, thread-safe game log.
/// Normal entries are queued and written by a background thread; Error and Fatal entries are written and flushed
/// synchronously on the calling thread (together with the recent breadcrumb trail) so a crash cannot lose them.
/// </summary>
public static class Log
{
    struct Entry
    {
        public DateTime Time;
        public LogLevel Level;
        public long Tick;
        public int Correlation;
        public string Subsystem;
        public string Location;
        public string Message;
        public int ThreadId;
    }

    static LogConfig _cfg = new() { MirrorToConsole = false, MinFileLevel = LogLevel.Fatal + 1 };
    static readonly object _fileLock = new();
    static readonly ConcurrentQueue<Entry> _queue = new();
    static readonly AutoResetEvent _signal = new(false);
    static Entry[] _crumbs = new Entry[256];
    static int _crumbHead, _crumbCount;
    static readonly object _crumbLock = new();
    static StreamWriter _writer;
    static string _filePath;
    static long _fileBytes;
    static int _rollIndex;
    static Thread _thread;
    static volatile bool _running;
    static int _nextCorrelation;
    static readonly AsyncLocal<int> _correlation = new();

    public static bool Initialized { get; private set; }
    public static string SessionId { get; private set; } = "nosession";
    public static string BuildId { get; private set; } = ResolveBuildId();
    public static string CurrentFile => _filePath;
    public static LogConfig Config => _cfg;

    // ---- ambient game context (cheap volatile fields, set by the simulation) ----
    static long _tick = -1;
    public static long Tick { get => Interlocked.Read(ref _tick); set => Interlocked.Exchange(ref _tick, value); }
    public static volatile string MapId = "-";
    public static volatile string GameState = "boot";
    public static long WorldSeed = -1;
    static volatile string _lastAction = "-";
    static DateTime _lastActionTime;

    /// <summary>Optional sink for the console (e.g. Godot output). Must not call back into Log.</summary>
    public static Action<LogLevel, string> ConsoleSink;

    public static void Init(LogConfig cfg, string sessionId = null)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        Shutdown();
        _cfg = cfg;
        _crumbs = new Entry[Math.Max(16, cfg.BreadcrumbCapacity)];
        _crumbHead = _crumbCount = 0;
        SessionId = sessionId ?? Guid.NewGuid().ToString("N")[..8];
        System.IO.Directory.CreateDirectory(cfg.Directory);
        Prune(cfg);
        _rollIndex = 0;
        OpenFile();
        _running = true;
        _thread = new Thread(WriterLoop) { IsBackground = true, Name = "LogWriter", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Initialized = true;
        WriteHeader();
    }

    public static void Shutdown()
    {
        if (!Initialized) return;
        Initialized = false;
        _running = false;
        _signal.Set();
        _thread?.Join(2000);
        lock (_fileLock)
        {
            DrainLocked();
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Records the player or automated action that most recently happened (reported with errors).</summary>
    public static void Action(string action)
    {
        _lastAction = action;
        _lastActionTime = DateTime.Now;
        Write(LogLevel.Debug, "Input", "action: " + action, "", "", 0);
    }

    public static string LastAction => _lastAction;

    // ---------------------------------------------------------------- public API

    public static void Trace(string msg, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (_cfg.Verbose || _cfg.MinFileLevel <= LogLevel.Trace) Write(LogLevel.Trace, null, msg, file, member, line);
    }

    public static void Debug(string msg, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        => Write(LogLevel.Debug, null, msg, file, member, line);

    public static void Info(string msg, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        => Write(LogLevel.Info, null, msg, file, member, line);

    public static void Warn(string msg, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        => Write(LogLevel.Warning, null, msg, file, member, line);

    public static void Error(string msg, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        => Write(LogLevel.Error, null, msg + "\n" + CaptureStack(2), file, member, line);

    public static void Fatal(string msg, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        => Write(LogLevel.Fatal, null, msg + "\n" + CaptureStack(2), file, member, line);

    /// <summary>Logs an exception with type, message, full stack (including inner exceptions) and the context string.</summary>
    public static void Exception(Exception ex, string context, LogLevel level = LogLevel.Error,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        var sb = new StringBuilder(512);
        sb.Append(context).Append(" | ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
        if (ex.Data.Count > 0)
            foreach (System.Collections.DictionaryEntry kv in ex.Data) sb.Append(" | ").Append(kv.Key).Append('=').Append(kv.Value);
        sb.Append('\n').Append(ex);
        Write(level, null, sb.ToString(), file, member, line);
    }

    /// <summary>Explicit subsystem override (for messages bridged from the engine).</summary>
    public static void WriteRaw(LogLevel level, string subsystem, string msg, string location)
        => Write(level, subsystem, msg, location, "", 0, rawLocation: true);

    /// <summary>
    /// Times a block. Logs at Debug (verbose mode) with its duration, or at Warning when it exceeds warnMs.
    /// Usage: <c>using (Log.Time("MapGen", 200)) { ... }</c>
    /// </summary>
    public static TimeScope Time(string what, double warnMs = 100, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        => new(what, warnMs, file, member, line);

    public readonly struct TimeScope : IDisposable
    {
        readonly string _what, _file, _member;
        readonly int _line;
        readonly double _warn;
        readonly long _start;

        internal TimeScope(string what, double warn, string file, string member, int line)
        {
            _what = what; _warn = warn; _file = file; _member = member; _line = line;
            _start = Stopwatch.GetTimestamp();
        }

        public double ElapsedMs => (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;

        public void Dispose()
        {
            double ms = ElapsedMs;
            if (ms >= _warn) Write(LogLevel.Warning, null, $"SLOW {_what} took {ms:F1} ms (budget {_warn:F0} ms)", _file, _member, _line);
            else if (_cfg.Verbose) Write(LogLevel.Debug, null, $"{_what} took {ms:F2} ms", _file, _member, _line);
        }
    }

    /// <summary>
    /// Starts a correlation scope: every entry written on this logical flow carries the same id until disposed,
    /// so a multi-step operation (e.g. "load save") can be followed through several functions.
    /// </summary>
    public static CorrelationScope Correlate(string operation, [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        int id = Interlocked.Increment(ref _nextCorrelation);
        int prev = _correlation.Value;
        _correlation.Value = id;
        Write(LogLevel.Debug, null, $"begin {operation} (corr {id})", file, member, line);
        return new CorrelationScope(prev, operation);
    }

    public readonly struct CorrelationScope : IDisposable
    {
        readonly int _prev;
        readonly string _op;
        internal CorrelationScope(int prev, string op) { _prev = prev; _op = op; }
        public void Dispose()
        {
            Write(LogLevel.Debug, "Log", $"end {_op}", "", "", 0);
            _correlation.Value = _prev;
        }
    }

    public static int CurrentCorrelation => _correlation.Value;

    /// <summary>Flushes everything written so far to disk (blocking).</summary>
    public static void Flush()
    {
        if (!Initialized) return;
        lock (_fileLock)
        {
            DrainLocked();
            _writer?.Flush();
        }
    }

    /// <summary>Copy of the in-memory breadcrumb trail (oldest first), formatted.</summary>
    public static List<string> Breadcrumbs()
    {
        var list = new List<string>();
        lock (_crumbLock)
        {
            for (int i = 0; i < _crumbCount; i++)
            {
                int idx = (_crumbHead - _crumbCount + i + _crumbs.Length) % _crumbs.Length;
                list.Add(Format(_crumbs[idx]));
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- internals

    static void Write(LogLevel level, string subsystem, string msg, string file, string member, int line, bool rawLocation = false)
    {
        bool toFile = Initialized && (level >= _cfg.MinFileLevel || (_cfg.Verbose && level >= LogLevel.Debug));
        bool toCrumb = level >= _cfg.MinBreadcrumbLevel;
        bool toConsole = _cfg.MirrorToConsole && level >= _cfg.MinConsoleLevel;
        if (!toFile && !toCrumb && !toConsole) return;

        var e = new Entry
        {
            Time = DateTime.Now,
            Level = level,
            Tick = Tick,
            Correlation = _correlation.Value,
            Subsystem = subsystem ?? SubsystemOf(file),
            Location = rawLocation ? file : (line > 0 ? $"{Path.GetFileName(file)}:{line} {member}" : member),
            Message = msg,
            ThreadId = Environment.CurrentManagedThreadId,
        };

        string crumbsBefore = null;
        if (toCrumb)
        {
            lock (_crumbLock)
            {
                if (level >= LogLevel.Error) crumbsBefore = FormatCrumbsLocked(40);
                _crumbs[_crumbHead] = e;
                _crumbHead = (_crumbHead + 1) % _crumbs.Length;
                if (_crumbCount < _crumbs.Length) _crumbCount++;
            }
        }

        if (toConsole)
        {
            var sink = ConsoleSink;
            string line1 = $"[{LevelName(level)}] {e.Subsystem}: {FirstLine(msg)}";
            if (sink != null) sink(level, line1); else Console.WriteLine(line1);
        }

        if (!toFile) return;

        if (level >= LogLevel.Error)
        {
            // Synchronous, flushed write with surrounding context.
            var sb = new StringBuilder(1024);
            sb.Append(Format(e));
            sb.Append("\n    context: state=").Append(GameState).Append(" map=").Append(MapId)
              .Append(" seed=").Append(WorldSeed).Append(" lastAction=\"").Append(_lastAction).Append('"');
            if (_lastActionTime != default) sb.Append(" (").Append((DateTime.Now - _lastActionTime).TotalSeconds.ToString("F1")).Append(" s ago)");
            if (!string.IsNullOrEmpty(crumbsBefore))
                sb.Append("\n    ---- recent events before this error ----\n").Append(crumbsBefore).Append("    ---- end of recent events ----");
            lock (_fileLock)
            {
                DrainLocked();
                WriteLineLocked(sb.ToString());
                _writer?.Flush();
                _writer?.BaseStream.Flush();
                if (_writer?.BaseStream is FileStream fs) fs.Flush(true);
            }
        }
        else
        {
            _queue.Enqueue(e);
            _signal.Set();
        }
    }

    static string FormatCrumbsLocked(int max)
    {
        var sb = new StringBuilder();
        int n = Math.Min(max, _crumbCount);
        for (int i = 0; i < n; i++)
        {
            int idx = (_crumbHead - n + i + _crumbs.Length) % _crumbs.Length;
            sb.Append("    | ").Append(FirstLine(Format(_crumbs[idx]))).Append('\n');
        }
        return sb.ToString();
    }

    static void WriterLoop()
    {
        while (_running)
        {
            _signal.WaitOne(250);
            lock (_fileLock)
            {
                DrainLocked();
                _writer?.Flush();
            }
        }
    }

    static void DrainLocked()
    {
        while (_queue.TryDequeue(out var e)) WriteLineLocked(Format(e));
    }

    static void WriteLineLocked(string text)
    {
        if (_writer == null) return;
        _writer.WriteLine(text);
        _fileBytes += text.Length + 2;
        if (_fileBytes > _cfg.MaxFileBytes) Roll();
    }

    static void Roll()
    {
        _writer.WriteLine("---- log rolled to next file ----");
        _writer.Flush();
        _writer.Dispose();
        _rollIndex++;
        OpenFile();
        WriteLineLocked($"---- continued from previous file (session {SessionId}, part {_rollIndex}) ----");
        Prune(_cfg);
    }

    static void OpenFile()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _filePath = Path.Combine(_cfg.Directory, $"{_cfg.FilePrefix}_{stamp}_{SessionId}{(_rollIndex > 0 ? "_part" + _rollIndex : "")}.log");
        var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, new UTF8Encoding(false), 64 * 1024) { AutoFlush = false };
        _fileBytes = fs.Length;
    }

    /// <summary>Deletes log files older than MaxAgeDays and keeps at most MaxFiles of them.</summary>
    static void Prune(LogConfig cfg)
    {
        var files = new DirectoryInfo(cfg.Directory).GetFiles(cfg.FilePrefix + "_*.log");
        Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        var cutoff = DateTime.UtcNow.AddDays(-cfg.MaxAgeDays);
        for (int i = 0; i < files.Length; i++)
        {
            if (files[i].FullName == (_filePath == null ? null : Path.GetFullPath(_filePath))) continue;
            if (i >= cfg.MaxFiles || files[i].LastWriteTimeUtc < cutoff)
            {
                try { files[i].Delete(); }
                catch (IOException ex) { Console.WriteLine($"[log] could not prune {files[i].Name}: {ex.Message}"); }
            }
        }
    }

    static void WriteHeader()
    {
        var p = Process.GetCurrentProcess();
        Write(LogLevel.Info, "Log",
            $"==== session {SessionId} started | build {BuildId} | {Environment.OSVersion} | .NET {Environment.Version} | " +
            $"{Environment.ProcessorCount} cpus | pid {p.Id} | file {_filePath} | verbose={_cfg.Verbose}", "", "", 0);
    }

    static string Format(in Entry e)
    {
        var sb = new StringBuilder(160 + (e.Message?.Length ?? 0));
        sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" | ")
          .Append(LevelName(e.Level)).Append(" | ")
          .Append(BuildId).Append(" | s:").Append(SessionId)
          .Append(" | t:").Append(e.Tick < 0 ? "-" : e.Tick.ToString())
          .Append(" | map:").Append(MapId)
          .Append(" | c:").Append(e.Correlation)
          .Append(" | th:").Append(e.ThreadId)
          .Append(" | ").Append(e.Subsystem)
          .Append(" | ").Append(e.Location)
          .Append(" | ").Append(e.Message);
        return sb.ToString();
    }

    static string LevelName(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "FATAL",
    };

    static string FirstLine(string s)
    {
        if (s == null) return "";
        int i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    /// <summary>"…/Remade.Sim/World/Climate.cs" -> "World.Climate"</summary>
    static string SubsystemOf(string file)
    {
        if (string.IsNullOrEmpty(file)) return "-";
        string name = Path.GetFileNameWithoutExtension(file);
        string dir = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
        return string.IsNullOrEmpty(dir) ? name : dir + "." + name;
    }

    static string CaptureStack(int skip) => new StackTrace(skip, true).ToString();

    static string ResolveBuildId()
    {
        var asm = typeof(Log).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return info ?? asm.GetName().Version?.ToString() ?? "unknown";
    }
}
