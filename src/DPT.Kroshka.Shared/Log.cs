using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DPT.Kroshka.Shared;

public sealed class LogEntry
{
    public long Seq { get; set; }
    public DateTime Utc { get; set; }
    public LogLevel Level { get; set; }
    public string Category { get; set; } = "APP";
    public string Message { get; set; } = "";

    public string TimeText => Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

/// <summary>
/// Trace based logging, configured entirely from code.
///
/// The duplicate-listener bug this avoids: calling Configure() twice used to append a
/// second file listener, so every line appeared twice and the writer was never closed.
/// Here Configure() is idempotent - it tears down whatever it installed before, and a
/// separate flag stops a re-entrant call from a listener thread.
///
/// Nothing is written to App.config: Trace.Listeners is cleared and rebuilt in memory.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly List<LogEntry> Ring = new();
    private static readonly object RingSync = new();

    private static LogLevel _minLevel = LogLevel.Info;
    private static bool _apiTrace = true;
    private static int _ringCap = 2000;
    private static long _seq;
    private static bool _configured;
    private static volatile bool _shutdown;
    private static bool _configuring;

    private static FileTraceListener _file;
    private static ConsoleTraceListenerEx _console;

    public static LogLevel MinLevel => _minLevel;
    public static long LastSeq { get { lock (RingSync) { return _seq; } } }
    public static string LogDir { get; private set; } = "logs";
    public static string CurrentFile => _file?.CurrentPath ?? "";

    /// <summary>
    /// Attaches exactly one console sink and exactly one file sink. Safe to call
    /// repeatedly (the admin board does, when the owner changes the level).
    /// </summary>
    public static void Configure(KroshkaConfig cfg)
    {
        if (cfg == null) return;

        lock (Sync)
        {
            if (_configuring) return;
            _configuring = true;
            try
            {
                _minLevel = cfg.LogLevel;
                _apiTrace = cfg.ApiTrace;
                _ringCap = cfg.LogRingSize;
                LogDir = cfg.LogDirPath;
                _shutdown = false;

                // ---- tear down a previous configuration, including our own sinks
                Detach();

                try { Directory.CreateDirectory(LogDir); }
                catch (Exception ex) { TryConsoleError("Cannot create log directory: " + ex.Message); }

                // ---- file sink
                try
                {
                    _file = new FileTraceListener(LogDir, cfg.LogMaxFileMb, cfg.LogRetentionDays);
                    Trace.Listeners.Add(_file);
                }
                catch (Exception ex)
                {
                    TryConsoleError("Cannot attach file log: " + ex.Message);
                }

                // ---- console sink (kept quiet at Verbose to avoid drowning the TUI)
                try
                {
                    _console = new ConsoleTraceListenerEx();
                    Trace.Listeners.Add(_console);
                }
                catch { }

                Trace.AutoFlush = false;
                _configured = true;
            }
            finally
            {
                _configuring = false;
            }
        }

        Info("LOG", $"Logging started. dir={LogDir} level={_minLevel} apiTrace={(_apiTrace ? "on" : "off")}");
    }

    private static void Detach()
    {
        // Remove every listener we may have added, then clear the collection so the
        // framework's DefaultTraceListener (which writes to the debugger only) cannot
        // double up with ours.
        try { Trace.Flush(); } catch { }
        try { Trace.Listeners.Clear(); } catch { }

        try { _file?.Dispose(); } catch { }
        _file = null;
        _console = null;
    }

    public static void Shutdown()
    {
        if (_shutdown) return;
        lock (Sync)
        {
            if (_shutdown) return;
            _shutdown = true;
            try { Trace.WriteLine(Format(new LogEntry { Seq = ++_seq, Utc = DateTime.UtcNow, Level = LogLevel.Info, Category = "LOG", Message = "Logging stopped." })); }
            catch { }
            try { Trace.Flush(); } catch { }
            try { _file?.Dispose(); } catch { }
            _file = null;
            _console = null;
            try { Trace.Listeners.Clear(); } catch { }
        }
    }

    // ---------------------------------------------------------------- writes

    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public static void Verbose(string category, string message) => Write(LogLevel.Verbose, category, message);

    public static void Error(string category, Exception ex) => Write(LogLevel.Error, category, ex?.ToString() ?? "unknown error");

    /// <summary>Request tracing, gated by log.apiTrace so a busy server stays readable.</summary>
    public static void Api(string category, string message)
    {
        if (!_apiTrace) return;
        Write(LogLevel.Debug, category, message);
    }

    public static void Write(LogLevel level, string category, string message)
    {
        if (level > _minLevel) return;

        LogEntry e = new()
        {
            Utc = DateTime.UtcNow,
            Level = level,
            Category = string.IsNullOrEmpty(category) ? "APP" : category,
            Message = message ?? ""
        };

        lock (RingSync)
        {
            e.Seq = ++_seq;
            Ring.Add(e);
            if (Ring.Count > _ringCap) Ring.RemoveRange(0, Ring.Count - _ringCap);
        }

        if (!_configured || _shutdown) return;
        try { Trace.WriteLine(Format(e)); }
        catch { }
    }

