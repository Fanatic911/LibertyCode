using Spectre.Console;
using System.Text.RegularExpressions;

namespace LibertyCode;

internal sealed class Repl
{
    private static readonly string[] Commands =
    [
        "/help", "/models", "/project", "/leave", "/cd", "/clear", "/undo", "/settings",
        "/prompt", "/provider", "/log", "/exit",
    ];

    private readonly AppSettings _s;
    private string _apiKey = "";
    private Agent _agent = null!;
    private CancellationTokenSource? _turn;
    private bool _inHub; // no project open: we are on /project

    public Repl(AppSettings settings) => _s = settings;

    public async Task RunAsync()
    {
        _apiKey = CryptoHelper.LoadApiKey(_s.Provider) ?? "";
        LineEditor.LoadHistory();
        Console.CancelKeyPress += (_, e) =>
        {
            var t = _turn;
            if (t == null) return;
            e.Cancel = true;
            try { t.Cancel(); } catch { }
        };

        NewSession();
        LogSessionInfo();
        await VerifyStartupAsync();

        while (true)
        {
            UI.StatusLine(_s, _agent.Perm.Mode, _agent.Ws.Root, _agent.ContextTokens, _inHub);
            string? raw = LineEditor.ReadInput(UI.PromptMarkup, UI.PromptLength, Commands);
            if (raw == null) break;
            raw = raw.Trim();
            if (raw.Length == 0) continue;

            Logger.Trace("USER", "Input: " + Logger.Clip(raw, 500));
            try { if (!await HandleAsync(raw)) break; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Exception("Unexpected error", ex); UI.Error(ex.Message); }
            UI.Blank();
        }
        UI.Line($"  {UI.Dim("Bye.")}");
    }

    private void NewSession(bool banner = true)
    {
        // No workspace (or the Projects folder itself) = the /project hub: nothing is opened yet.
        _inHub = string.IsNullOrWhiteSpace(_s.Workspace)
              || !Directory.Exists(_s.Workspace)
              || SamePath(_s.Workspace, Paths.ProjectsDir);
        if (_inHub) _s.Workspace = "";
        string root = _inHub ? Paths.ProjectsDir : _s.Workspace;
        Directory.CreateDirectory(root);
        _agent = new Agent(_s, _apiKey, new Workspace(root));
        if (banner)
        {
            UI.Banner(_s, _agent.Perm.Mode, _agent.Ws.Root, _inHub);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private async Task<bool> HandleAsync(string raw)
    {
        if (raw.Equals("exit", StringComparison.OrdinalIgnoreCase) || raw.Equals("quit", StringComparison.OrdinalIgnoreCase)) return false;
        raw = NormalizeBareCommand(raw);
        if (_inHub && !raw.StartsWith('/')) raw = "/" + raw; // outside a project, everything typed is a command
        if (raw.StartsWith('/')) return await CommandAsync(raw);
        if (raw.StartsWith('!')) { await RunShellAsync(raw[1..].Trim()); return true; }

        if (string.IsNullOrWhiteSpace(_apiKey)) { UI.Warn("No API key. Use /provider set api-key."); return true; }
        await AskAgentAsync(raw);
        return true;
    }

    /// <summary>
    /// Lets the user type app commands without the leading slash ("project new Matcha", "help", "undo").
    /// Only unambiguous forms are accepted, so normal requests are never hijacked; everything else goes to the model.
    /// </summary>
    private static string NormalizeBareCommand(string raw)
    {
        if (raw.StartsWith('/') || raw.StartsWith('!')) return raw;
        var w = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0) return raw;
        string cmd = w[0].ToLowerInvariant();
        string second = w.Length > 1 ? w[1].ToLowerInvariant() : "";

        bool isCommand = cmd switch
        {
            "help" or "clear" or "undo" or "provider" or "leave" => w.Length == 1,
            "settings" => w.Length == 1
                         || (w.Length is 2 or 3 && second is "notification" or "notifications" or "autoselect" or "debug" or "warn" or "mode" or "security" or "language")
                         || (w.Length >= 2 && second == "set"),
            "project" => w.Length == 1
                         || ((second is "new" or "open") && w.Length >= 3)
                         || (second is "delete" or "remove" && w.Length >= 2)
                         || (second == "list" && w.Length == 2),
            "models" => w.Length <= 2,
            "cd" => w.Length >= 2,
            "log" => w.Length == 1,
            "prompt" => w.Length == 1 || (w.Length == 2 && second == "edit"),
            _ => false,
        };
        return isCommand ? "/" + raw : raw;
    }

    private async Task AskAgentAsync(string input)
    {
        using var cts = new CancellationTokenSource();
        _turn = cts;
        using var esc = new EscWatcher(cts);
        try { await _agent.RunTurnAsync(input, cts.Token); }
        finally { _turn = null; }

        if (_s.Notifications && !cts.IsCancellationRequested)
        {
            string project = new DirectoryInfo(_agent.Ws.Root).Name;
            Notifier.Send("LibertyCode · " + project, _agent.FinalReply ?? "Stopped: your input is needed.");
        }
    }

    // ── Slash commands ────────────────────────────────────────────────────────
    private async Task<bool> CommandAsync(string raw)
    {
        var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string cmd = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/exit": case "/quit": return false;
            case "/help": ShowHelp(); break;
            case "/clear": NewSession(); break;
            case "/models": await ModelsAsync(arg); break;
            case "/project": ProjectCommand(arg); break;
            case "/leave": LeaveProject(); break;
            case "/cd": ChangeDir(arg); break;
            case "/undo": UndoLast(); break;
            case "/settings": SettingsCommand(arg); break;
            case "/prompt":
                if (arg.Equals("edit", StringComparison.OrdinalIgnoreCase)) SystemPrompt.Edit(); else SystemPrompt.View();
                break;
            case "/provider": await ChangeProviderAsync(arg); break;
            case "/log":
                var lines = Logger.Tail();
                if (lines.Length == 0) UI.Hint("The log is empty.");
                foreach (var l in lines) UI.Line($"  {UI.Dim(l)}");
                break;
            default:
                UI.Warn($"Unknown command {cmd}. Type /help.");
                break;
        }
        return true;
    }

