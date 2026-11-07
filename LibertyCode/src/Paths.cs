namespace LibertyCode;

/// <summary>Where LibertyCode keeps its data. No admin rights needed: everything lives in the user profile.</summary>
internal static class Paths
{
    private static readonly string? Override = Environment.GetEnvironmentVariable("LIBERTY_HOME");

    public static readonly string DataDir = !string.IsNullOrWhiteSpace(Override)
        ? Override!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LibertyCode");

    public static readonly string ProjectsDir = !string.IsNullOrWhiteSpace(Override)
        ? Path.Combine(Override!, "Projects")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LibertyCode", "Projects");

    public static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");
    public static readonly string EnvFile      = Path.Combine(DataDir, ".env.enc");
    public static readonly string LogFile      = Path.Combine(DataDir, "libertycode.log");
    public static readonly string DebugFile    = Path.Combine(DataDir, "debug.log");
    public static readonly string PromptFile   = Path.Combine(DataDir, "prompt.txt");
    public static readonly string HistoryFile  = Path.Combine(DataDir, "history.txt");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ProjectsDir);
    }
}