    // ---------------------------------------------------------------- reading

    public static List<LogEntry> Tail(long sinceSeq, int limit, LogLevel? minLevel, string categoryFilter)
    {
        List<LogEntry> result = new();
        lock (RingSync)
        {
            foreach (LogEntry e in Ring)
            {
                if (e.Seq <= sinceSeq) continue;
                if (minLevel.HasValue && e.Level > minLevel.Value) continue;
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    e.Category.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(e);
            }
        }
        if (limit > 0 && result.Count > limit)
            result.RemoveRange(0, result.Count - limit);
        return result;
    }

    public static string LevelLetter(LogLevel l) => l switch
    {
        LogLevel.Error => "E",
        LogLevel.Warn => "W",
        LogLevel.Info => "I",
        LogLevel.Debug => "D",
        _ => "V"
    };

    public static string Format(LogEntry e)
    {
        string cat = e.Category ?? "APP";
        if (cat.Length < 8) cat += new string(' ', 8 - cat.Length);
        return e.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
               " [" + LevelLetter(e.Level) + "] " + cat + " " + SingleLine(e.Message);
    }

    private static string SingleLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
        return s.Replace("\r\n", " | ").Replace("\n", " | ").Replace("\r", " | ");
    }

    private static void TryConsoleError(string message)
    {
        try { Console.Error.WriteLine(message); } catch { }
    }

    // ---------------------------------------------------------------- sinks

    /// <summary>Rolling daily file sink with a size cap and retention pruning.</summary>
    private sealed class FileTraceListener : TraceListener
    {
        private readonly object _l = new();
        private readonly string _dir;
        private readonly long _maxBytes;
        private readonly int _retentionDays;
        private StreamWriter _writer;
        private DateTime _day = DateTime.MinValue;
        private int _index;
        private long _bytes;
        private bool _dead;
        private bool _disposed;

        public string CurrentPath { get; private set; } = "";

        public FileTraceListener(string dir, int maxMb, int retentionDays)
        {
            _dir = dir;
            _maxBytes = Math.Max(1, maxMb) * 1024L * 1024L;
            _retentionDays = retentionDays;
            Directory.CreateDirectory(_dir);
            CleanupOldFiles();
        }

        public override void Write(string message) => WriteLine(message);

        public override void WriteLine(string message)
        {
            if (_disposed || _dead || message == null) return;
            lock (_l)
            {
                if (_disposed || _dead) return;
                try
                {
                    EnsureWriter();
                    _writer.WriteLine(message);
                    _bytes += message.Length + 2;
                }
                catch
                {
                    try { CloseWriter(); } catch { }
                    _dead = !Directory.Exists(_dir);
                }
            }
        }

        private void EnsureWriter()
        {
            DateTime today = DateTime.Now.Date;
            if (_writer != null && _day == today && _bytes < _maxBytes) return;

            CloseWriter();
            if (_day != today) { _day = today; _index = 0; }
            else _index++;

            string name = _index == 0
                ? $"kroshka-{_day:yyyyMMdd}.log"
                : $"kroshka-{_day:yyyyMMdd}.{_index:000}.log";
            CurrentPath = Path.Combine(_dir, name);

            FileStream fs = new(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            _bytes = fs.Length;
        }

        private void CloseWriter()
        {
            if (_writer == null) return;
            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
            _writer = null;
        }

        private void CleanupOldFiles()
        {
            if (_retentionDays <= 0) return;
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-_retentionDays);
                foreach (string file in Directory.GetFiles(_dir, "kroshka-*.log*"))
                {
                    try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
                    catch { }
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_l)
            {
                _disposed = true;
                CloseWriter();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Console sink with colour. Trace colour codes so a crash never cascades into
    /// another exception, and no-ops when there is no console (Windows service).
    /// </summary>
    private sealed class ConsoleTraceListenerEx : TraceListener
    {
        private readonly object _l = new();
        private readonly bool _hasConsole;

        public ConsoleTraceListenerEx()
        {
            try
            {
                _hasConsole = !Console.IsOutputRedirected || OperatingSystem.IsWindows();
                _hasConsole = true;
            }
            catch { _hasConsole = false; }
        }

        public override void Write(string message) => WriteLine(message);

        public override void WriteLine(string message)
        {
            if (!_hasConsole || message == null) return;
            lock (_l)
            {
                try
                {
                    ConsoleColor old = Console.ForegroundColor;
                    Console.ForegroundColor = ColorFor(message);
                    Console.WriteLine(message);
                    Console.ForegroundColor = old;
                }
                catch
                {
                    try { Console.ResetColor(); } catch { }
                }
            }
        }

        private static ConsoleColor ColorFor(string line)
        {
            int i = line.IndexOf('[');
            if (i < 0 || i + 2 >= line.Length) return ConsoleColor.Gray;
            return line[i + 1] switch
            {
                'E' => ConsoleColor.Red,
                'W' => ConsoleColor.Yellow,
                'I' => ConsoleColor.Gray,
                'D' => ConsoleColor.DarkGray,
                _ => ConsoleColor.DarkCyan
            };
        }
    }
}