    private void ShowHelp()
    {
        var table = new Table
        {
            Border = Sym.Unicode ? TableBorder.Rounded : TableBorder.Square,
            BorderStyle = new Style(Theme.OrangeColor),
            Expand = false,
        };
        table.AddColumn(new TableColumn(UI.Dim(Loc.T("Command"))).NoWrap());
        table.AddColumn(new TableColumn(UI.Dim(Loc.T("Description"))));

        (string, string)[] rows =
        [
            ("/models [name | key [api-key]]",       "Change model (searchable list) or API key, anywhere"),
            ("/settings set Security Strict/Mid/Off", "Permission level"),
            ("/undo",                               "Revert the file changes of the last request"),
            ("/project [list|new|open|delete]",     "List, create, open or delete a project"),
            ("/leave",                              "Leave the project and go back to /project"),
            ("/cd <path>",                          "Use another folder as workspace"),
            ("/clear",                              "New conversation"),
            ("/provider [set <name> | set api-key <key>]", "List providers / switch provider / set its API key"),
            ("/settings [key value]",               "Show settings, or set a value"),
            ("/prompt [edit]",                      "Extra instructions added to the system prompt"),
            ("/log",                                "Show the log"),
            ("/exit",                               "Quit"),
        ];
        UI.Blank();
        foreach (var (c, d) in rows)
            table.AddRow(UI.M(Theme.Orange, c), UI.Dim(Loc.T(d)));
        AnsiConsole.Write(new Padder(table, new Padding(2, 0, 0, 0)));
    }

    private static string LangName(string code)
    {
        try
        {
            string n = System.Globalization.CultureInfo.GetCultureInfo(code).NativeName;
            int i = n.IndexOf(" (");
            if (i > 0) n = n[..i];
            return char.ToUpperInvariant(n[0]) + n[1..];
        }
        catch { return code; }
    }

