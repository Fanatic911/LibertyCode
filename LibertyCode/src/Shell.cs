using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LibertyCode;

internal static class Shell
{
    private static readonly Regex DangerousRx = new(
        @"(^|[\s;&|(])(rm\s+-[a-z]*(rf|fr)|rm\s+-[a-z]*r\b|rmdir\s+/s|rd\s+/s|del\s+.*/[sfq]|format\s+[a-z]:|remove-item\s+.*-recurse|shutdown|mkfs|dd\s+if=|reg\s+delete|diskpart|set-executionpolicy|git\s+push\s+.*(--force|\s-f\b)|git\s+reset\s+--hard|git\s+clean\s+-[a-z]*f)|(curl|wget|iwr|irm)\s.*\|\s*(sh|bash|iex)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SafeRx = new(
        @"^(git\s+(status|diff|log|branch|show|rev-parse)(\s+[\w\-./=:@^~]+)*|ls(\s+[\w\-./]+)*|dir(\s+[\w\-./\\:*?]+)*|pwd|whoami|dotnet\s+--(version|info|list-sdks)|node\s+(-v|--version)|python3?\s+(-V|--version)|npm\s+(-v|--version))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsDangerous(string command) => DangerousRx.IsMatch(command);
    public static bool IsSafeReadOnly(string command) => SafeRx.IsMatch(command.Trim());

    public static async Task<(int Code, string Output, bool TimedOut)> RunAsync(
        string command, string cwd, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (OperatingSystem.IsWindows())
        {
            // chcp 65001 makes cmd.exe (and most tools) emit UTF-8, so accents are not garbled in the output.
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"chcp 65001>nul & " + command + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        Logger.Trace("TOOL", $"Shell: {command}\n  cwd={psi.WorkingDirectory}, timeout={timeoutSeconds}s");
        var clock = Stopwatch.StartNew();
        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };

        try { p.Start(); }
        catch (Exception ex) { Logger.Exception("Shell start failed", ex); return (-1, ex.Message, false); }

        try { p.StandardInput.Close(); } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        bool timedOut = false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(limit.Token);
            p.WaitForExit(); // flush the async readers
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        string output;
        lock (sb) output = sb.ToString().TrimEnd();
        Logger.Trace("TOOL", $"Shell finished in {clock.ElapsedMilliseconds} ms: {(timedOut ? "TIMED OUT" : "exit " + p.ExitCode)}, {output.Length} chars of output");
        return (timedOut ? -1 : p.ExitCode, output, timedOut);
    }

    public static string Clip(string s, int max = 12000)
    {
        if (s.Length <= max) return s;
        int half = max / 2;
        return s[..half] + $"\n… [{s.Length - max} characters omitted] …\n" + s[^half..];
    }
}
