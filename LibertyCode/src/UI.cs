using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace LibertyCode;

/// <summary>All terminal rendering. Palette lives in Theme.</summary>
internal static class UI
{
    public static readonly object Gate = new();

    // ── Console init ──────────────────────────────────────────────────────────
    public static void Init()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        SetTitle();
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
        {
            var handle = GetStdHandle(-11);
            if (GetConsoleMode(handle, out uint mode)) SetConsoleMode(handle, mode | 0x0004);
        }
    }

    /// <summary>Window title: "LibertyCode" instead of the path of the .exe.</summary>
    public static void SetTitle() { try { Console.Title = "LibertyCode"; } catch { } }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    public static int Width
    {
        get
        {
            try { return Math.Clamp(AnsiConsole.Profile.Width - 1, 40, 140); }
            catch { return 80; }
        }
    }

    // ── Markup helpers ────────────────────────────────────────────────────────
    public static string M(string color, string text) => $"[{color}]{Markup.Escape(text)}[/]";
    public static string Dim(string text) => M(Theme.Dim, text);
    public static string Bold(string text) => $"[bold]{Markup.Escape(text)}[/]";
    public static string Violet(string text) => M(Theme.Violet, text);

    public static void Line(string markup = "") => AnsiConsole.MarkupLine(markup);
    public static void Blank() => AnsiConsole.WriteLine();

    public static void Ok(string msg)   => Line($"  {M(Theme.Green, Sym.Check)} {Markup.Escape(Loc.T(msg))}");
    public static void Warn(string msg) { Line($"  {M(Theme.Yellow, "!")} {M(Theme.Yellow, Loc.T(msg))}"); Logger.Warn(msg); }
    public static void Error(string msg) { Line($"  {M(Theme.Red, Sym.Cross)} {M(Theme.Red, Loc.T(msg))}"); Logger.Error(msg); }
    public static void Hint(string msg) => Line($"  {Dim(Loc.T(msg))}");

    public static string Short(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            path = "~" + path[home.Length..];
        return path;
    }

    public static string SecurityName(PermissionMode m) => m switch
    {
        PermissionMode.Ask => "Strict",
        PermissionMode.Edits => "Mid",
        _ => "Off",
    };

    public static string SecurityHint(PermissionMode m) => m switch
    {
        PermissionMode.Ask => Loc.T("asks before edits and commands"),
        PermissionMode.Edits => Loc.T("applies edits, asks before commands"),
        _ => Loc.T("never asks (dangerous commands still ask)"),
    };

    public static string ModeColor(PermissionMode m) => m switch
    {
        PermissionMode.Ask => Theme.Green,
        PermissionMode.Edits => Theme.Yellow,
        _ => Theme.Red,
    };

    // ── Banner ────────────────────────────────────────────────────────────────
    public const string HubLabel = "/project";

    public static void Banner(AppSettings s, PermissionMode mode, string workspace, bool hub = false)
    {
        AnsiConsole.Clear();
        SetTitle();
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        grid.AddRow(Dim(Loc.T("Model:")),    $"{Violet(s.Model)} {Dim("· " + Providers.ShortLabel(s.Provider))}");
        grid.AddRow(Dim(Loc.T("Current directory:")), hub ? M(Theme.Orange, HubLabel) : Markup.Escape(Short(workspace)));
        grid.AddRow(Dim(Loc.T("Security:")), M(ModeColor(mode), SecurityName(mode)));

        var panel = new Panel(grid)
        {
            Border = Sym.Unicode ? BoxBorder.Rounded : BoxBorder.Square,
            BorderStyle = new Style(Theme.OrangeColor),
            Header = new PanelHeader($" [bold {Theme.Orange}]{Sym.Diamond} Liberty[/][bold {Theme.Violet}]Code[/] "),
            Padding = new Padding(1, 0, 2, 0),
            Expand = false,
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        Line($" {Dim(Loc.T("/help for commands  ·  esc to interrupt  ·  ctrl+c twice to exit"))}");
        Blank();
    }

    private static readonly string[] Logo =
    [
        @"$$\       $$\ $$\                            $$\                      $$$$$$\                  $$\           ",
        @"$$ |      \__|$$ |                           $$ |                    $$  __$$\                 $$ |          ",
        @"$$ |      $$\ $$$$$$$\   $$$$$$\   $$$$$$\ $$$$$$\   $$\   $$\       $$ /  \__| $$$$$$\   $$$$$$$ | $$$$$$\  ",
        @"$$ |      $$ |$$  __$$\ $$  __$$\ $$  __$$\\_$$  _|  $$ |  $$ |      $$ |      $$  __$$\ $$  __$$ |$$  __$$\ ",
        @"$$ |      $$ |$$ |  $$ |$$$$$$$$ |$$ |  \__| $$ |    $$ |  $$ |      $$ |      $$ /  $$ |$$ /  $$ |$$$$$$$$ |",
        @"$$ |      $$ |$$ |  $$ |$$   ____|$$ |       $$ |$$\ $$ |  $$ |      $$ |  $$\ $$ |  $$ |$$ |  $$ |$$   ____|",
        @"$$$$$$$$\ $$ |$$$$$$$  |\$$$$$$$\ $$ |       \$$$$  |\$$$$$$$ |      \$$$$$$  |\$$$$$$  |\$$$$$$$ |\$$$$$$$\ ",
        @"\________|\__|\_______/  \_______|\__|        \____/  \____$$ |       \______/  \______/  \_______| \_______|",
        @"                                                     $$\   $$ |                                              ",
        @"                                                     \$$$$$$  |                                              ",
        @"                                                      \______/                                               ",
    ];

    /// <summary>The original ASCII logo, shown on first run when the terminal is wide enough.</summary>
    public static void BigLogo()
    {
        if (Width < 112) return;
        AnsiConsole.WriteLine();
        foreach (var line in Logo)
        {
            const int split = 63;
            string l = line.Length > split ? line[..split] : line;
            string r = line.Length > split ? line[split..] : "";
            Line($"  [{Theme.Orange}]{Markup.Escape(l)}[/][{Theme.Violet}]{Markup.Escape(r)}[/]");
        }
        AnsiConsole.WriteLine();
    }

    // ── Prompt + status ───────────────────────────────────────────────────────
    public static void StatusLine(AppSettings s, PermissionMode mode, string workspace, int ctxTokens, bool hub = false)
    {
        string folder = hub ? HubLabel : new DirectoryInfo(workspace).Name;
        if (string.IsNullOrEmpty(folder)) folder = workspace;
        string info = $"{folder} · {s.Model} · {SecurityName(mode)}";
        if (ctxTokens > 0) info += $" · {FormatTokens(ctxTokens)} ctx";
        int w = Width;
        int bar = Math.Max(4, w - info.Length - 3);
        Line($"[{Theme.Faint}]{new string('─', bar)}[/] {Dim(info)}");
    }

    public static string FormatTokens(int n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    public static string PromptMarkup => $"[bold {Theme.Orange}]{Sym.Pointer}[/] ";
    public const int PromptLength = 2;

    // ── Tool rendering ────────────────────────────────────────────────────────
    public static void ToolHeader(string name, string arg)
    {
        int room = Math.Max(10, Width - name.Length - 8);
        if (arg.Length > room) arg = arg[..(room - 1)] + "…";
        Line($"{M(Theme.Orange, Sym.Diamond)} [bold]{Markup.Escape(name)}[/]{Dim("(")}{Markup.Escape(arg)}{Dim(")")}");
    }

    /// <summary>A result line under a tool header, markup already prepared.</summary>
    public static void ToolLine(string markup) => Line($"  {Dim(Sym.Branch)}  {markup}");

    public static void ToolError(string plain) => ToolLine($"{M(Theme.Red, Sym.Cross)} {M(Theme.Red, plain)}");

    public static void Lines(IEnumerable<string> markupLines)
    {
        foreach (var l in markupLines) Line(l);
    }
}

// ── Spinner ───────────────────────────────────────────────────────────────────
internal sealed class Spinner : IDisposable
{
    private static readonly string[] Frames = Sym.Frames;
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTime _t0 = DateTime.UtcNow;
    private readonly Task? _task;
    private readonly bool _live = !Console.IsOutputRedirected;
    private bool _stopped;
    private volatile string _label = "";

    public void Set(string label) => _label = label;

    public Spinner(string label)
    {
        _label = Loc.T(label);
        if (!_live) return;
        Console.Write("\x1b[?25l");
        _task = Task.Run(() =>
        {
            int i = 0;
            while (!_cts.IsCancellationRequested)
            {
                lock (UI.Gate)
                {
                    if (_stopped) return;
                    double s = (DateTime.UtcNow - _t0).TotalSeconds;
                    Console.Write($"\r\x1b[2K\x1b[38;2;255;159;28m{Frames[i++ % Frames.Length]}\x1b[0m \x1b[38;2;139;148;158m{_label}… {s:0}s · {Loc.T("esc to interrupt")}\x1b[0m");
                }
                try { Task.Delay(90, _cts.Token).Wait(); } catch { }
            }
        });
    }

    public void Stop()
    {
        lock (UI.Gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        _cts.Cancel();
        try { _task?.Wait(300); } catch { }
        if (_live) Console.Write("\r\x1b[2K\x1b[?25h");
    }

    public void Dispose() => Stop();
}

// ── Esc / interrupt watcher ───────────────────────────────────────────────────
internal sealed class EscWatcher : IDisposable
{
    private static EscWatcher? _current;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _task;
    private bool _paused;

    public EscWatcher(CancellationTokenSource target)
    {
        _current = this;
        if (Console.IsInputRedirected) return;
        _task = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                lock (_gate)
                {
                    try
                    {
                        if (!_paused && Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true);
                            if (k.Key == ConsoleKey.Escape) { try { target.Cancel(); } catch { } }
                        }
                    }
                    catch { }
                }
                try { await Task.Delay(40, _stop.Token); } catch { }
            }
        });
    }

    public static void Pause()  { var c = _current; if (c != null) lock (c._gate) c._paused = true; }
    public static void Resume() { var c = _current; if (c != null) lock (c._gate) c._paused = false; }

    public void Dispose()
    {
        _stop.Cancel();
        try { _task?.Wait(200); } catch { }
        if (_current == this) _current = null;
    }
}