    private void ShowSettings()
    {
        var g = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(3)).AddColumn();
        g.AddRow(UI.Dim(Loc.T("Provider:")), Markup.Escape(Providers.Label(_s.Provider)));
        g.AddRow(UI.Dim(Loc.T("Model:")), UI.Violet(_s.Model));
        g.AddRow(UI.Dim(Loc.T("Security:")), UI.M(UI.ModeColor(_agent.Perm.Mode), UI.SecurityName(_agent.Perm.Mode)) + UI.Dim("  (" + UI.SecurityHint(_agent.Perm.Mode) + ")"));
        g.AddRow(UI.Dim(Loc.T("Language:")), Markup.Escape(LangName(_s.Language)));
        g.AddRow(UI.Dim(Loc.T("Workspace:")), _inHub ? UI.M(Theme.Orange, UI.HubLabel) : Markup.Escape(UI.Short(_agent.Ws.Root)));
        g.AddRow(UI.Dim(Loc.T("Autoselect:")), OnOff(_s.AutoSelect));
        g.AddRow(UI.Dim(Loc.T("Notification:")), OnOff(_s.Notifications));
        g.AddRow(UI.Dim(Loc.T("Debug:")), OnOff(Logger.DebugEnabled));
        g.AddRow(UI.Dim(Loc.T("Data:")), Markup.Escape(UI.Short(Paths.DataDir)));
        UI.Blank();
        AnsiConsole.Write(new Padder(g, new Padding(2, 0, 0, 0)));
    }

    private static string OnOff(bool v) => v ? UI.M(Theme.Green, "true") : UI.Dim("false");

    private static bool? ParseBool(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "true" or "on" or "yes" or "1" or "oui" => true,
        "false" or "off" or "no" or "0" or "non" => false,
        _ => null,
    };

    /// <summary>/settings  ·  /settings set &lt;key&gt; &lt;value&gt;  ·  /settings set Security Strict/Mid/Off</summary>
    private void SettingsCommand(string arg)
    {
        if (arg.Length == 0) { ShowSettings(); return; }

        // Support both "/settings set key val" and legacy "/settings key val"
        var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int keyIdx = 0;
        if (p.Length > 0 && p[0].Equals("set", StringComparison.OrdinalIgnoreCase)) keyIdx = 1;

        if (keyIdx >= p.Length) { ShowSettings(); return; }

        string key = p[keyIdx].ToLowerInvariant();
        string rawVal = p.Length > keyIdx + 1 ? p[keyIdx + 1] : "";
        bool? boolVal = rawVal.Length > 0 ? ParseBool(rawVal) : null;

        switch (key)
        {
            case "notification": case "notifications":
                _s.Notifications = boolVal ?? !_s.Notifications;
                SettingsManager.Save(_s);
                UI.Ok($"Notification: {(_s.Notifications ? "true" : "false")}");
                if (_s.Notifications) Notifier.Send("LibertyCode", "Notifications are on.");
                break;
            case "autoselect":
                _s.AutoSelect = boolVal ?? !_s.AutoSelect;
                SettingsManager.Save(_s);
                UI.Ok($"AutoSelect: {(_s.AutoSelect ? "true" : "false")}");
                UI.Hint(_s.AutoSelect ? "LibertyCode will reopen where you left off." : "LibertyCode will start on /project.");
                break;
            case "debug":
                SetDebug(boolVal ?? !Logger.DebugEnabled);
                break;
            case "language":
                if (rawVal.Length < 2) { UI.Warn("Usage: /settings set language fr"); return; }
                _s.Language = rawVal.ToLowerInvariant();
                Loc.Lang = _s.Language;
                SettingsManager.Save(_s);
                UI.Ok($"Language: {LangName(_s.Language)}");
                break;
            case "security": case "mode": case "warn":
                SetSecurity(rawVal);
                break;
            default:
                UI.Warn("Usage: /settings set <name> <value>  (debug true/false)");
                break;
        }
    }

    /// <summary>Checks the API key and the current model against the provider at startup (short timeout, never blocks when offline).</summary>
    private async Task VerifyStartupAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_s.Model)) return;

        if (_s.Provider == "nvidia")
        {
            // Test only the selected model now; the full scan of the others goes on in the background.
            int st;
            using (new Spinner("Checking API key and models"))
                st = await Providers.TestModelAsync("nvidia", _apiKey, _s.Model);
            StartBackgroundScan();
            if (st == 401) { UI.Warn($"{Providers.Label(_s.Provider)} rejected your API key. Use /provider set api-key to enter a valid one."); return; }
            if (Providers.IsUnusable(st))
            {
                UI.Warn(Loc.T("This model is not usable with your key. Pick another one."));
                await ModelAsync("");
            }
            else if (st != 0) Providers.MarkWorking(_apiKey, _s.Model);
            return;
        }

        ModelsResult r;
        using (new Spinner("Checking API key and models"))
            r = await Providers.GetModelsAsync(_s.Provider, _apiKey, timeoutSec: 8);

        if (r.AuthRejected)
        {
            UI.Warn($"{Providers.Label(_s.Provider)} rejected your API key. Use /provider set api-key to enter a valid one.");
            return;
        }
        if (r.Error != null)
        {
            UI.Hint("Could not check the model list: " + r.Error);
            return;
        }
        if (!r.Verified || r.Models.Contains(_s.Model, StringComparer.OrdinalIgnoreCase)) return;

        UI.Warn($"The model '{_s.Model}' does not exist on {Providers.Label(_s.Provider)} (any more). Pick one from the real list:");
        await ModelAsync("", r);
    }

    private static void ShowModelsError(ModelsResult r)
    {
        UI.Error(r.Error ?? "Could not fetch the models.");
        if (r.AuthRejected) UI.Hint("Use /provider set api-key to update the API key.");
    }

    private void SetModel(string model)
    {
        _s.Model = model; SettingsManager.Save(_s);
        _agent.Reconfigure();
        Logger.Info($"Model set to {model}");
        UI.Ok($"Model set to {model}");
    }

    /// <summary>NVIDIA lists models that its hosted API does not serve: test the chosen one before using it.</summary>
    private async Task<bool> ModelUsableAsync(string model)
    {
        if (_s.Provider != "nvidia" || Providers.IsKnownWorking(_apiKey, model)) return true;
        int st;
        using (new Spinner(Loc.T("Testing the model")))
            st = await Providers.TestModelAsync(_s.Provider, _apiKey, model);
        if (!Providers.IsUnusable(st)) return true;
        Providers.MarkBroken(_apiKey, model);
        UI.Warn(Loc.T("This model is not usable with your key. Pick another one."));
        return false;
    }

    private Task? _scan;
    private CancellationTokenSource? _scanCts;

    /// <summary>Silent and slow: tests the NVIDIA models one by one in the background (the current model is tested first, before this).</summary>
    private void StartBackgroundScan(List<string>? models = null)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        string key = _apiKey;
        _scan = Task.Run(async () =>
        {
            models ??= (await Providers.GetModelsAsync("nvidia", key, timeoutSec: 20)).Models;
            await Providers.ScanSlowlyAsync(key, models, cts.Token);
        });
    }

    /// <summary>NVIDIA lists models its hosted API does not serve: hide the ones already known broken, scan the rest in the background.</summary>
    private ModelsResult FilterNvidia(ModelsResult r)
    {
        var ok = r.Models.Where(m => !Providers.IsKnownBroken(_apiKey, m)).ToList();
        int pending = ok.Count(m => !Providers.IsKnownWorking(_apiKey, m));
        if (pending > 0)
        {
            if (_scan == null || _scan.IsCompleted || _scanCts!.IsCancellationRequested) StartBackgroundScan(r.Models);
            UI.Hint($"{pending} models are still being checked in the background.");
        }
        return ok.Count == 0 ? r : new ModelsResult { Models = ok, Status = r.Status, Verified = true };
    }

    /// <summary>/models  ·  /models &lt;name&gt;  ·  /models key [api-key]. Works anywhere, no project needed.</summary>
    private async Task ModelsAsync(string arg)
    {
        bool key = arg.Equals("key", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("key ", StringComparison.OrdinalIgnoreCase);
        if (arg.Length == 0 && _s.Provider != "ollama")
            key = Prompts.Choose("Models", new[] { false, true }, k => k ? "Change API key" : "Change model");
        if (!key) { await ModelAsync(arg); return; }
        if (_s.Provider == "ollama") { UI.Hint("Ollama needs no API key."); return; }
        await SetKeyAsync(arg.Length > 3 ? arg[4..].Trim() : "");
    }

    private async Task ModelAsync(string arg, ModelsResult? preloaded = null)
    {
        ModelsResult r;
        if (preloaded != null) r = preloaded;
        else
        {
            using (new Spinner("Fetching models"))
                r = await Providers.GetModelsAsync(_s.Provider, _apiKey);
        }

        if (r.Error != null)
        {
            ShowModelsError(r);
            // Typed name + provider unreachable (not a key problem): accept it, but say it is unchecked.
            if (arg.Length > 0 && !r.AuthRejected)
            {
                UI.Warn("The name could not be checked.");
                SetModel(arg);
            }
            return;
        }
        if (!r.Verified) UI.Hint("This provider has no model list: showing the built-in one (not verified).");
        if (_s.Provider == "nvidia" && r.Ok) r = FilterNvidia(r);
        LineEditor.ModelNames = r.Models;

        if (arg.Length > 0)
        {
            string? exact = r.Models.FirstOrDefault(m => m.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (exact == null && r.Verified)
            {
                var near = r.Models.Where(m => m.Contains(arg, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
                if (near.Count == 1) exact = near[0]; // autocomplete: a single partial match is the model
                else
                {
                    UI.Warn($"'{arg}' is not a model available on {Providers.Label(_s.Provider)}.");
                    UI.Hint(near.Count > 0 ? "Did you mean: " + string.Join(", ", near) : "Type /models to see the real list.");
                    return;
                }
            }
            if (!await ModelUsableAsync(exact ?? arg)) return;
            SetModel(exact ?? arg);
            return;
        }

        while (true)
        {
            var shown = r.Models.Where(m => !Providers.IsKnownBroken(_apiKey, m)).ToList();
            if (shown.Count == 0) shown = r.Models;
            string pick = Prompts.Choose($"Model ({shown.Count} available)", shown, m => m == _s.Model ? m + "  (current)" : m, 14, search: true)
                .Replace("  (current)", "");
            if (!await ModelUsableAsync(pick)) continue;
            SetModel(pick);
            break;
        }
    }

    // ── Debug mode ────────────────────────────────────────────────────────────
    private void LogSessionInfo()
    {
        if (!Logger.DebugEnabled) return;
        Logger.Trace("DEBUG", $"Settings: provider={_s.Provider}, model={_s.Model}, security={UI.SecurityName(_s.Mode)}, language={_s.Language}, key={Logger.Mask(_apiKey)}");
        Logger.Trace("DEBUG", $"Workspace: {(_inHub ? "(none - /project hub)" : _agent.Ws.Root)}");
    }

    private void SetDebug(bool on)
    {
        _s.Debug = on;
        SettingsManager.Save(_s);
        if (on)
        {
            bool opened = Logger.StartDebug();
            LogSessionInfo();
            if (opened) UI.Ok("Debug mode on: live window opened");
            else
            {
                UI.Warn("Debug mode on, but the window could not be opened.");
                UI.Hint("Follow the log yourself: " + Paths.DebugFile);
            }
        }
        else
        {
            Logger.StopDebug();
            UI.Ok("Debug mode off");
        }
    }

    private void SetSecurity(string arg)
    {
        PermissionMode? m = arg.ToLowerInvariant() switch
        {
            "strict" or "ask"   => PermissionMode.Ask,
            "mid" or "edits"    => PermissionMode.Edits,
            "off" or "auto"     => PermissionMode.Auto,
            _ => null,
        };
        if (m == null)
        {
            if (arg.Length > 0) { UI.Warn("Security values: Strict, Mid, Off.  Usage: /settings set Security Strict/Mid/Off"); return; }
            var all = new[] { PermissionMode.Ask, PermissionMode.Edits, PermissionMode.Auto };
            m = Prompts.Choose("Security", all, x => UI.SecurityName(x) + "  —  " + UI.SecurityHint(x));
        }
        _s.Mode = m.Value;
        _agent.Perm.Mode = m.Value;
        SettingsManager.Save(_s);
        UI.Ok($"Security: {UI.SecurityName(m.Value)}");
    }

    private void UndoLast()
    {
        var report = _agent.Undo.UndoLast();
        if (report.Count == 0) { UI.Hint("Nothing to undo."); return; }
        foreach (var (path, action) in report)
        {
            string color = action.StartsWith("failed") ? Theme.Red : Theme.Green;
            UI.Line($"  {UI.M(color, Sym.Undo)} {UI.Dim(action)} {Markup.Escape(_agent.Ws.Rel(path))}");
        }
    }

    private async Task SetKeyAsync(string inline = "", bool pickModel = true)
    {
        string? key = inline.Length > 0 ? inline : LineEditor.Read($"  {UI.Bold("New API key")} {UI.M(Theme.Orange, "›")} ", 18, secret: true);
        if (string.IsNullOrWhiteSpace(key)) { UI.Hint("Cancelled."); return; }
        if (LineEditor.SecretIssue == "long") { UI.Warn("Key too long. Paste only the API key."); return; }
        if (LineEditor.SecretIssue == "invalid") { UI.Warn("Invalid key. Use only the characters of the key (no spaces, accents or symbols)."); return; }
        key = CryptoHelper.CleanKey(key);

        ModelsResult r;
        using (new Spinner("Checking API key"))
            r = await Providers.GetModelsAsync(_s.Provider, key, probeKey: true);

        if (r.AuthRejected)
        {
            UI.Error($"{Providers.Label(_s.Provider)} rejected this key. Nothing was saved.");
            return;
        }
        if (r.Error != null) UI.Warn("Could not check the key (" + r.Error + "). Saved anyway.");

        CryptoHelper.SaveApiKey(key, _s.Provider);
        _apiKey = key;
        _agent.Reconfigure(_apiKey);
        if (_s.Provider == "nvidia") StartBackgroundScan();
        Logger.Info("API key updated: " + Logger.Mask(key));
        UI.Ok(r.Error == null ? "API key valid and saved (encrypted)" : "API key saved (encrypted)");

        if (pickModel && r.Verified && !r.Models.Contains(_s.Model, StringComparer.OrdinalIgnoreCase))
        {
            UI.Warn($"The model '{_s.Model}' is not available with this key. Pick one:");
            await ModelAsync("", r);
        }
    }

    private async Task ChangeProviderAsync(string arg = "")
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            string pick;
            try
            {
                pick = Prompts.Choose("AI provider", Providers.All.Keys.ToList(),
                    k => Providers.All[k].Label + (k == _s.Provider ? "  (current)" : ""));
            }
            catch (OperationCanceledException) { return; }
            await SwitchProviderAsync(pick);
            return;
        }

        string rest = parts.Length > 1 ? parts[1] : "";
        if (rest.Equals("api-key", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("api-key ", StringComparison.OrdinalIgnoreCase))
        {
            if (_s.Provider == "ollama") { UI.Hint("Ollama needs no API key."); return; }
            await SetKeyAsync(rest.Length > 7 ? rest[8..].Trim() : "");
            return;
        }

        string? provider = Providers.All.Keys.FirstOrDefault(k => k.Equals(rest, StringComparison.OrdinalIgnoreCase))
                        ?? Providers.All.Keys.FirstOrDefault(k => Providers.All[k].Label.Equals(rest, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            UI.Warn(rest.Length == 0 ? "Usage: /provider set <name>" : $"Unknown provider '{rest}'. Type /provider to see the list.");
            return;
        }
        await SwitchProviderAsync(provider);
    }

    private async Task SwitchProviderAsync(string provider)
    {
        if (provider == _s.Provider) { UI.Hint($"Already using {Providers.Label(provider)}."); return; }

        string prevProvider = _s.Provider, prevModel = _s.Model, prevKey = _apiKey;
        _s.Provider = provider;
        _scanCts?.Cancel();
        string? saved = provider == "ollama" ? "ollama" : CryptoHelper.LoadApiKey(provider);
        try
        {
            if (saved != null && provider != "ollama"
                && Prompts.Choose("An old API key was found. Use it?", new[] { true, false }, y => Loc.T(y ? "Yes" : "No")) == false)
                saved = null;
            if (saved != null)
            {
                _apiKey = saved;
                _agent.Reconfigure(_apiKey);
            }
            else
            {
                await SetKeyAsync(pickModel: false);
                saved = CryptoHelper.LoadApiKey(provider);
                if (saved == null) throw new OperationCanceledException();
            }
            await ModelAsync("");
        }
        catch (OperationCanceledException)
        {
            _s.Provider = prevProvider; _s.Model = prevModel; _apiKey = prevKey;
            _agent.Reconfigure(_apiKey);
            SettingsManager.Save(_s);
            UI.Hint($"Cancelled: still on {Providers.Label(prevProvider)}.");
            return;
        }
        SettingsManager.Save(_s);
        Logger.Info($"Provider set to {provider}");
        NewSession();
    }

    // ── Workspace / projects ──────────────────────────────────────────────────
    private void SwitchWorkspace(string path)
    {
        _s.Workspace = Path.GetFullPath(path);
        SettingsManager.Save(_s);
        NewSession();
    }

    private void ChangeDir(string arg)
    {
        if (arg.Length == 0) { UI.Hint("Usage: /cd <path>"); return; }
        arg = arg.Trim('"');
        if (arg.StartsWith('~')) arg = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + arg[1..];
        string full = Path.GetFullPath(Path.IsPathRooted(arg) ? arg : Path.Combine(_agent.Ws.Root, arg));
        if (!Directory.Exists(full)) { UI.Warn("Folder not found: " + full); return; }
        SwitchWorkspace(full);
    }

    private List<string> ListProjects() =>
        Directory.Exists(Paths.ProjectsDir)
            ? Directory.GetDirectories(Paths.ProjectsDir).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    private bool IsCurrent(string name) =>
        !_inHub && SamePath(Path.Combine(Paths.ProjectsDir, name), _agent.Ws.Root);

    /// <summary>Framed table of the projects: used on startup and by /project list.</summary>
    private void ShowProjects()
    {
        var dirs = ListProjects();
        UI.Blank();
        if (dirs.Count == 0) { UI.Hint("No projects yet."); return; }

        var table = new Table
        {
            Border = Sym.Unicode ? TableBorder.Rounded : TableBorder.Square,
            BorderStyle = new Style(Theme.OrangeColor),
            Expand = false,
        };
        table.AddColumn(new TableColumn(UI.Dim(Loc.T("Project"))).NoWrap());
        table.AddColumn(new TableColumn(UI.Dim(Loc.T("Created"))).NoWrap());
        table.AddColumn(new TableColumn(UI.Dim(Loc.T("Modified"))).NoWrap());
        table.AddColumn(new TableColumn(UI.Dim(Loc.T("Files"))).NoWrap().RightAligned());

        foreach (var d in dirs)
        {
            var info = new DirectoryInfo(Path.Combine(Paths.ProjectsDir, d));
            bool cur = IsCurrent(d);
            string label = cur
                ? $"{UI.M(Theme.Orange, Sym.Pointer)} {UI.Bold(d)}"
                : $" {Markup.Escape(d)}";
            int files = 0;
            try { files = info.EnumerateFiles("*", SearchOption.AllDirectories).Count(f => !f.FullName.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)); } catch { }
            table.AddRow(label,
                UI.Dim(info.CreationTime.ToString("yyyy-MM-dd")),
                UI.Dim(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")),
                UI.Dim(files.ToString()));
        }
        AnsiConsole.Write(table);
    }

    private void CreateProject(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { UI.Warn("That name has invalid characters."); return; }
        string path = Path.Combine(Paths.ProjectsDir, name);
        if (Directory.Exists(path)) { UI.Warn($"Project '{name}' already exists."); return; }
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "README.md"), $"# {name}\n\nCreated with LibertyCode.\n");
        Logger.Info("Project created: " + name);
        SwitchWorkspace(path);
    }

    private void LeaveProject()
    {
        if (_inHub) { UI.Hint("You are already on /project."); return; }
        _s.Workspace = "";
        SettingsManager.Save(_s);
        NewSession();
    }

    private void DeleteProject(string name)
    {
        var dirs = ListProjects();
        if (dirs.Count == 0) { UI.Hint("No projects to delete."); return; }

        if (name.Length == 0)
            name = Prompts.Choose("Delete which project?", dirs, d => d);
        else
        {
            string? match = dirs.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
            if (match == null) { UI.Warn($"Project '{name}' not found. Try /project list."); return; }
            name = match;
        }

        bool delete = Prompts.Choose($"Delete '{name}' and everything inside it? This cannot be undone.",
            new[] { true, false }, v => Loc.T(v ? "Yes, delete it" : "No, keep it"));
        if (!delete) { UI.Hint("Cancelled."); return; }

        string path = Path.Combine(Paths.ProjectsDir, name);
        bool wasCurrent = !_inHub && SamePath(path, _agent.Ws.Root);
        if (wasCurrent)
        {
            // Step out of the project before deleting it.
            _s.Workspace = "";
            SettingsManager.Save(_s);
            NewSession(banner: false);
        }

        try
        {
            ForceDelete(path);
            Logger.Info("Project deleted: " + name);
        }
        catch (Exception ex)
        {
            UI.Error($"Could not delete '{name}': {ex.Message}");
            if (wasCurrent) NewSession();
            return;
        }

        if (wasCurrent) NewSession();
        UI.Ok($"Project '{name}' deleted");
    }

    /// <summary>Deletes a folder even when it holds read-only files (typical of .git on Windows).</summary>
    private static void ForceDelete(string path)
    {
        if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path, false); // a link: remove the link only, never what it points to
            return;
        }
        foreach (var e in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(e, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, true);
    }

    private void ProjectCommand(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        string name = parts.Length > 1 ? parts[1] : "";

        switch (sub)
        {
            case "list":
                ShowProjects();
                break;
            case "new":
            {
                if (name.Length == 0) { UI.Hint("Usage: /project new <name>"); return; }
                CreateProject(name);
                break;
            }
            case "open":
            {
                if (name.Length == 0) { UI.Hint("Usage: /project open <name>"); return; }
                string path = Path.Combine(Paths.ProjectsDir, name);
                if (!Directory.Exists(path)) { UI.Warn($"Project '{name}' not found. Try /project list."); return; }
                SwitchWorkspace(path);
                break;
            }
            case "delete": case "remove":
                DeleteProject(name);
                break;
            default:
                UI.Hint("Usage: /project [list | new <name> | open <name> | delete <name>]");
                break;
        }
    }

    // ── !command ──────────────────────────────────────────────────────────────
    private async Task RunShellAsync(string command)
    {
        if (command.Length == 0) { UI.Hint("Usage: !<command>"); return; }
        UI.ToolHeader("Run", command);

        using var cts = new CancellationTokenSource();
        _turn = cts;
        using var esc = new EscWatcher(cts);
        try
        {
            (int code, string output, bool timedOut) r;
            using (new Spinner("Running"))
                r = await Shell.RunAsync(command, _agent.Ws.Root, 300, cts.Token);

            var outLines = r.output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            int keep = r.code == 0 ? 3 : 8;
            if (outLines.Count > keep) UI.Line($"     {UI.Dim(Loc.T($"… +{outLines.Count - keep} lines"))}");
            foreach (var l in outLines.TakeLast(keep))
                UI.Line($"     {UI.Dim(l.Length > UI.Width - 7 ? l[..Math.Max(1, UI.Width - 8)] + "…" : l)}");
            UI.ToolLine(r.timedOut ? UI.M(Theme.Red, "Timed out")
                : r.code == 0 ? $"{UI.M(Theme.Green, Sym.Check)} {UI.Dim("exit 0")}" : $"{UI.M(Theme.Red, Sym.Cross)} {UI.M(Theme.Red, "exit " + r.code)}");
        }
        catch (OperationCanceledException) { UI.ToolLine(UI.Dim(Loc.T("Interrupted"))); }
        finally { _turn = null; }
    }
}
