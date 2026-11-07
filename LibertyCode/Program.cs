using LibertyCode;

// Internal: the debug window is this same program started with --debug-viewer (see DebugViewer.cs).
if (args.Length >= 2 && args[0] == "--debug-viewer")
{
    int parentPid = 0;
    for (int i = 2; i + 1 < args.Length; i++)
        if (args[i] == "--pid") int.TryParse(args[i + 1], out parentPid);
    DebugViewer.Run(args[1], parentPid);
    return;
}

UI.Init();
Paths.EnsureDirectories();

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Logger.Exception("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
TaskScheduler.UnobservedTaskException += (_, e) => Logger.Exception("Unobserved task exception", e.Exception);
AppDomain.CurrentDomain.ProcessExit += (_, _) => { Logger.Shutdown(); DebugViewer.CloseWindow(); };

AppSettings settings = SettingsManager.Load();

// `LibertyCode --debug` (or LIBERTY_DEBUG=1, or /debug on) opens the live debug window.
bool debugFlag = args.Contains("--debug") || Environment.GetEnvironmentVariable("LIBERTY_DEBUG") is "1" or "true";
args = args.Where(a => a != "--debug").ToArray();
if (settings.Debug || debugFlag) Logger.StartDebug();

// `LibertyCode .` or `LibertyCode <folder>` opens that folder as the workspace.
if (args.Length > 0 && Directory.Exists(args[0]))
{
    settings.Workspace = Path.GetFullPath(args[0]);
    SettingsManager.Save(settings);
}
else if (!settings.AutoSelect)
{
    // Default: start on /project. (In memory only; the last folder is kept for when AutoSelect is turned on.)
    settings.Workspace = "";
}

CryptoHelper.MigrateLegacy(settings.Provider);
bool needsSetup = string.IsNullOrWhiteSpace(settings.Provider)
               || string.IsNullOrWhiteSpace(settings.Model)
               || !Providers.All.ContainsKey(settings.Provider)
               || CryptoHelper.LoadApiKey(settings.Provider) is null;

try
{
    if (needsSetup) settings = await SetupWizard.RunAsync(settings);
    await new Repl(settings).RunAsync();
}
catch (OperationCanceledException)
{
    // setup cancelled with ctrl+c / ctrl+d
}
