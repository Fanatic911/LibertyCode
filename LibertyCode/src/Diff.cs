using System.Text;
using Spectre.Console;

namespace LibertyCode;

internal enum DiffKind { Same, Add, Del }

internal readonly record struct DiffLine(DiffKind Kind, int OldNo, int NewNo, string Text);

/// <summary>Line diff (LCS) + rendering of the result as coloured markup lines.</summary>
internal static class Diff
{
    private const int MaxCells = 6_000_000;

    public static string[] SplitLines(string text)
    {
        text = text.Replace("\r\n", "\n");
        if (text.Length == 0) return [];
        var lines = text.Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    public static List<DiffLine> Compute(string[] a, string[] b)
    {
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;

        int n = a.Length - pre - suf, m = b.Length - pre - suf;
        var result = new List<DiffLine>(a.Length + b.Length);

        for (int i = 0; i < pre; i++) result.Add(new(DiffKind.Same, i + 1, i + 1, a[i]));

        int oi = pre, ni = pre; // 0-based cursors into a / b
        if (n > 0 || m > 0)
        {
            if ((long)(n + 1) * (m + 1) > MaxCells)
            {
                for (int i = 0; i < n; i++) result.Add(new(DiffKind.Del, ++oi, 0, a[pre + i]));
                for (int j = 0; j < m; j++) result.Add(new(DiffKind.Add, 0, ++ni, b[pre + j]));
            }
            else
            {
                int w = m + 1;
                var l = new int[(n + 1) * w];
                for (int i = n - 1; i >= 0; i--)
                    for (int j = m - 1; j >= 0; j--)
                        l[i * w + j] = a[pre + i] == b[pre + j]
                            ? l[(i + 1) * w + j + 1] + 1
                            : Math.Max(l[(i + 1) * w + j], l[i * w + j + 1]);

                int x = 0, y = 0;
                while (x < n && y < m)
                {
                    if (a[pre + x] == b[pre + y]) { result.Add(new(DiffKind.Same, ++oi, ++ni, a[pre + x])); x++; y++; }
                    else if (l[(x + 1) * w + y] >= l[x * w + y + 1]) { result.Add(new(DiffKind.Del, ++oi, 0, a[pre + x])); x++; }
                    else { result.Add(new(DiffKind.Add, 0, ++ni, b[pre + y])); y++; }
                }
                while (x < n) { result.Add(new(DiffKind.Del, ++oi, 0, a[pre + x])); x++; }
                while (y < m) { result.Add(new(DiffKind.Add, 0, ++ni, b[pre + y])); y++; }
            }
        }

        for (int i = 0; i < suf; i++)
            result.Add(new(DiffKind.Same, ++oi, ++ni, a[a.Length - suf + i]));
        return result;
    }

    public static (int Added, int Removed) Count(List<DiffLine> d) =>
        (d.Count(x => x.Kind == DiffKind.Add), d.Count(x => x.Kind == DiffKind.Del));

    // ── Rendering ─────────────────────────────────────────────────────────────
    private const string Indent = "     ";

    private static string Fit(string s, int max)
    {
        s = s.Replace("\t", "    ").Replace("\r", "");
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (!char.IsControl(c)) sb.Append(c);
        s = sb.ToString();
        return s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
    }

    private static string Row(DiffKind kind, int no, string text, int numW, int width)
    {
        int room = Math.Max(10, width - Indent.Length - numW - 4);
        string body = Fit(text, room);
        string num = no > 0 ? no.ToString().PadLeft(numW) : new string(' ', numW);
        switch (kind)
        {
            case DiffKind.Add:
            {
                string raw = $" {num} + {body}".PadRight(width - Indent.Length);
                return $"{Indent}[{Theme.AddFg} on {Theme.AddBg}]{Markup.Escape(raw)}[/]";
            }
            case DiffKind.Del:
            {
                string raw = $" {num} - {body}".PadRight(width - Indent.Length);
                return $"{Indent}[{Theme.DelFg} on {Theme.DelBg}]{Markup.Escape(raw)}[/]";
            }
            default:
                return $"{Indent}[{Theme.Dim}]{Markup.Escape($" {num}   {body}")}[/]";
        }
    }

    /// <summary>Renders changed hunks with 3 lines of context.</summary>
    public static List<string> Render(List<DiffLine> diff, int maxLines = 48, int context = 3)
    {
        int width = UI.Width;
        var keep = new bool[diff.Count];
        for (int i = 0; i < diff.Count; i++)
        {
            if (diff[i].Kind == DiffKind.Same) continue;
            for (int k = Math.Max(0, i - context); k <= Math.Min(diff.Count - 1, i + context); k++) keep[k] = true;
        }

        int maxNo = diff.Count == 0 ? 1 : diff.Max(d => Math.Max(d.OldNo, d.NewNo));
        int numW = Math.Max(3, maxNo.ToString().Length);

        var lines = new List<string>();
        int shown = 0, hidden = 0;
        bool gap = false;
        for (int i = 0; i < diff.Count; i++)
        {
            if (!keep[i]) { gap = lines.Count > 0; continue; }
            if (shown >= maxLines) { hidden++; continue; }
            if (gap) { lines.Add($"{Indent}[{Theme.Faint}] {new string(' ', numW - 1)}{Sym.Gap}[/]"); gap = false; }
            var d = diff[i];
            lines.Add(Row(d.Kind, d.Kind == DiffKind.Del ? d.OldNo : d.NewNo, d.Text, numW, width));
            shown++;
        }
        if (hidden > 0) lines.Add($"{Indent}[{Theme.Dim}] … +{hidden} more lines[/]");
        return lines;
    }

    /// <summary>Preview of a whole file (new file in green, deleted file in red).</summary>
    public static List<string> RenderWhole(string[] content, bool added, int maxLines = 10)
    {
        int width = UI.Width;
        int numW = Math.Max(3, content.Length.ToString().Length);
        var lines = new List<string>();
        for (int i = 0; i < Math.Min(maxLines, content.Length); i++)
            lines.Add(Row(added ? DiffKind.Add : DiffKind.Del, i + 1, content[i], numW, width));
        if (content.Length > maxLines)
            lines.Add($"{Indent}[{Theme.Dim}] … +{content.Length - maxLines} more lines[/]");
        return lines;
    }
}
