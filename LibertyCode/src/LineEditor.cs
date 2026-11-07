using System.Text;
using Spectre.Console;

namespace LibertyCode;

/// <summary>Single-line input with history, cursor editing and Tab completion for slash commands.</summary>
internal static class LineEditor
{
    private static readonly List<string> History = [];
    private static DateTime _lastCtrlC = DateTime.MinValue;

    public static void LoadHistory()
    {
        try { if (File.Exists(Paths.HistoryFile)) History.AddRange(File.ReadAllLines(Paths.HistoryFile).TakeLast(200)); } catch { }
    }

    private static void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || (History.Count > 0 && History[^1] == line)) return;
        History.Add(line);
        try { File.WriteAllLines(Paths.HistoryFile, History.TakeLast(200)); } catch { }
    }

    /// <summary>Reads a possibly multi-line input (a trailing backslash continues on the next line). Null = exit.</summary>
    public static string? ReadInput(string promptMarkup, int promptLen, IReadOnlyList<string> completions)
    {
        var sb = new StringBuilder();
        string markup = promptMarkup;
        int len = promptLen;
        while (true)
        {
            string? line = Read(markup, len, completions, secret: false, remember: false);
            if (line == null) return null;
            if (line.EndsWith('\\'))
            {
                sb.AppendLine(line[..^1]);
                markup = $"[{Theme.Faint}]·[/] ";
                len = 2;
                continue;
            }
            sb.Append(line);
            break;
        }
        string result = sb.ToString();
        Remember(result.ReplaceLineEndings(" ⏎ "));
        return result;
    }

    /// <summary>Set by Read(secret: true): "invalid" (characters refused) or "long" (over the limit), else null.</summary>
    public static string? SecretIssue { get; private set; }
    /// <summary>Model names known for the current provider (Tab completion of "/models xxx").</summary>
    public static IReadOnlyList<string> ModelNames { get; set; } = [];
    private const int MaxSecret = 300;

    public static string? Read(string promptMarkup, int promptLen, IReadOnlyList<string>? completions = null,
        bool secret = false, bool remember = false)
    {
        if (Console.IsInputRedirected)
        {
            AnsiConsole.Markup(promptMarkup);
            return Console.ReadLine();
        }

        AnsiConsole.Markup(promptMarkup);
        if (secret) SecretIssue = null;
        var buf = new StringBuilder();
        int pos = 0;
        int histIdx = History.Count;
        string draft = "";
        int startLeft = Console.CursorLeft, startTop = Console.CursorTop;
        Console.TreatControlCAsInput = true;

        void Redraw()
        {
            try
            {
                int width = Math.Max(10, Console.WindowWidth);
                Console.CursorVisible = false;
                Console.SetCursorPosition(startLeft, startTop);
                if (secret)
                {
                    if (buf.Length == 0) SecretIssue = null;
                    // Never wrap: show at most one line of dots, however long the pasted text is.
                    int shown = Math.Min(buf.Length, Math.Max(1, width - startLeft - 2));
                    Console.Write(new string('•', shown));
                    Console.Write("\x1b[K");
                    Console.SetCursorPosition(Math.Min(startLeft + Math.Min(pos, shown), width - 1), startTop);
                    Console.CursorVisible = true;
                    return;
                }
                Console.Write(buf.ToString());
                Console.Write("\x1b[J");
                int rowsDown = (startLeft + buf.Length) / width;
                startTop = Console.CursorTop - rowsDown;
                int abs = startLeft + pos;
                Console.SetCursorPosition(abs % width, startTop + abs / width);
                Console.CursorVisible = true;
            }
            catch { /* cursor math can fail on exotic terminals; typing still works */ }
        }

        try
        {
            while (true)
            {
                var k = Console.ReadKey(true);
                // AltGr is reported as Ctrl+Alt on Windows: it must NOT count as Ctrl, otherwise characters
                // typed with AltGr (\ @ # { } [ ] | ~ and accented letters on many layouts) are silently dropped.
                bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;
                bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0 && !alt;

                if (k.Key == ConsoleKey.Enter)
                {
                    pos = buf.Length; Redraw();
                    Console.WriteLine();
                    string r = buf.ToString();
                    if (remember) Remember(r);
                    return r;
                }
                if (ctrl && k.Key == ConsoleKey.C)
                {
                    if (buf.Length > 0) { buf.Clear(); pos = 0; Redraw(); continue; }
                    if ((DateTime.UtcNow - _lastCtrlC).TotalSeconds < 2) { Console.WriteLine(); return null; }
                    _lastCtrlC = DateTime.UtcNow;
                    Console.WriteLine();
                    UI.Hint("press ctrl+c again to exit");
                    AnsiConsole.Markup(promptMarkup);
                    startLeft = Console.CursorLeft; startTop = Console.CursorTop;
                    continue;
                }
                if (ctrl && k.Key == ConsoleKey.D && buf.Length == 0) { Console.WriteLine(); return null; }

                switch (k.Key)
                {
                    case ConsoleKey.Backspace:
                        if (ctrl) { DeleteWordBack(); }
                        else if (pos > 0) { buf.Remove(pos - 1, 1); pos--; }
                        break;
                    case ConsoleKey.Delete:
                        if (pos < buf.Length) buf.Remove(pos, 1);
                        break;
                    case ConsoleKey.LeftArrow:
                        pos = ctrl ? WordLeft() : Math.Max(0, pos - 1);
                        break;
                    case ConsoleKey.RightArrow:
                        pos = ctrl ? WordRight() : Math.Min(buf.Length, pos + 1);
                        break;
                    case ConsoleKey.Home: pos = 0; break;
                    case ConsoleKey.End: pos = buf.Length; break;
                    case ConsoleKey.Escape: buf.Clear(); pos = 0; break;
                    case ConsoleKey.UpArrow:
                        if (secret || History.Count == 0 || histIdx == 0) break;
                        if (histIdx == History.Count) draft = buf.ToString();
                        histIdx--;
                        buf.Clear().Append(History[histIdx].Replace(" ⏎ ", " ")); pos = buf.Length;
                        break;
                    case ConsoleKey.DownArrow:
                        if (secret || histIdx >= History.Count) break;
                        histIdx++;
                        buf.Clear().Append(histIdx == History.Count ? draft : History[histIdx].Replace(" ⏎ ", " ")); pos = buf.Length;
                        break;
                    case ConsoleKey.Tab:
                        if (!secret && buf.ToString().StartsWith("/models ", StringComparison.OrdinalIgnoreCase))
                        {
                            string part = buf.ToString()[8..].Trim();
                            var mm = ModelNames.Where(n => n.StartsWith(part, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (mm.Count == 0) mm = ModelNames.Where(n => n.Contains(part, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (mm.Count == 1) { buf.Clear().Append("/models ").Append(mm[0]); pos = buf.Length; }
                            else if (mm.Count > 1)
                            {
                                string common = mm[0];
                                foreach (var m in mm)
                                    while (!m.StartsWith(common, StringComparison.OrdinalIgnoreCase)) common = common[..^1];
                                if (common.Length >= part.Length) { buf.Clear().Append("/models ").Append(common); pos = buf.Length; }
                                Console.WriteLine();
                                UI.Line($"  {UI.Dim(string.Join("   ", mm.Take(12)))}");
                                AnsiConsole.Markup(promptMarkup);
                                startLeft = Console.CursorLeft; startTop = Console.CursorTop;
                            }
                        }
                        else if (completions != null && buf.Length > 0 && buf[0] == '/' && !buf.ToString().Contains(' '))
                        {
                            var matches = completions.Where(c => c.StartsWith(buf.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
                            if (matches.Count == 1) { buf.Clear().Append(matches[0]).Append(' '); pos = buf.Length; }
                            else if (matches.Count > 1)
                            {
                                string common = matches[0];
                                foreach (var m in matches)
                                    while (!m.StartsWith(common, StringComparison.OrdinalIgnoreCase)) common = common[..^1];
                                buf.Clear().Append(common); pos = buf.Length;
                                Console.WriteLine();
                                UI.Line($"  {UI.Dim(string.Join("   ", matches))}");
                                AnsiConsole.Markup(promptMarkup);
                                startLeft = Console.CursorLeft; startTop = Console.CursorTop;
                            }
                        }
                        break;
                    default:
                        if (ctrl)
                        {
                            switch (k.Key)
                            {
                                case ConsoleKey.A: pos = 0; break;
                                case ConsoleKey.E: pos = buf.Length; break;
                                case ConsoleKey.U: buf.Remove(0, pos); pos = 0; break;
                                case ConsoleKey.K: buf.Remove(pos, buf.Length - pos); break;
                                case ConsoleKey.W: DeleteWordBack(); break;
                            }
                        }
                        else if (secret)
                        {
                            char c = k.KeyChar;
                            if (c < '!' || c > '~') { if (!char.IsControl(c)) SecretIssue = "invalid"; }
                            else if (buf.Length >= MaxSecret) SecretIssue = "long";
                            else { buf.Insert(pos, c); pos++; }
                        }
                        else if (!char.IsControl(k.KeyChar))
                        {
                            buf.Insert(pos, k.KeyChar); pos++;
                        }
                        break;
                }
                Redraw();
            }
        }
        finally
        {
            Console.TreatControlCAsInput = false;
            try { Console.CursorVisible = true; } catch { }
        }

        void DeleteWordBack()
        {
            int p = WordLeft();
            buf.Remove(p, pos - p);
            pos = p;
        }
        int WordLeft()
        {
            int p = pos;
            while (p > 0 && buf[p - 1] == ' ') p--;
            while (p > 0 && buf[p - 1] != ' ') p--;
            return p;
        }
        int WordRight()
        {
            int p = pos;
            while (p < buf.Length && buf[p] == ' ') p++;
            while (p < buf.Length && buf[p] != ' ') p++;
            return p;
        }
    }
}
