using System.Text;

namespace LibertyCode;

/// <summary>
/// Two outputs:
///  - libertycode.log : short, always on (INFO / WARN / ERROR), one line per entry.
///  - debug.log       : only in Debug mode, everything (requests, responses, AI stream, tool steps, stack traces).
///                      A separate console window (DebugViewer) follows this file in real time.
/// </summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static StreamWriter? _dbg;
    private static bool _streamOpen; // an AI stream line is open (no trailing newline yet)

    /// <summary>Written on its own line to tell the debug window to close itself.</summary>
    public const string CloseMarker = "@@CLOSE@@";

    public static bool DebugEnabled => _dbg != null;

    // ── Normal log ────────────────────────────────────────────────────────────
    public static void Log(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message.ReplaceLineEndings(" ")}";
                File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
            }
        }
        catch { /* logging must never crash the app */ }
        Trace(level, message);
    }

    public static void Info(string msg)  => Log("INFO",  msg);
    public static void Warn(string msg)  => Log("WARN",  msg);
    public static void Error(string msg) => Log("ERROR", msg);

    /// <summary>Error with the full exception (type, message, stack) in the debug window.</summary>
    public static void Exception(string context, Exception ex)
    {
        Error($"{context}: {ex.GetType().Name}: {ex.Message}");
        Trace("STACK", ex.ToString());
    }

    public static string[] Tail(int lines = 20)
    {
        try { return File.Exists(Paths.LogFile) ? File.ReadAllLines(Paths.LogFile).TakeLast(lines).ToArray() : []; }
        catch { return []; }
    }

    // ── Debug mode ────────────────────────────────────────────────────────────
    /// <summary>Turns debug on (if needed) and makes sure the live window is open. Returns false if no window could be opened.</summary>
    public static bool StartDebug()
    {
        lock (Gate)
        {
            if (_dbg == null)
            {
                try
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    var fs = new FileStream(Paths.DebugFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _dbg = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                }
                catch { _dbg = null; return false; }
                _streamOpen = false;
                Write(Line("DEBUG", "Debug mode ON - LibertyCode " + (typeof(Logger).Assembly.GetName().Version?.ToString(3) ?? "?")
                    + $" | {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"
                    + $" | {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"));
                Write(Line("DEBUG", $"Data folder: {Paths.DataDir}"));
            }
        }
        return DebugViewer.Launch(Paths.DebugFile);
    }

    public static void StopDebug()
    {
        lock (Gate)
        {
            if (_dbg == null) return;
            CloseStreamLine();
            Write(Line("DEBUG", "Debug mode OFF"));
            Write(CloseMarker + "\n");
            try { _dbg.Dispose(); } catch { }
            _dbg = null;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_dbg == null) return;
            CloseStreamLine();
            Write(Line("INFO", "LibertyCode is exiting"));
        }
    }

    /// <summary>Debug-only entry. Multi-line messages are indented under their first line.</summary>
    public static void Trace(string level, string message)
    {
        if (_dbg == null) return;
        lock (Gate)
        {
            if (_dbg == null) return;
            CloseStreamLine();
            Write(Line(level, message));
        }
    }

    public static void Debug(string message) => Trace("DEBUG", message);

    /// <summary>Starts an open line (no newline) that StreamChunk fills in real time.</summary>
    public static void StreamChunk(string text)
    {
        if (_dbg == null || text.Length == 0) return;
        lock (Gate)
        {
            if (_dbg == null) return;
            if (!_streamOpen)
            {
                Write($"{DateTime.Now:HH:mm:ss.fff} [AI   ] ");
                _streamOpen = true;
            }
            Write(text.ReplaceLineEndings("\n"));
        }
    }

    public static void StreamEnd()
    {
        if (_dbg == null) return;
        lock (Gate) CloseStreamLine();
    }

    private static void CloseStreamLine()
    {
        if (!_streamOpen) return;
        Write("\n");
        _streamOpen = false;
    }

    private static string Line(string level, string message)
    {
        var parts = message.ReplaceLineEndings("\n").Split('\n');
        var sb = new StringBuilder();
        sb.Append($"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {parts[0]}\n");
        for (int i = 1; i < parts.Length; i++) sb.Append("             | ").Append(parts[i]).Append('\n');
        return sb.ToString();
    }

    private static void Write(string s)
    {
        try { _dbg?.Write(s); } catch { }
    }

    // ── Helpers for callers ───────────────────────────────────────────────────
    /// <summary>"sk-abcd...wxyz": enough to recognise a key, never enough to use it.</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(none)";
        return key.Length <= 10 ? "****" : key[..4] + "..." + key[^4..];
    }

    public static string MaskHeader(string name, string value)
    {
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            int i = value.IndexOf(' ');
            return i > 0 ? value[..(i + 1)] + Mask(value[(i + 1)..]) : Mask(value);
        }
        return name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ? Mask(value) : value;
    }

    public static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + $" ... (+{s.Length - max} chars)";
    }
}
