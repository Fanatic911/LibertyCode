using Newtonsoft.Json.Linq;

namespace LibertyCode;

/// <summary>The agent loop: stream the model, run its tool calls, feed results back, repeat.</summary>
internal sealed class Agent
{
    private const int MaxSteps = 30;

    private readonly AppSettings _s;
    private string _apiKey;
    private readonly List<Msg> _history = [];
    private bool _toolsOff;
    private const int MaxMalformedRetries = 2;

    public Workspace Ws { get; }
    public Permissions Perm { get; }
    public UndoStack Undo { get; } = new();
    public int ContextTokens { get; private set; }

    /// <summary>The model's final answer of the last turn; null when the turn was interrupted or stopped early.</summary>
    public string? FinalReply { get; private set; }

    public Agent(AppSettings s, string apiKey, Workspace ws)
    {
        _s = s; _apiKey = apiKey; Ws = ws;
        Perm = new Permissions(s.Mode);
    }

    /// <summary>Call after the model or the API key changed: keeps the conversation, re-enables tools.</summary>
    public void Reconfigure(string? apiKey = null)
    {
        if (apiKey != null) _apiKey = apiKey;
        _toolsOff = false;
    }

    public async Task RunTurnAsync(string input, CancellationToken ct)
    {
        Undo.BeginTurn();
        FinalReply = null;
        Logger.Trace("USER", $"Turn start - workspace={Ws.Root}, security={UI.SecurityName(Perm.Mode)}\n{Logger.Clip(input, 1500)}");
        var userMsg = new Msg { Role = "user", Text = input };
        _history.Add(userMsg);
        bool progressed = false; // true once the model answered at least once this turn
        int errorStreak = 0;     // consecutive failed tool calls: small models can spiral, so we cut it short
        var seen = new Dictionary<string, int>(); // identical calls repeated in this turn
        int malformed = 0;       // the provider rejected a badly formed tool call (small models do this)

        for (int step = 0; step < MaxSteps; step++)
        {
            TrimContext();
            string system = SystemPrompt.Build(_s, Ws, !_toolsOff);
            Logger.Trace("STEP", $"Step {step + 1}/{MaxSteps}: calling {_s.Provider}/{_s.Model} - {_history.Count} message(s) in history, system prompt {system.Length} chars, tools {(_toolsOff ? "OFF" : "on")}, last known context {ContextTokens} tokens");
            var md = new MarkdownStream();
            var leak = new LeakFilter();
            var spinner = new Spinner("Thinking");
            bool first = true;

            LlmResult result;
            try
            {
                result = await LlmClient.StreamAsync(_s, _apiKey, system, _history, _toolsOff ? null : Tools.Specs, text =>
                {
                    string visible = leak.Push(text);
                    if (visible.Length == 0) return;
                    if (first) { spinner.Stop(); UI.Blank(); first = false; }
                    md.Feed(visible);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                spinner.Stop(); md.Flush();
                UI.ToolLine(UI.Dim(Loc.T("Interrupted")));
                Logger.Trace("STEP", "Turn interrupted by the user (Esc / Ctrl+C) while waiting for the model");
                if (!progressed) _history.Remove(userMsg);
                return;
            }
            catch (LlmException ex) when (ex.Status == 400 && IsMalformedToolCall(ex.Message) && malformed < MaxMalformedRetries)
            {
                // Not "tools unsupported": the model just produced an invalid call. Try again instead of giving up.
                spinner.Stop(); md.Flush();
                malformed++;
                Logger.Warn($"Malformed tool call, retrying ({malformed}/{MaxMalformedRetries}): " + ex.Message);
                step--;
                continue;
            }
            catch (LlmException ex) when (ex.Status == 400 && !_toolsOff && !IsMalformedToolCall(ex.Message) && MentionsTools(ex.Message))
            {
                spinner.Stop(); md.Flush();
                _toolsOff = true;
                Logger.Trace("STEP", "Provider refused the tools: switching to chat only for this model");
                UI.Warn("This model doesn't support tools: chat only. Pick another with /models for file editing.");
                step--;
                continue;
            }
            catch (LlmException ex)
            {
                spinner.Stop(); md.Flush();
                ShowError(ex);
                if (!progressed) _history.Remove(userMsg);
                return;
            }
            finally
            {
                spinner.Stop();
            }
            // Some models print tool calls as text: hide them and run them as real calls.
            var (clean, textCalls) = TextToolCalls.Extract(result.Text);
            string rest = leak.Rest(clean);
            if (rest.Length > 0) { if (first) { UI.Blank(); first = false; } md.Feed(rest); }
            md.Flush();
            if (result.Calls.Count == 0) result.Calls.AddRange(textCalls);
            result.Text = clean;
            progressed = true;

            Logger.Trace("STEP", $"Model answered: {result.Text.Length} chars, {result.Calls.Count} tool call(s), tokens in={result.InTokens} out={result.OutTokens}");
            if (result.InTokens > 0) ContextTokens = result.InTokens;
            _history.Add(new Msg { Role = "assistant", Text = result.Text, Calls = result.Calls });
            if (result.Calls.Count == 0)
            {
                FinalReply = result.Text;
                Logger.Trace("STEP", $"Turn finished: final answer ({result.Text.Length} chars), {step + 1} step(s)");
                return;
            }

            for (int i = 0; i < result.Calls.Count; i++)
            {
                var call = result.Calls[i];
                ToolOutput output;
                try
                {
                    output = await ExecuteAsync(call, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    UI.ToolLine(UI.Dim(Loc.T("Interrupted")));
                    Logger.Trace("STEP", $"Turn interrupted during tool '{call.Name}'");
                    for (int j = i; j < result.Calls.Count; j++)
                        _history.Add(new Msg { Role = "tool", CallId = result.Calls[j].Id, Text = "Cancelled by the user.", IsError = true });
                    return;
                }
                string key = call.Name + call.Args.ToString(Newtonsoft.Json.Formatting.None);
                seen[key] = seen.GetValueOrDefault(key) + 1;
                errorStreak = output.IsError ? errorStreak + 1 : 0;

                string forModel = output.ForModel;
                if (seen[key] >= (output.IsError ? 2 : 3))
                    forModel += $"\n[This exact call was already made {seen[key]} times this turn. Do not repeat it: change approach, or stop and tell the user what is blocking you.]";
                _history.Add(new Msg { Role = "tool", CallId = call.Id, Text = forModel, IsError = output.IsError });
            }

            if (errorStreak >= 5)
            {
                UI.Warn("Several tool calls failed in a row, so I stopped. Tell me what to do next, or try /models with a stronger model.");
                Logger.Trace("STEP", "Turn stopped: 5 failed tool calls in a row");
                return;
            }
        }

        Logger.Trace("STEP", $"Turn stopped: reached the {MaxSteps}-step limit");
        UI.Warn($"Stopped after {MaxSteps} steps. Send a message to continue.");
    }

    private async Task<ToolOutput> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        var (name, arg) = Tools.Describe(call.Name, call.Args);
        Logger.Trace("TOOL", $"Model requests tool '{call.Name}' (id {call.Id})\n{Logger.Clip(call.Args.ToString(Newtonsoft.Json.Formatting.None), 1500)}");
        UI.Blank();
        UI.ToolHeader(name, arg);

        Plan plan;
        try { plan = Tools.Prepare(Ws, Undo, call.Name, call.Args); }
        catch (ToolError e)
        {
            UI.ToolError(e.Message);
            Logger.Trace("TOOL", $"'{call.Name}' rejected before running: {e.Message}");
            return new ToolOutput { ForModel = "Error: " + e.Message + (e.ModelHint != null ? "\n" + e.ModelHint : ""), IsError = true };
        }
        catch (Exception e)
        {
            UI.ToolError(e.Message);
            Logger.Exception($"Tool '{call.Name}' failed to prepare", e);
            return new ToolOutput { ForModel = "Error: " + e.Message, IsError = true };
        }

        if (plan.Headline != null) UI.ToolLine(plan.Headline);
        if (plan.Preview != null) UI.Lines(plan.Preview);

        bool needsAsk = Perm.Needs(plan);
        Logger.Trace("TOOL", $"Prepared '{call.Name}': kind={plan.Kind}, dangerous={plan.Dangerous}, asks the user={needsAsk} (security {UI.SecurityName(Perm.Mode)})");
        if (needsAsk)
        {
            var (decision, note) = Perm.Ask(plan);
            Logger.Trace("TOOL", $"User decision: {decision}{(note != null ? " - " + note : "")}");
            if (decision == Decision.No)
            {
                UI.ToolLine($"{UI.M(Theme.Red, Sym.Cross)} {UI.Dim(Loc.T("Declined"))}{(note != null ? UI.Dim(" · " + note) : "")}");
                return new ToolOutput
                {
                    IsError = true,
                    ForModel = "The user declined this action." + (note != null ? $" Their instruction: {note}" : " Do not retry it; ask what they want instead."),
                };
            }
            if (decision == Decision.Always) Perm.Remember(plan);
        }

        ToolOutput o;
        Spinner? spin = plan.Kind == ToolKind.Command ? new Spinner("Running") : null;
        try { o = await plan.Run(ct); }
        catch (OperationCanceledException) { spin?.Stop(); throw; }
        catch (ToolError e) { o = new ToolOutput { IsError = true, ForModel = "Error: " + e.Message, Summary = UI.M(Theme.Red, Sym.Cross + " " + e.Message) }; }
        catch (Exception e)
        {
            Logger.Exception($"Tool '{call.Name}' crashed", e);
            o = new ToolOutput { IsError = true, ForModel = "Error: " + e.Message, Summary = UI.M(Theme.Red, Sym.Cross + " " + e.Message) };
        }
        finally { spin?.Stop(); }

        if (o.Summary.Length > 0) UI.ToolLine(o.Summary);
        if (o.Details != null) UI.Lines(o.Details);
        Logger.Info($"{call.Name} {arg}");
        Logger.Trace("TOOL", $"Result of '{call.Name}': {(o.IsError ? "ERROR" : "ok")}\n{Logger.Clip(o.ForModel, 2000)}");
        return o;
    }

    private static bool IsMalformedToolCall(string msg) =>
        msg.Contains("Failed to call a function", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("tool_use_failed", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("failed_generation", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsTools(string msg) =>
        msg.Contains("tool", StringComparison.OrdinalIgnoreCase) || msg.Contains("function", StringComparison.OrdinalIgnoreCase);

    private static void ShowError(LlmException ex)
    {
        Logger.Error($"HTTP {ex.Status}: {ex.Message}");
        string msg = ex.Status switch
        {
            401 or 403 => "Authentication failed. Check your API key with /provider set api-key.",
            404 => "Model not found, or no access to it. Pick another with /models.",
            400 when IsMalformedToolCall(ex.Message) => "The model keeps producing invalid tool calls. Try again, rephrase, or pick a stronger model with /models.",
            429 => "Rate limit reached. Wait a moment and try again.",
            _ => ex.Message,
        };
        UI.Blank();
        UI.Error(msg);
        if (ex.Status is not (401 or 403 or 404 or 429) && ex.Status != 0) UI.Hint($"HTTP {ex.Status}");
    }

    /// <summary>Keeps the context bounded by shrinking old tool outputs.</summary>
    private void TrimContext()
    {
        long total = _history.Sum(m => (long)m.Text.Length);
        if (total < 320_000) return;
        for (int i = 0; i < _history.Count - 8 && total > 240_000; i++)
        {
            var m = _history[i];
            if (m.Role != "tool" || m.Text.Length < 600) continue;
            total -= m.Text.Length;
            m.Text = "[old tool output removed to save context]";
            total += m.Text.Length;
        }
    }
}
