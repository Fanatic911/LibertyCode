using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace LibertyCode;

internal enum ToolKind { Read, Edit, Delete, Command }

internal sealed class ToolOutput
{
    public string ForModel = "";
    public string Summary = "";          // markup shown under the tool header
    public List<string>? Details;        // extra markup lines
    public bool IsError;
}

/// <summary>What a tool call is about to do. Built first so it can be previewed and approved before running.</summary>
internal sealed class Plan
{
    public ToolKind Kind;
    public string Question = "";
    public string Key = "";
    public bool Dangerous;
    public string? Headline;             // markup, shown before the preview
    public List<string>? Preview;        // markup lines (diff)
    public required Func<CancellationToken, Task<ToolOutput>> Run;
}

internal static class Tools
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "__pycache__", "dist", "target", ".venv", "venv",
    };

    private static JObject S(string json) => JObject.Parse(json);

    public static readonly IReadOnlyList<ToolSpec> Specs =
    [
        new("read_file", "Read a text file. Returns numbered lines. Use offset/limit for large files.",
            S("""{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace"},"offset":{"type":"integer","description":"First line to read (1-based)"},"limit":{"type":"integer","description":"Max lines (default 2000)"}},"required":["path"]}""")),
        new("write_file", "Create a new file or fully overwrite an existing one. Prefer edit_file for changes to existing files.",
            S("""{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string","description":"Complete file content"}},"required":["path","content"]}""")),
        new("edit_file", "Replace text in an existing file. old_string must match exactly (whitespace included) and be unique unless replace_all is true.",
            S("""{"type":"object","properties":{"path":{"type":"string"},"old_string":{"type":"string"},"new_string":{"type":"string"},"replace_all":{"type":"boolean","description":"Replace every occurrence"}},"required":["path","old_string","new_string"]}""")),
        new("delete_file", "Delete a file.",
            S("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")),
        new("list_dir", "List the files and folders of a directory (not recursive).",
            S("""{"type":"object","properties":{"path":{"type":"string","description":"Directory, default is the workspace root"}}}""")),
        new("glob", "Find files by glob pattern, e.g. **/*.cs or src/**/*.json.",
            S("""{"type":"object","properties":{"pattern":{"type":"string"}},"required":["pattern"]}""")),
        new("grep", "Search file contents with a regular expression. Returns file:line: text.",
            S("""{"type":"object","properties":{"pattern":{"type":"string","description":"Regular expression"},"path":{"type":"string","description":"File or directory to search (default: workspace)"},"glob":{"type":"string","description":"Only files matching this glob, e.g. *.cs"},"ignore_case":{"type":"boolean"}},"required":["pattern"]}""")),
        new("run_command", "Run a shell command in the workspace (build, test, git...). The user must approve it.",
            S("""{"type":"object","properties":{"command":{"type":"string"},"timeout_seconds":{"type":"integer","description":"Default 120, max 600"}},"required":["command"]}""")),
    ];

    // ── Argument helpers ──────────────────────────────────────────────────────
    private static string Str(JObject a, string key, bool required = true)
    {
        string? v = a[key]?.Type == JTokenType.String ? (string?)a[key] : null;
        if (required && v == null) throw new ToolError($"Missing required argument '{key}'.");
        return v ?? "";
    }
    private static int? Int(JObject a, string key) => a[key]?.Type is JTokenType.Integer or JTokenType.Float ? (int?)a[key] : null;
    private static bool Bool(JObject a, string key) => a[key]?.Type == JTokenType.Boolean && (bool)a[key]!;

    public static (string Name, string Arg) Describe(string tool, JObject a)
    {
        string P(string k) => a[k]?.Type == JTokenType.String ? (string)a[k]! : "";
        return tool switch
        {
            "read_file"   => ("Read", P("path")),
            "write_file"  => ("Write", P("path")),
            "edit_file"   => ("Update", P("path")),
            "delete_file" => ("Delete", P("path")),
            "list_dir"    => ("List", string.IsNullOrEmpty(P("path")) ? "." : P("path")),
            "glob"        => ("Glob", P("pattern")),
            "grep"        => ("Search", P("pattern")),
            "run_command" => ("Run", P("command").ReplaceLineEndings(" ")),
            _             => (tool, ""),
        };
    }

    public static Plan Prepare(Workspace ws, UndoStack undo, string tool, JObject a)
    {
        if (a["__error"] != null) throw new ToolError((string?)a["__error"] ?? "Invalid arguments.");
        return tool switch
        {
            "read_file"   => PrepareRead(ws, a),
            "write_file"  => PrepareWrite(ws, undo, a),
            "edit_file"   => PrepareEdit(ws, undo, a),
            "delete_file" => PrepareDelete(ws, undo, a),
            "list_dir"    => PrepareList(ws, a),
            "glob"        => PrepareGlob(ws, a),
            "grep"        => PrepareGrep(ws, a),
            "run_command" => PrepareRun(ws, a),
            _             => throw new ToolError($"Unknown tool '{tool}'."),
        };
    }

    private static Plan Instant(Func<ToolOutput> f) => new()
    {
        Kind = ToolKind.Read,
        Run = _ => Task.FromResult(f()),
    };

    // ── Text file helpers ─────────────────────────────────────────────────────
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Reads a text file without ever corrupting accents: UTF-8 first, and if the bytes are not valid UTF-8
    /// (old Windows-1252 files) they are decoded as Latin-1, which round-trips byte for byte.
    /// </summary>
    private static (string Text, bool Bom, bool Latin1) ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Take(8000).Contains((byte)0)) throw new ToolError("This looks like a binary file.");
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        int skip = bom ? 3 : 0;
        try { return (StrictUtf8.GetString(bytes, skip, bytes.Length - skip), bom, false); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(bytes), false, true); }
    }

    private static void WriteText(string path, string text, bool bom, bool latin1 = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] body = latin1 ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
    }

    /// <summary>
    /// Maps typographic look-alikes (em/en dashes, curly quotes, non-breaking spaces) to plain ASCII, one char for one char,
    /// so indices stay valid. Models often type "-" where the file has "—" (or the reverse).
    /// </summary>
    private static string Fold(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
            c[i] = c[i] switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                '‘' or '’' or '‛' => '\'',
                '“' or '”' or '‟' => '"',
                ' ' or ' ' or ' ' or ' ' => ' ',
                _ => c[i],
            };
        return new string(c);
    }

    private static int CountOf(string hay, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = hay.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }

    private static string MatchEol(string s, bool crlf) =>
        crlf ? s.Replace("\r\n", "\n").Replace("\n", "\r\n") : s.Replace("\r\n", "\n");

    private static string Stats(int added, int removed) =>
        $"[{Theme.Green}]+{added}[/] [{Theme.Red}]−{removed}[/]";

    // ── read_file ─────────────────────────────────────────────────────────────
    private static Plan PrepareRead(Workspace ws, JObject a)
    {
        string full = ws.Resolve(Str(a, "path"));
        int offset = Math.Max(1, Int(a, "offset") ?? 1);
        int limit = Math.Clamp(Int(a, "limit") ?? 2000, 1, 5000);
        return Instant(() =>
        {
            if (Directory.Exists(full)) throw new ToolError("That is a directory. Use list_dir.");
            if (!File.Exists(full)) throw new ToolError($"File not found: {ws.Rel(full)}");
            var sb = new StringBuilder();
            int total = 0, shown = 0;
            long size = new FileInfo(full).Length;
            IEnumerable<string> Lines()
            {
                if (size > 4_000_000)
                {
                    using var fs = File.OpenRead(full);
                    var head = new byte[Math.Min(8000, fs.Length)];
                    int n = fs.Read(head, 0, head.Length);
                    if (head.Take(n).Contains((byte)0)) throw new ToolError("This looks like a binary file.");
                    foreach (var l in File.ReadLines(full)) yield return l;
                    yield break;
                }
                using var sr = new StringReader(ReadText(full).Text);
                string? line;
                while ((line = sr.ReadLine()) != null) yield return line;
            }
            foreach (var line in Lines())
            {
                total++;
                if (total < offset || shown >= limit) continue;
                string l = line.Length > 2000 ? line[..2000] + "…" : line;
                sb.Append(total.ToString().PadLeft(6)).Append('\t').AppendLine(l);
                shown++;
            }
            if (total == 0) return new ToolOutput { ForModel = "(empty file)", Summary = UI.Dim("Empty file") };
            if (shown == 0) throw new ToolError($"Offset {offset} is past the end of the file ({total} lines).");
            if (offset + shown - 1 < total)
                sb.AppendLine($"[file has {total} lines; showing {offset}-{offset + shown - 1}. Use offset/limit to read more.]");
            return new ToolOutput { ForModel = sb.ToString(), Summary = UI.Dim(Loc.T($"Read {shown} line{(shown == 1 ? "" : "s")}")) };
        });
    }

    // ── write_file ────────────────────────────────────────────────────────────
    private static Plan PrepareWrite(Workspace ws, UndoStack undo, JObject a)
    {
        string full = ws.Resolve(Str(a, "path"));
        string content = Str(a, "content");
        string rel = ws.Rel(full);
        if (Directory.Exists(full)) throw new ToolError("That path is a directory.");

        bool exists = File.Exists(full);
        string old = ""; bool bom = false, latin1 = false;
        if (exists) (old, bom, latin1) = ReadText(full);
        bool crlf = old.Contains("\r\n");
        string final = exists ? MatchEol(content, crlf) : content;

        if (exists && final == old)
            return Instant(() => new ToolOutput { ForModel = "No changes: the file already has this content.", Summary = UI.Dim(Loc.T("No changes")) });

        var newLines = Diff.SplitLines(final);
        Plan plan = new()
        {
            Kind = ToolKind.Edit,
            Question = exists ? "Overwrite this file?" : "Create this file?",
            Run = _ =>
            {
                undo.Record(full);
                // a Latin-1 file stays Latin-1 unless the new text contains characters it cannot hold
                WriteText(full, final, bom, latin1 && final.All(ch => ch <= 'ÿ'));
                return Task.FromResult(new ToolOutput { ForModel = $"{(exists ? "Updated" : "Created")} {rel}." });
            },
        };

        if (!exists)
        {
            plan.Headline = $"{UI.M(Theme.Green, "Created")} {UI.Dim($"· {newLines.Length} lines")}";
            plan.Preview = Diff.RenderWhole(newLines, added: true);
        }
        else
        {
            var diff = Diff.Compute(Diff.SplitLines(old), newLines);
            var (ad, rm) = Diff.Count(diff);
            plan.Headline = $"{UI.M(Theme.Yellow, "Rewrote")} {Stats(ad, rm)}";
            plan.Preview = Diff.Render(diff);
        }
        return plan;
    }

    // ── edit_file ─────────────────────────────────────────────────────────────
    private static Plan PrepareEdit(Workspace ws, UndoStack undo, JObject a)
    {
        string full = ws.Resolve(Str(a, "path"));
        string rel = ws.Rel(full);
        string oldStr = Str(a, "old_string"), newStr = Str(a, "new_string");
        bool all = Bool(a, "replace_all");

        if (!File.Exists(full)) throw new ToolError($"File not found: {rel}. Use write_file to create it.");
        if (oldStr.Length == 0) throw new ToolError("old_string is empty. Use write_file to create or overwrite a file.");
        if (oldStr == newStr) throw new ToolError("old_string and new_string are identical.");

        var (text, bom, latin1) = ReadText(full);
        bool crlf = text.Contains("\r\n");
        oldStr = MatchEol(oldStr, crlf);
        newStr = MatchEol(newStr, crlf);

        // Exact match first; if that fails, retry ignoring typographic look-alikes (— vs -, curly quotes...).
        string hay = text, needle = oldStr;
        int count = CountOf(hay, needle);
        if (count == 0)
        {
            string fh = Fold(text), fn = Fold(oldStr);
            int fc = CountOf(fh, fn);
            if (fc > 0) { hay = fh; needle = fn; count = fc; }
        }
        if (count == 0)
        {
            string shown = text.Length > 6000 ? text[..6000] + "\n… [truncated]" : text;
            throw new ToolError(
                "old_string was not found. It must match the file exactly, including whitespace. Read the file again.",
                $"Current content of {rel} (copy old_string from here, do not retype it):\n{shown}");
        }
        if (count > 1 && !all)
            throw new ToolError($"old_string appears {count} times. Add more surrounding context to make it unique, or set replace_all.");

        string result;
        if (all)
        {
            var rb = new StringBuilder();
            int last = 0, at = 0;
            while ((at = hay.IndexOf(needle, last, StringComparison.Ordinal)) >= 0)
            {
                rb.Append(text, last, at - last).Append(newStr);
                last = at + needle.Length;
            }
            rb.Append(text, last, text.Length - last);
            result = rb.ToString();
        }
        else
        {
            int at = hay.IndexOf(needle, StringComparison.Ordinal);
            result = text[..at] + newStr + text[(at + needle.Length)..];
        }

        var diff = Diff.Compute(Diff.SplitLines(text), Diff.SplitLines(result));
        var (ad, rm) = Diff.Count(diff);

        return new Plan
        {
            Kind = ToolKind.Edit,
            Question = "Apply this edit?",
            Headline = $"{UI.M(Theme.Yellow, "Updated")} {Stats(ad, rm)}{(count > 1 ? UI.Dim($" · {count} places") : "")}",
            Preview = Diff.Render(diff),
            Run = _ =>
            {
                undo.Record(full);
                WriteText(full, result, bom, latin1 && result.All(ch => ch <= 'ÿ'));
                return Task.FromResult(new ToolOutput { ForModel = $"Edited {rel} (+{ad} −{rm})." });
            },
        };
    }

    // ── delete_file ───────────────────────────────────────────────────────────
    private static Plan PrepareDelete(Workspace ws, UndoStack undo, JObject a)
    {
        string full = ws.Resolve(Str(a, "path"));
        string rel = ws.Rel(full);
        if (Directory.Exists(full)) throw new ToolError("That is a directory. delete_file only removes files.");
        if (!File.Exists(full)) throw new ToolError($"File not found: {rel}");

        string[] lines;
        try { lines = Diff.SplitLines(ReadText(full).Text); } catch (ToolError) { lines = []; }

        return new Plan
        {
            Kind = ToolKind.Delete,
            Question = "Delete this file?",
            Headline = $"{UI.M(Theme.Red, "Deleted")} {UI.Dim($"· {lines.Length} lines")}",
            Preview = lines.Length > 0 ? Diff.RenderWhole(lines, added: false, maxLines: 8) : null,
            Run = _ =>
            {
                undo.Record(full);
                File.Delete(full);
                return Task.FromResult(new ToolOutput { ForModel = $"Deleted {rel}." });
            },
        };
    }

    // ── list_dir ──────────────────────────────────────────────────────────────
    private static Plan PrepareList(Workspace ws, JObject a)
    {
        string full = ws.Resolve(Str(a, "path", required: false) is { Length: > 0 } p ? p : ".");
        return Instant(() =>
        {
            if (!Directory.Exists(full)) throw new ToolError($"Directory not found: {ws.Rel(full)}");
            var entries = Directory.EnumerateFileSystemEntries(full)
                .Where(e => !Ignored.Contains(Path.GetFileName(e)))
                .OrderByDescending(Directory.Exists).ThenBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
            var sb = new StringBuilder();
            foreach (var e in entries.Take(200))
                sb.AppendLine(Directory.Exists(e) ? Path.GetFileName(e) + "/" : Path.GetFileName(e));
            if (entries.Count > 200) sb.AppendLine($"… +{entries.Count - 200} more");
            return new ToolOutput
            {
                ForModel = entries.Count == 0 ? "(empty directory)" : sb.ToString(),
                Summary = UI.Dim(Loc.T($"{entries.Count} item{(entries.Count == 1 ? "" : "s")}")),
            };
        });
    }

    // ── glob / grep ───────────────────────────────────────────────────────────
    private static IEnumerable<string> Walk(string dir)
    {
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { entries = []; }
        foreach (var e in entries)
        {
            if (Directory.Exists(e))
            {
                if (Ignored.Contains(Path.GetFileName(e))) continue;
                foreach (var f in Walk(e)) yield return f;
            }
            else yield return e;
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        glob = glob.Replace('\\', '/');
        var sb = new StringBuilder("^");
        if (!glob.Contains('/')) sb.Append("(?:.*/)?");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                    else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    private static Plan PrepareGlob(Workspace ws, JObject a)
    {
        var rx = GlobToRegex(Str(a, "pattern"));
        return Instant(() =>
        {
            var hits = Walk(ws.Root).Select(f => ws.Rel(f).Replace('\\', '/')).Where(r => rx.IsMatch(r)).ToList();
            string body = hits.Count == 0 ? "No files matched." : string.Join('\n', hits.Take(200)) + (hits.Count > 200 ? $"\n… +{hits.Count - 200} more" : "");
            return new ToolOutput { ForModel = body, Summary = UI.Dim(Loc.T($"{hits.Count} file{(hits.Count == 1 ? "" : "s")}")) };
        });
    }

    private static Plan PrepareGrep(Workspace ws, JObject a)
    {
        string pattern = Str(a, "pattern");
        string start = ws.Resolve(Str(a, "path", required: false) is { Length: > 0 } p ? p : ".");
        string globStr = Str(a, "glob", required: false);
        Regex rx;
        try
        {
            rx = new Regex(pattern, (Bool(a, "ignore_case") ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex) { throw new ToolError("Invalid regular expression: " + ex.Message); }
        Regex? fileRx = globStr.Length > 0 ? GlobToRegex(globStr) : null;

        return Instant(() =>
        {
            IEnumerable<string> files = File.Exists(start) ? [start] : Walk(start);
            var sb = new StringBuilder();
            int matches = 0;
            foreach (var f in files)
            {
                string rel = ws.Rel(f).Replace('\\', '/');
                if (fileRx != null && !fileRx.IsMatch(rel)) continue;
                try
                {
                    if (new FileInfo(f).Length > 1_000_000) continue;
                    int n = 0;
                    foreach (var line in File.ReadLines(f))
                    {
                        n++;
                        if (n == 1 && line.Contains('\0')) break;
                        if (!rx.IsMatch(line)) continue;
                        sb.AppendLine($"{rel}:{n}: {(line.Length > 300 ? line[..300] + "…" : line.Trim())}");
                        if (++matches >= 100) break;
                    }
                }
                catch (RegexMatchTimeoutException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (matches >= 100) break;
            }
            return new ToolOutput
            {
                ForModel = matches == 0 ? "No matches." : sb.ToString() + (matches >= 100 ? "[stopped at 100 matches]" : ""),
                Summary = UI.Dim(Loc.T($"{matches} match{(matches == 1 ? "" : "es")}")),
            };
        });
    }

    // ── run_command ───────────────────────────────────────────────────────────
    private static Plan PrepareRun(Workspace ws, JObject a)
    {
        string command = Str(a, "command").Trim();
        if (command.Length == 0) throw new ToolError("Empty command.");
        if (ws.OutsidePath(command) is { } outside)
            throw new ToolError($"Access denied: the command reaches '{outside}', outside the workspace.",
                "Do not retry with another path or command. Tell the user you can only work inside the workspace.");
        int timeout = Math.Clamp(Int(a, "timeout_seconds") ?? 120, 5, 600);

        return new Plan
        {
            Kind = ToolKind.Command,
            Question = "Run this command?",
            Key = command,
            Dangerous = Shell.IsDangerous(command),
            Run = async ct =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (code, output, timedOut) = await Shell.RunAsync(command, ws.Root, timeout, ct);
                sw.Stop();

                string time = sw.Elapsed.TotalSeconds >= 1 ? $" · {sw.Elapsed.TotalSeconds:0.#}s" : "";
                var res = new ToolOutput { IsError = code != 0 };
                res.Summary = timedOut
                    ? UI.M(Theme.Red, Loc.T($"Timed out after {timeout}s"))
                    : code == 0 ? $"{UI.M(Theme.Green, Sym.Check)} {UI.Dim("exit 0" + time)}"
                                : $"{UI.M(Theme.Red, Sym.Cross)} {UI.M(Theme.Red, "exit " + code)}{UI.Dim(time)}";

                var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                int show = code == 0 ? 3 : 8;
                var tail = lines.Count > show ? lines.TakeLast(show).ToList() : lines;
                var details = new List<string>();
                if (lines.Count > show) details.Add($"     {UI.Dim(Loc.T($"… +{lines.Count - show} lines"))}");
                foreach (var l in tail)
                {
                    string t = l.Replace("\t", "    ");
                    int room = Math.Max(20, UI.Width - 7);
                    if (t.Length > room) t = t[..(room - 1)] + "…";
                    details.Add($"     {UI.Dim(t)}");
                }
                res.Details = details;
                res.ForModel = (timedOut ? $"Command timed out after {timeout}s.\n" : $"exit code: {code}\n") + Shell.Clip(output);
                return res;
            },
        };
    }
}
