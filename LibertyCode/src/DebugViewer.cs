using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace LibertyCode;

/// <summary>
/// Debug window. The main app starts a second copy of itself (`LibertyCode --debug-viewer &lt;file&gt;`) in a new
/// console window (cmd.exe on Windows). That copy only follows debug.log and prints it with colours, in real time.
/// </summary>
internal static class DebugViewer
{
    // ══ Launcher (main app side) ══════════════════════════════════════════════
    private static Process? _proc;

    public static bool Launch(string file)
    {
        try
        {
            if (_proc is { HasExited: false }) return true; // window already open

            string self = Environment.ProcessPath ?? "";
            if (self.Length == 0) return false;
            var cmd = new List<string> { self };
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // started with `dotnet run` / `dotnet LibertyCode.dll`
                string dll = Path.Combine(AppContext.BaseDirectory, (Assembly.GetEntryAssembly()?.GetName().Name ?? "LibertyCode") + ".dll");
                if (!File.Exists(dll)) return false;
                cmd.Add(dll);
            }
            cmd.AddRange(["--debug-viewer", file, "--pid", Environment.ProcessId.ToString()]);

            if (OperatingSystem.IsWindows())
            {
                // New console window running cmd.exe. The doubled quotes are the form cmd /c expects.
                string line = string.Join(" ", cmd.Select(a => "\"" + a + "\""));
                _proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{line}\"")
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                });
                return _proc != null;
            }

            if (OperatingSystem.IsMacOS())
            {
                string sh = string.Join(" ", cmd.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
                string osa = sh.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"tell application \"Terminal\" to do script \"{osa}\"");
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("tell application \"Terminal\" to activate");
                _proc = Process.Start(psi);
                return _proc != null;
            }

            // Linux: first terminal emulator that exists
            foreach (var (term, flag) in new[] { ("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e"), ("xfce4-terminal", "-x"), ("xterm", "-e") })
            {
                try
                {
                    var psi = new ProcessStartInfo(term) { UseShellExecute = false };
                    psi.ArgumentList.Add(flag);
                    foreach (var a in cmd) psi.ArgumentList.Add(a);
                    _proc = Process.Start(psi);
                    if (_proc != null) return true;
                }
                catch { /* try the next one */ }
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Trace("ERROR", "Could not open the debug window: " + ex.Message);
            return false;
        }
    }

    /// <summary>Closes the debug window (called when LibertyCode exits).</summary>
    public static void CloseWindow()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
    }

    // ══ Viewer (new window side) ══════════════════════════════════════════════
    private static readonly StringBuilder Head = new();
    private static int _lineLen;                 // characters of the current line seen so far
    private static ConsoleColor _color = ConsoleColor.Gray;

    public static void Run(string file, int parentPid)
    {
        try { RunCore(file, parentPid); }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Debug window error: " + ex);
            Console.ResetColor();
            Console.WriteLine("Press any key to close.");
            try { Console.ReadKey(true); } catch { }
        }
    }

    private static void RunCore(string file, int parentPid)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        try { Console.Title = "LibertyCode"; } catch { }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("LibertyCode  —  Debug Console");
        Console.WriteLine("==============================");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Live log: " + file);
        Console.WriteLine("Turn it off with /settings set debug false (this window closes by itself).");
        Console.WriteLine();
        Console.ResetColor();

        // Wait for the file (it is created just before this window opens).
        for (int i = 0; i < 50 && !File.Exists(file); i++) Thread.Sleep(100);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = Encoding.UTF8.GetDecoder();
        var buf = new byte[16384];
        var chars = new char[16384];
        bool parentGone = false;

        while (true)
        {
            if (fs.Length < fs.Position) fs.Position = 0; // file recreated (debug restarted)
            int n = fs.Read(buf, 0, buf.Length);
            if (n > 0)
            {
                int c = decoder.GetChars(buf, 0, n, chars, 0);
                string text = new(chars, 0, c);
                bool close = text.Contains(Logger.CloseMarker);
                if (close) text = text.Replace(Logger.CloseMarker + "\n", "").Replace(Logger.CloseMarker, "");
                Feed(text);
                if (close)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("-- debug mode off, closing --");
                    Console.ResetColor();
                    Thread.Sleep(900);
                    return;
                }
                continue;
            }

            if (!parentGone && !IsAlive(parentPid))
            {
                parentGone = true;
                Settle();
                return; // LibertyCode has exited: close this window too
            }
            // If the parent is still alive but the user has this window focused (input available),
            // don't auto-close — let them read; they can close it manually.

            Settle();
            Thread.Sleep(60);
        }
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return true;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // A log line starts with "HH:mm:ss.fff [LEVEL] ". The level (chars 14..18) decides the colour of the line;
    // lines that do not start like that (stack traces, AI text on several lines) keep the previous colour.
    private const string Pattern = "dd:dd:dd.ddd [";
    private const int PrefixLen = 20;

    private static void Feed(string s)
    {
        foreach (char ch in s)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                if (Head.Length > 0) Flush(decide: true);
                Console.Write('\n');
                _lineLen = 0;
                continue;
            }
            if (_lineLen < PrefixLen)
            {
                Head.Append(ch);
                _lineLen++;
                if (_lineLen == PrefixLen) Flush(decide: true);
            }
            else Console.Write(ch);
        }
        Settle();
    }

    /// <summary>If the buffered start of a line can no longer be a timestamp prefix, print it now (keeps streaming responsive).</summary>
    private static void Settle()
    {
        if (Head.Length == 0) return;
        if (!CouldBePrefix(Head.ToString()))
        {
            Console.Write(Head.ToString());
            Head.Clear();
            _lineLen = PrefixLen;
        }
    }

    private static bool CouldBePrefix(string h)
    {
        for (int i = 0; i < h.Length && i < Pattern.Length; i++)
        {
            char p = Pattern[i];
            if (p == 'd' ? !char.IsDigit(h[i]) : h[i] != p) return false;
        }
        return true;
    }

    private static void Flush(bool decide)
    {
        string h = Head.ToString();
        Head.Clear();
        if (decide && h.Length >= PrefixLen && CouldBePrefix(h) && h[19] == ']')
            _color = ColorFor(h.Substring(14, 5).Trim());
        Console.ForegroundColor = _color;
        Console.Write(h);
    }

    private static ConsoleColor ColorFor(string level) => level switch
    {
        "ERROR" => ConsoleColor.Red,
        "STACK" => ConsoleColor.DarkRed,
        "WARN"  => ConsoleColor.Yellow,
        "REQ"   => ConsoleColor.Cyan,
        "RES"   => ConsoleColor.DarkCyan,
        "AI"    => ConsoleColor.Green,
        "TOOL"  => ConsoleColor.Magenta,
        "STEP"  => ConsoleColor.Blue,
        "USER"  => ConsoleColor.White,
        "MODEL" => ConsoleColor.DarkYellow,
        "DEBUG" => ConsoleColor.DarkGray,
        _       => ConsoleColor.Gray,
    };
}
