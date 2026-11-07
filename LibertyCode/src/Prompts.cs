using Spectre.Console;

namespace LibertyCode;

internal static class Prompts
{
    /// <summary>Arrow-key selection (with optional type-to-filter). Falls back to a numbered list when not interactive.</summary>
    public static T Choose<T>(string title, IReadOnlyList<T> items, Func<T, string> label, int pageSize = 12, bool search = false)
        where T : notnull
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive || Console.IsInputRedirected)
        {
            UI.Line($"  {UI.Bold(title)}");
            for (int i = 0; i < items.Count; i++) UI.Line($"    {UI.Dim((i + 1) + ".")} {Markup.Escape(label(items[i]))}");
            while (true)
            {
                Console.Write("  > ");
                string? line = Console.ReadLine();
                if (line == null) throw new OperationCanceledException();
                if (int.TryParse(line.Trim(), out int n) && n >= 1 && n <= items.Count) return items[n - 1];
            }
        }

        return ChooseBox(title, items, label, pageSize, search);
    }

    // ── Framed menu ───────────────────────────────────────────────────────────
    private static T ChooseBox<T>(string title, IReadOnlyList<T> items, Func<T, string> label, int pageSize, bool search)
        where T : notnull
    {
        title = Loc.T(title);
        var labels = items.Select(label).Select(Loc.T).ToList();
        bool u = Sym.Unicode;
        string tl = u ? "╭" : "+", tr = u ? "╮" : "+", bl = u ? "╰" : "+", br = u ? "╯" : "+";
        string h = u ? "─" : "-", v = u ? "│" : "|", ml = u ? "├" : "+", mr = u ? "┤" : "+";

        int maxW = Math.Max(30, Console.WindowWidth - 4);
        int width = Math.Clamp(Math.Max(labels.Max(l => l.Length) + 6, title.Length + 6), 34, maxW); // inner width
        int rows = Math.Min(pageSize, items.Count);
        int height = rows + 4;

        var filter = new System.Text.StringBuilder();
        var idx = Enumerable.Range(0, items.Count).ToList();
        int sel = 0, scroll = 0;

        string Fit(string t, int w) => t.Length <= w ? t.PadRight(w) : t[..Math.Max(0, w - 1)] + "…";
        string B(string c) => $"[{Theme.Orange}]{c}[/]";

        void Refilter()
        {
            string f = filter.ToString();
            idx = Enumerable.Range(0, items.Count)
                .Where(i => f.Length == 0 || labels[i].Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            sel = 0; scroll = 0;
        }

        void Draw(int y0)
        {
            var lines = new List<string>();
            string t = " " + Fit(title, width - 4).TrimEnd() + " ";
            lines.Add(B(tl + h) + $"[bold]{Markup.Escape(t)}[/]" + B(new string(h[0], Math.Max(0, width - 1 - t.Length)) + tr));
            if (sel < scroll) scroll = sel;
            if (sel >= scroll + rows) scroll = sel - rows + 1;
            for (int r = 0; r < rows; r++)
            {
                int k = scroll + r;
                string body;
                if (k < idx.Count)
                {
                    string text = Fit(labels[idx[k]], width - 4);
                    body = k == sel
                        ? $"[{Theme.Orange}]{Markup.Escape(Sym.Pointer + " " + text)}[/] "
                        : $"  {Markup.Escape(text)} ";
                }
                else body = new string(' ', width - 1);
                lines.Add(B(v) + " " + body + B(v));
            }
            lines.Add(B(ml + new string(h[0], width) + mr));
            string hint = search
                ? (filter.Length > 0 ? Loc.T("filter: {x}").Replace("{x}", filter.ToString()) : Loc.T("type to filter"))
                : Loc.T("↑↓ choose");
            hint += "  ·  " + Loc.T("Enter") + "  ·  Esc";
            lines.Add(B(v) + " " + $"[{Theme.Dim}]{Markup.Escape(Fit(hint, width - 2))}[/]" + " " + B(v));
            // bottom border replaces the hint row's closing: draw as the last line
            lines.Add(B(bl + new string(h[0], width) + br));

            for (int i = 0; i < lines.Count; i++)
            {
                Console.SetCursorPosition(0, y0 + i);
                AnsiConsole.Markup(lines[i]);
            }
        }

        int total = height + 1; // + bottom border line
        for (int i = 0; i < total; i++) Console.WriteLine();
        int top = Math.Max(0, Console.CursorTop - total);

        Console.TreatControlCAsInput = true;
        try
        {
            Console.CursorVisible = false;
            while (true)
            {
                Draw(top);
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    throw new OperationCanceledException();
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:   if (idx.Count > 0) sel = (sel - 1 + idx.Count) % idx.Count; break;
                    case ConsoleKey.DownArrow: if (idx.Count > 0) sel = (sel + 1) % idx.Count; break;
                    case ConsoleKey.PageUp:    sel = Math.Max(0, sel - rows); break;
                    case ConsoleKey.PageDown:  sel = Math.Min(Math.Max(0, idx.Count - 1), sel + rows); break;
                    case ConsoleKey.Home:      sel = 0; break;
                    case ConsoleKey.End:       sel = Math.Max(0, idx.Count - 1); break;
                    case ConsoleKey.Enter:
                        if (idx.Count == 0) break;
                        Clear(top, total);
                        return items[idx[sel]];
                    case ConsoleKey.Escape:
                        if (filter.Length > 0) { filter.Clear(); Refilter(); break; }
                        Clear(top, total);
                        throw new OperationCanceledException();
                    case ConsoleKey.Backspace:
                        if (search && filter.Length > 0) { filter.Length--; Refilter(); }
                        break;
                    default:
                        if (search && !char.IsControl(key.KeyChar)) { filter.Append(key.KeyChar); Refilter(); }
                        break;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
            Console.TreatControlCAsInput = false;
        }
    }

    private static void Clear(int top, int total)
    {
        int w = Math.Max(1, Console.WindowWidth - 1);
        for (int i = 0; i < total; i++)
        {
            Console.SetCursorPosition(0, top + i);
            Console.Write(new string(' ', w));
        }
        Console.SetCursorPosition(0, top);
    }
}
