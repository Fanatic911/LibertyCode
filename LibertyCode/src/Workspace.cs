namespace LibertyCode;

/// <summary>The folder the agent works in. Every file path is confined to it.</summary>
internal sealed class Workspace
{
    public string Root { get; }

    public Workspace(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private const string Hint = "Do not retry with another path or command. Tell the user you can only work inside the workspace.";

    private bool Inside(string full) =>
        full.Equals(Root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ToolError("Missing path.");
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        full = Path.TrimEndingDirectorySeparator(full);
        if (!Inside(full)) throw new ToolError($"Access denied: '{path}' is outside the workspace.", Hint);

        // a link or junction inside the workspace must not lead out of it
        try
        {
            for (string? p = full; p != null && p.Length > Root.Length; p = Path.GetDirectoryName(p))
            {
                FileSystemInfo fi = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
                if (fi.Exists && fi.LinkTarget != null && fi.ResolveLinkTarget(true) is { } target
                    && !Inside(Path.TrimEndingDirectorySeparator(target.FullName)))
                    throw new ToolError($"Access denied: '{path}' leads outside the workspace.", Hint);
            }
        }
        catch (IOException) { }
        return full;
    }

    private static readonly System.Text.RegularExpressions.Regex OutsideRx = new(
        @"(?<![\w])(?:[A-Za-z]:(?=[\\/]|\s|$|[""'])|\\\\)[^\s""'|&<>;]*"
        + @"|%(?:userprofile|appdata|localappdata|homedrive|homepath|systemroot|systemdrive|windir|programfiles|programdata|public)%"
        + @"|(?<![\w.~])\.\.(?=[\\/]|$|\s|[""'])|(?<=^|\s)~(?=[\\/]|\s|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The first thing in a shell command that points outside the workspace (other drive, parent folder, home...), or null.</summary>
    public string? OutsidePath(string command)
    {
        foreach (System.Text.RegularExpressions.Match m in OutsideRx.Matches(command))
        {
            string v = m.Value;
            bool absolute = v.Length >= 2 && (v[1] == ':' || v.StartsWith("\\\\"));
            if (absolute)
            {
                try { if (Inside(Path.TrimEndingDirectorySeparator(Path.GetFullPath(v)))) continue; } catch { }
            }
            return v;
        }
        return null;
    }

    public string Rel(string full)
    {
        string r = Path.GetRelativePath(Root, full);
        return r == "." ? "." : r;
    }
}

/// <summary>Remembers file contents before each change so /undo can restore them.</summary>
internal sealed class UndoStack
{
    private readonly List<List<(string Path, byte[]? Data)>> _turns = [];
    private List<(string Path, byte[]? Data)>? _cur;

    public void BeginTurn() => _cur = null;

    public void Record(string path)
    {
        if (_cur == null) { _cur = []; _turns.Add(_cur); }
        if (_cur.Any(x => x.Path == path)) return;
        _cur.Add((path, File.Exists(path) ? File.ReadAllBytes(path) : null));
    }

    /// <summary>Undoes the last turn that changed files. Returns "restored"/"removed" lines.</summary>
    public List<(string Path, string Action)> UndoLast()
    {
        var report = new List<(string, string)>();
        if (_turns.Count == 0) return report;
        var turn = _turns[^1];
        _turns.RemoveAt(_turns.Count - 1);
        _cur = null;
        for (int i = turn.Count - 1; i >= 0; i--)
        {
            var (path, data) = turn[i];
            try
            {
                if (data == null) { if (File.Exists(path)) File.Delete(path); report.Add((path, "removed")); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, data);
                    report.Add((path, "restored"));
                }
            }
            catch (Exception ex) { report.Add((path, "failed: " + ex.Message)); }
        }
        return report;
    }
}
