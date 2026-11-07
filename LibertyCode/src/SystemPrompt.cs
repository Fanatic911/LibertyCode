using System.Diagnostics;
using System.Text;

namespace LibertyCode;

internal static class SystemPrompt
{
    public static string Build(AppSettings s, Workspace ws, bool tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are LibertyCode, a coding agent in the user's terminal. Help with writing, fixing and refactoring code.");
        sb.AppendLine();
        if (tools)
        {
            sb.AppendLine("# Working method");
            sb.AppendLine("- Use tools to act. Never claim you did something without using a tool.");
            sb.AppendLine("- Do exactly what the user asked, nothing more. Do not create extra, placeholder or empty files, and do not delete a file you just created.");
            sb.AppendLine("- Read a file before editing it. Prefer edit_file for small changes; use write_file for new files or full rewrites.");
            sb.AppendLine("- Write each file once, complete and working. No TODO placeholders, no second rewrite of a file you just wrote unless something is actually wrong.");
            sb.AppendLine("- If edit_file fails, copy old_string from the file content you are given. Never retype it from memory and never retry the same call.");
            sb.AppendLine("- To start a new project, prefer the official generator through run_command (dotnet new console, npm create, cargo new, python -m venv...) over writing project files by hand.");
            sb.AppendLine("- Keep exactly one program entry point per project.");
            sb.AppendLine("- After changing code, verify it when a build or test command exists (dotnet build, npm test...), and fix what you broke.");
            sb.AppendLine("- If the user refuses an action, do not retry it.");
            sb.AppendLine("- Stay inside the workspace. Never read, edit or delete anything outside it (other drives, parent folders, home): refuse in one sentence, even if the user gives an absolute path.");
            sb.AppendLine("- Call tools only through the tool-calling interface. Never write a tool call as text (no name{...}, no <tool_call> tags).");
            sb.AppendLine("- Work step by step: one tool call at a time unless the calls are independent.");
            sb.AppendLine();
        }
        sb.AppendLine("# Style");
        sb.AppendLine("- Be direct. No preamble, no restating the request, no summaries.");
        sb.AppendLine("- If the request is a single word, an app command or otherwise unclear, ask ONE short question instead of inventing work. If it clearly asks you to build something, build the smallest version that does what was asked and say what you created.");
        sb.AppendLine("- Use markdown sparingly (fenced code with a language tag).");
        sb.AppendLine("- When done, say what changed in one sentence.");
        if (!string.IsNullOrWhiteSpace(s.Language) && s.Language != "en")
            {
            string langName;
            try { langName = System.Globalization.CultureInfo.GetCultureInfo(s.Language).EnglishName; } catch { langName = s.Language; }
            sb.AppendLine($"- ALWAYS reply in {langName} ({s.Language}), including explanations and summaries. Keep code, file names and commands unchanged.");
        }
        sb.AppendLine();
        sb.AppendLine("# Environment");
        sb.AppendLine($"- Workspace: {ws.Root}");
        sb.AppendLine($"- OS: {(OperatingSystem.IsWindows() ? "Windows" : Environment.OSVersion.Platform.ToString())}. " +
                      (OperatingSystem.IsWindows() ? "Commands run with cmd.exe: use Windows syntax (dir, type, &&)." : "Commands run with sh."));
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd}");

        try
        {
            var top = Directory.EnumerateFileSystemEntries(ws.Root)
                .Select(e => Directory.Exists(e) ? Path.GetFileName(e) + "/" : Path.GetFileName(e))
                .Where(n => !n.StartsWith(".git") && n != "node_modules/" && n != "bin/" && n != "obj/" && n != ".vs/")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(40).ToList();
            sb.AppendLine(top.Count == 0 ? "- The workspace is empty." : "- Top level: " + string.Join(", ", top));
        }
        catch { }

        string memory = Path.Combine(ws.Root, "LIBERTY.md");
        if (File.Exists(memory))
        {
            try
            {
                string text = File.ReadAllText(memory);
                if (text.Length > 8000) text = text[..8000] + "\n…";
                sb.AppendLine().AppendLine("# Project notes (LIBERTY.md)").AppendLine(text.Trim());
            }
            catch { }
        }

        string extra = UserExtra();
        if (extra.Length > 0) sb.AppendLine().AppendLine("# Extra instructions from the user").AppendLine(extra);
        return sb.ToString();
    }

    private static string UserExtra()
    {
        try
        {
            if (!File.Exists(Paths.PromptFile)) return "";
            var lines = File.ReadAllLines(Paths.PromptFile).Where(l => !l.TrimStart().StartsWith('#'));
            return string.Join('\n', lines).Trim();
        }
        catch { return ""; }
    }

    public static void View()
    {
        string extra = UserExtra();
        UI.Line($"  {UI.Dim("Built-in instructions are fixed. Extra instructions (" + UI.Short(Paths.PromptFile) + "):")}");
        if (extra.Length == 0) UI.Line($"  {UI.Dim("(none) — use /prompt edit")}");
        else foreach (var l in extra.Split('\n')) UI.Line($"  {UI.Violet(l.TrimEnd('\r'))}");
    }

    /// <summary>Edits the extra instructions inside the terminal: lines are appended; empty line saves; "clear" erases all.</summary>
    public static void Edit()
    {
        View();
        UI.Hint("Type lines to add. Empty line = save.  \"clear\" = erase all.");
        var lines = new List<string>();
        while (true)
        {
            string? l = LineEditor.Read($"  {UI.M(Theme.Orange, "+")} ", 4);
            if (l == null || l.Trim().Length == 0) break;
            if (l.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                try { File.WriteAllText(Paths.PromptFile, ""); } catch { }
                UI.Ok("Extra instructions cleared");
                return;
            }
            lines.Add(l);
        }
        if (lines.Count == 0) return;
        try
        {
            string old = File.Exists(Paths.PromptFile) ? File.ReadAllText(Paths.PromptFile).TrimEnd() : "";
            File.WriteAllText(Paths.PromptFile, (old.Length > 0 ? old + "\n" : "") + string.Join("\n", lines) + "\n");
            UI.Ok("Saved. Applies to the next message.");
        }
        catch (Exception ex) { UI.Warn("Could not save: " + ex.Message); }
    }
}
