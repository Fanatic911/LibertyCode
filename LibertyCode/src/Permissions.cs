using Spectre.Console;

namespace LibertyCode;

internal enum Decision { Yes, Always, No }

/// <summary>Decides when to ask, and shows the inline approval menu (replaces the old popup window).</summary>
internal sealed class Permissions
{
    private readonly HashSet<string> _allowedCommands = new(StringComparer.Ordinal);
    public PermissionMode Mode { get; set; }

    public Permissions(PermissionMode mode) => Mode = mode;

    public bool Needs(Plan p) => p.Kind switch
    {
        ToolKind.Read    => false,
        ToolKind.Edit    => Mode == PermissionMode.Ask,
        ToolKind.Delete  => Mode != PermissionMode.Auto,
        ToolKind.Command => p.Dangerous
                            || (Mode != PermissionMode.Auto
                                && !_allowedCommands.Contains(p.Key)
                                && !Shell.IsSafeReadOnly(p.Key)),
        _ => true,
    };

    public void Remember(Plan p)
    {
        if (p.Kind == ToolKind.Edit) Mode = PermissionMode.Edits;
        else if (p.Kind == ToolKind.Command && !p.Dangerous) _allowedCommands.Add(p.Key);
    }

    public (Decision Decision, string? Note) Ask(Plan p)
    {
        var options = new List<(string Label, Decision D)> { ("Yes", Decision.Yes) };
        if (!p.Dangerous && p.Kind == ToolKind.Edit)
            options.Add(("Yes, and don't ask again for edits this session", Decision.Always));
        if (!p.Dangerous && p.Kind == ToolKind.Command)
            options.Add(("Yes, and don't ask again for this exact command", Decision.Always));
        options.Add(("No", Decision.No));

        string question = p.Dangerous ? "This command looks destructive. Run it anyway?" : p.Question;
        string qColor = p.Dangerous ? Theme.Red : Theme.Violet;

        if (Console.IsInputRedirected)
        {
            UI.Line($"  {UI.M(qColor, question)} {UI.Dim("[y]es / [a]lways / [n]o")}");
            string? a = Console.ReadLine()?.Trim().ToLowerInvariant();
            return a switch
            {
                "y" or "yes" => (Decision.Yes, null),
                "a" or "always" when options.Any(o => o.D == Decision.Always) => (Decision.Always, null),
                _ => (Decision.No, null),
            };
        }

        EscWatcher.Pause();
        int sel = 0;
        Decision result;
        try
        {
            Console.Write("\x1b[?25l");
            AnsiConsole.MarkupLine($"  [bold {qColor}]{Markup.Escape(Loc.T(question))}[/]");
            Draw(options, sel, first: true);

            while (true)
            {
                var k = Console.ReadKey(true);
                bool ctrlC = k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0;
                if (k.Key is ConsoleKey.UpArrow or ConsoleKey.K) sel = (sel - 1 + options.Count) % options.Count;
                else if (k.Key is ConsoleKey.DownArrow or ConsoleKey.J or ConsoleKey.Tab) sel = (sel + 1) % options.Count;
                else if (k.Key == ConsoleKey.Enter) { result = options[sel].D; break; }
                else if (k.Key == ConsoleKey.Y) { result = Decision.Yes; break; }
                else if (k.Key == ConsoleKey.A && options.Any(o => o.D == Decision.Always)) { result = Decision.Always; break; }
                else if (k.Key == ConsoleKey.N || k.Key == ConsoleKey.Escape || ctrlC) { result = Decision.No; break; }
                else if (k.KeyChar >= '1' && k.KeyChar < '1' + options.Count) { result = options[k.KeyChar - '1'].D; break; }
                else continue;
                Draw(options, sel, first: false);
            }

            // collapse the menu (question + options)
            Console.Write($"\x1b[{options.Count + 1}A\r\x1b[J");
        }
        finally
        {
            Console.Write("\x1b[?25h");
            EscWatcher.Resume();
        }

        if (result != Decision.No) return (result, null);

        // Esc watcher stays paused while typing: otherwise it consumes keystrokes and the note loses characters.
        EscWatcher.Pause();
        string? note;
        try { note = LineEditor.Read($"  {UI.Dim($"tell the model what to do instead (enter to skip) {Sym.Pointer}")} ", 55); }
        finally { EscWatcher.Resume(); }
        return (Decision.No, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
    }

    private static void Draw(List<(string Label, Decision D)> options, int sel, bool first)
    {
        if (!first) Console.Write($"\x1b[{options.Count}A");
        for (int i = 0; i < options.Count; i++)
        {
            Console.Write("\r\x1b[2K");
            string label = Markup.Escape($"{i + 1}. {Loc.T(options[i].Label)}");
            AnsiConsole.MarkupLine(i == sel
                ? $"  [bold {Theme.Orange}]{Sym.Pointer}[/] [bold]{label}[/]"
                : $"    [{Theme.Dim}]{label}[/]");
        }
    }
}
