using Spectre.Console;

namespace LibertyCode;

internal static class SetupWizard
{
    /// <summary>Provider → key → model → permission mode. Updates and returns the given settings.</summary>
    public static async Task<AppSettings> RunAsync(AppSettings s, bool firstRun = true)
    {
        AnsiConsole.Clear();
        if (firstRun)
        {
            UI.BigLogo();
            UI.Line($"  [bold {Theme.Orange}]Welcome to Liberty[/][bold {Theme.Violet}]Code[/]");
        }
        else UI.Line($"  [bold {Theme.Orange}]Change provider[/]");
        UI.Blank();

        // 1 ─ provider  (Ollama needs no API key)
        var keys = Providers.All.Keys.ToList();
        string provider = Prompts.Choose("AI provider", keys, k => Providers.All[k].Label);

        // 2 ─ API key (Ollama is local: no key needed)
        string key = "";
        ModelsResult res;
        if (provider == "ollama")
        {
            CryptoHelper.SaveApiKey("ollama", "ollama");
            key = "ollama";
            using (new Spinner("Connecting to Ollama"))
                res = await Providers.GetModelsAsync(provider, key);
            if (!res.Ok)
            {
                UI.Warn(res.Error ?? "Ollama did not respond. Make sure it is running (ollama serve).");
                res = new ModelsResult { Models = Providers.StaticModels["ollama"].ToList(), Verified = false };
            }
        }
        else
        {
            while (true)
            {
                string? typed = LineEditor.Read($"  {UI.Bold("API key")} {UI.M(Theme.Orange, "›")} ", 14, secret: true);
                if (typed == null) throw new OperationCanceledException();
                if (string.IsNullOrWhiteSpace(typed)) { UI.Warn("The key cannot be empty."); continue; }
                if (LineEditor.SecretIssue == "long") { UI.Warn("Key too long. Paste only the API key."); continue; }
                if (LineEditor.SecretIssue == "invalid") { UI.Warn("Invalid key. Use only the characters of the key (no spaces, accents or symbols)."); continue; }
                key = CryptoHelper.CleanKey(typed);

                using (new Spinner("Checking the key and fetching the models"))
                    res = await Providers.GetModelsAsync(provider, key, probeKey: true);

                if (res.Ok) break;
                if (res.AuthRejected)
                {
                    UI.Warn(res.Status == 403 && res.Error != null ? res.Error : $"{Providers.Label(provider)} rejected this key. Check it and try again.");
                    continue;
                }

                UI.Warn(res.Error ?? "Could not fetch the models.");
                var next = Prompts.Choose("What now?", new[] { "retry", "other" }, a => a == "retry" ? "Try again with the same key" : "Enter another key");
                if (next == "retry")
                {
                    using (new Spinner("Checking the key and fetching the models"))
                        res = await Providers.GetModelsAsync(provider, key, probeKey: true);
                    if (res.Ok) break;
                    UI.Warn(res.Error ?? "Still no answer.");
                }
            }
            CryptoHelper.SaveApiKey(key, provider);
        }
        Logger.Info($"API key accepted by {provider}: {Logger.Mask(key)}");
        if (!res.Verified) UI.Hint("This provider has no model list: showing the built-in one (not verified).");

        // 3 ─ model (only models the provider really offers)
        string model = Prompts.Choose($"Model ({res.Models.Count} available)", res.Models, m => m, pageSize: 14, search: true);

        // 4 ─ permissions
        var modes = new[] { PermissionMode.Ask, PermissionMode.Edits, PermissionMode.Auto };
        var mode = Prompts.Choose("Security", modes, m => UI.SecurityName(m) + "  —  " + UI.SecurityHint(m));

        s.Provider = provider;
        s.Model = model;
        s.Mode = mode;
        if (firstRun) { s.AutoSelect = false; s.Notifications = false; s.Debug = false; }
        SettingsManager.Save(s);
        Logger.Info($"Setup completed: {provider}/{model}");

        if (firstRun)
        {
            // Key accepted: wipe the terminal, thank the user, then the main screen appears.
            AnsiConsole.Clear();
            Thread.Sleep(200);
            UI.Blank();
            UI.Line($"  {UI.Violet(Loc.T("Thanks for using LibertyCode :)"))}");
            Thread.Sleep(500); // the main screen clears and redraws itself right after
        }
        else
        {
            UI.Blank();
            UI.Ok("Ready");
        }
        return s;
    }
}