// ── Streaming markdown renderer ───────────────────────────────────────────────
internal sealed class MarkdownStream
{
    private static readonly Regex InlineRx = new(@"`([^`]+)`|\*\*([^*]+)\*\*", RegexOptions.Compiled);
    private readonly StringBuilder _buf = new();
    private bool _inCode, _started, _lastBlank;

    public void Feed(string delta)
    {
        _buf.Append(delta);
        string all = _buf.ToString();
        int start = 0, i;
        while ((i = all.IndexOf('\n', start)) >= 0)
        {
            RenderLine(all[start..i].TrimEnd('\r'));
            start = i + 1;
        }
        _buf.Clear();
        _buf.Append(all[start..]);
    }

    public void Flush()
    {
        if (_buf.Length > 0) RenderLine(_buf.ToString().TrimEnd('\r'));
        _buf.Clear();
        if (_inCode) { UI.Line($"  {UI.M(Theme.Faint, Sym.BoxBot)}"); _inCode = false; }
    }

    private string Prefix()
    {
        if (_started) return "  ";
        _started = true;
        return $"{UI.M(Theme.Orange, Sym.Diamond)} ";
    }

    private static string Inline(string t)
    {
        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in InlineRx.Matches(t))
        {
            sb.Append(Markup.Escape(t[pos..m.Index]));
            sb.Append(m.Groups[1].Success
                ? $"[{Theme.Code}]{Markup.Escape(m.Groups[1].Value)}[/]"
                : $"[bold]{Markup.Escape(m.Groups[2].Value)}[/]");
            pos = m.Index + m.Length;
        }
        sb.Append(Markup.Escape(t[pos..]));
        return sb.ToString();
    }

    private void RenderLine(string line)
    {
        string trimmed = line.TrimStart();

        if (trimmed.StartsWith("```"))
        {
            if (!_inCode)
            {
                _inCode = true;
                string lang = trimmed.Trim('`').Trim();
                UI.Line($"{Prefix()}{UI.M(Theme.Faint, Sym.BoxTop)}{(lang.Length > 0 ? " " + UI.Dim(lang) : "")}");
            }
            else
            {
                _inCode = false;
                UI.Line($"  {UI.M(Theme.Faint, Sym.BoxBot)}");
            }
            _lastBlank = false;
            return;
        }

        if (_inCode)
        {
            UI.Line($"  {UI.M(Theme.Faint, "│")} [{Theme.Code}]{Markup.Escape(line.Replace("\t", "    "))}[/]");
            return;
        }

        if (trimmed.Length == 0)
        {
            if (_started && !_lastBlank) UI.Blank();
            _lastBlank = true;
            return;
        }
        _lastBlank = false;

        var hm = Regex.Match(trimmed, @"^#{1,6}\s+(.*)$");
        if (hm.Success)
        {
            UI.Line($"{Prefix()}[bold {Theme.Orange}]{Markup.Escape(hm.Groups[1].Value)}[/]");
            return;
        }

        var lm = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
        if (lm.Success)
        {
            UI.Line($"{Prefix()}{lm.Groups[1].Value}{UI.M(Theme.Dim, "•")} {Inline(lm.Groups[2].Value)}");
            return;
        }

        if (trimmed.StartsWith("> "))
        {
            UI.Line($"{Prefix()}{UI.M(Theme.Faint, Sym.Quote)} {UI.Dim(trimmed[2..])}");
            return;
        }

        UI.Line($"{Prefix()}{line.Substring(0, line.Length - trimmed.Length)}{Inline(trimmed)}");
    }
}
