namespace LibertyCode;

/// <summary>Colour palette (hex, used inside Spectre markup).</summary>
internal static class Theme
{
    public const string Orange = "#FF9F1C";
    public static readonly Spectre.Console.Color OrangeColor = new(255, 159, 28);
    public const string Violet = "#B388FF";
    public const string Green  = "#4ADE80";
    public const string Red    = "#F87171";
    public const string Yellow = "#FBBF24";
    public const string Cyan   = "#67E8F9";
    public const string Dim    = "#E6EDF3";
    public const string Faint  = "#4B5563";
    public const string Code   = "#C4B5FD";

    public const string AddFg = "#D1FAE5";
    public const string AddBg = "#12361F";
    public const string DelFg = "#FEE2E2";
    public const string DelBg = "#3F1518";
}

/// <summary>
/// Terminal symbols. Legacy Windows consoles (conhost + Consolas) have no glyph for characters such as ❯ or ⎿
/// and draw a "?" box instead, so those get plain, universally available characters.
/// Force a mode with LIBERTY_ASCII=1 or LIBERTY_UNICODE=1.
/// </summary>
internal static class Sym
{
    public static readonly bool Unicode = Detect();

    private static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("LIBERTY_ASCII") == "1") return false;
        if (Environment.GetEnvironmentVariable("LIBERTY_UNICODE") == "1") return true;
        if (!OperatingSystem.IsWindows()) return true;
        // Windows Terminal, VS Code, ConEmu, Wezterm... have modern font fallback; the old console host does not.
        return Environment.GetEnvironmentVariable("WT_SESSION") != null
            || Environment.GetEnvironmentVariable("TERM_PROGRAM") != null
            || Environment.GetEnvironmentVariable("ConEmuPID") != null;
    }

    // "›" exists in every console font (WGL4), unlike "❯" (U+276F), which Consolas does not have.
    public static string Pointer => Unicode ? "›" : ">";
    public static string Branch  => Unicode ? "⎿" : "└";
    public static string Diamond => Unicode ? "◆" : "♦";
    public static string Check   => Unicode ? "✓" : "√";
    public static string Cross   => Unicode ? "✗" : "x";
    public static string Undo    => Unicode ? "↶" : "<";
    public static string Gap     => Unicode ? "⋯" : "..";
    public static string Quote   => Unicode ? "▎" : "|";
    public static string BoxTop  => Unicode ? "╭─" : "┌─";
    public static string BoxBot  => Unicode ? "╰─" : "└─";
    public static readonly string[] Frames = Unicode
        ? ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"]
        : ["|", "/", "-", "\\"];
}
