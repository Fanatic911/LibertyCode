using Newtonsoft.Json;

namespace LibertyCode;

/// <summary>How much the agent may do without asking.</summary>
internal enum PermissionMode
{
    Ask,    // ask before every edit / delete / command
    Edits,  // file edits are applied directly, deletes and commands still ask
    Auto,   // nothing asks (dangerous commands still do)
}

internal class AppSettings
{
    public string Provider  { get; set; } = "";
    public string Model     { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string Language  { get; set; } = "en";

    /// <summary>true: reopen the last project/folder at startup. false: always start on /project.</summary>
    public bool AutoSelect    { get; set; } = false;

    /// <summary>true: send a desktop notification when the AI has finished answering.</summary>
    public bool Notifications { get; set; } = false;

    /// <summary>true: open the live debug window (every request, response, error and processing step).</summary>
    public bool Debug         { get; set; } = false;

    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public PermissionMode Mode { get; set; } = PermissionMode.Ask;
}

internal static class SettingsManager
{
    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(Paths.SettingsFile)
                ? JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { s = new AppSettings(); }

        if (string.IsNullOrWhiteSpace(s.Language)) s.Language = DetectLanguage();
        Loc.Lang = s.Language.ToLowerInvariant();
        // An empty workspace means "no project open" (the /project hub).
        if (string.IsNullOrWhiteSpace(s.Workspace) || !Directory.Exists(s.Workspace)) s.Workspace = "";
        return s;
    }

    public static string DetectLanguage()
    {
        try
        {
            return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() == "fr" ? "fr" : "en";
        }
        catch { return "en"; }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Paths.SettingsFile, JsonConvert.SerializeObject(s, Formatting.Indented));
        }
        catch (Exception ex) { Logger.Error("Could not save settings: " + ex.Message); }
    }
}
