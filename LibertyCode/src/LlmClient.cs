using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LibertyCode;

/// <summary>Streaming chat client with tool calling. Speaks OpenAI-compatible and Anthropic protocols.</summary>
internal static class LlmClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LibertyCode/1.0"); // GitHub refuses requests without one
        return c;
    }

    public static async Task<LlmResult> StreamAsync(
        AppSettings s, string apiKey, string system, List<Msg> history,
        IReadOnlyList<ToolSpec>? tools, Action<string> onText, CancellationToken ct)
    {
        bool claude = s.Provider == "claude";
        string baseUrl = Providers.BaseUrl(s.Provider);
        bool gotText = false;
        bool noTemperature = false; // some models (o-series, gpt-5...) only accept the default temperature
        void Text(string t) { gotText = true; onText(t); Logger.StreamChunk(t); }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var req = claude
                    ? BuildClaude(baseUrl, s.Model, apiKey, system, history, tools)
                    : BuildOpenAi(baseUrl, s, apiKey, system, history, tools, !noTemperature);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await LogRequestAsync(req, attempt);

                HttpResponseMessage res;
                using (var hdr = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    hdr.CancelAfter(TimeSpan.FromSeconds(60));
                    try { res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, hdr.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { throw new LlmException(0, "The provider took too long to answer."); }
                    catch (HttpRequestException ex) { throw new LlmException(0, "Network error: " + ex.Message); }
                }

                using (res)
                {
                    Logger.Trace("RES", $"HTTP {(int)res.StatusCode} {res.ReasonPhrase} - headers after {sw.ElapsedMilliseconds} ms{InterestingHeaders(res)}");
                    if (!res.IsSuccessStatusCode)
                    {
                        string body = await res.Content.ReadAsStringAsync(ct);
                        Logger.Trace("RES", "Error body: " + Logger.Clip(body, 4000));
                        throw new LlmException((int)res.StatusCode, ExtractError(body));
                    }
                    await using var stream = await res.Content.ReadAsStreamAsync(ct);
                    LlmResult r;
                    try { r = claude ? await ParseClaude(stream, Text, ct) : await ParseOpenAi(stream, Text, ct); }
                    finally { Logger.StreamEnd(); }
                    Logger.Trace("RES", $"Stream finished in {sw.ElapsedMilliseconds} ms: {r.Text.Length} chars of text, {r.Calls.Count} tool call(s), tokens in={r.InTokens} out={r.OutTokens}");
                    return r;
                }
            }
            catch (LlmException ex) when (ex.Status == 400 && !noTemperature && !claude && s.Provider != "openai"
                                          && ex.Message.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            {
                noTemperature = true;
                Logger.Warn("This model refuses a custom temperature: retrying with the default one.");
            }
            catch (LlmException ex) when (attempt < 2 && !gotText && (ex.Status is 0 or 429 or 500 or 502 or 503 or 529) && !ct.IsCancellationRequested)
            {
                Logger.Warn($"Retrying after: {ex.Message} (attempt {attempt + 1}/3)");
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 2 : 5), ct);
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                throw new LlmException(0, "Connection lost: " + ex.Message);
            }
        }
    }

    private static async Task LogRequestAsync(HttpRequestMessage req, int attempt)
    {
        if (!Logger.DebugEnabled) return;
        var lines = req.Headers.Select(h => $"  {h.Key}: {Logger.MaskHeader(h.Key, string.Join(",", h.Value))}").ToList();
        string body = req.Content != null ? await req.Content.ReadAsStringAsync() : "";
        Logger.Trace("REQ", $"{req.Method} {req.RequestUri} (attempt {attempt + 1})\n{string.Join("\n", lines)}\n  body ({body.Length} chars): {Logger.Clip(body, 6000)}");
    }

    private static string InterestingHeaders(HttpResponseMessage res)
    {
        if (!Logger.DebugEnabled) return "";
        var keep = res.Headers.Where(h => h.Key.Contains("request-id", StringComparison.OrdinalIgnoreCase)
                                       || h.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase)
                                       || h.Key.Contains("ratelimit", StringComparison.OrdinalIgnoreCase))
                                 .Select(h => $"{h.Key}={string.Join(",", h.Value)}").ToList();
        return keep.Count == 0 ? "" : "\n  " + string.Join("\n  ", keep);
    }

    private static string ExtractError(string body)
    {
        try
        {
            var o = JObject.Parse(body);
            string? m = (string?)o["error"]?["message"] ?? (string?)o["message"] ?? (o["error"] as JValue)?.ToString();
            if (!string.IsNullOrWhiteSpace(m)) return m!;
        }
        catch { }
        body = body.Trim();
        return body.Length > 240 ? body[..240] + "…" : body;
    }

    // ── Request builders ──────────────────────────────────────────────────────
    private static HttpRequestMessage BuildOpenAi(string baseUrl, AppSettings s, string key, string system,
        List<Msg> history, IReadOnlyList<ToolSpec>? tools, bool useTemperature = true)
    {
        var msgs = new JArray { new JObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case "user":
                    msgs.Add(new JObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                case "assistant":
                {
                    var o = new JObject { ["role"] = "assistant" };
                    o["content"] = string.IsNullOrEmpty(m.Text) ? null : m.Text;
                    if (m.Calls.Count > 0)
                        o["tool_calls"] = new JArray(m.Calls.Select(c => new JObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JObject { ["name"] = c.Name, ["arguments"] = c.Args.ToString(Formatting.None) },
                        }));
                    msgs.Add(o);
                    break;
                }
                case "tool":
                    msgs.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.CallId,
                        ["content"] = string.IsNullOrEmpty(m.Text) ? "(no output)" : m.Text,
                    });
                    break;
            }
        }

        var body = new JObject { ["model"] = s.Model, ["stream"] = true, ["messages"] = msgs };
        if (s.Provider == "openai") body["max_completion_tokens"] = 8192; else body["max_tokens"] = 8192;
        // Low temperature = far fewer random tool calls from small open models (Qwen, Llama...). OpenAI is left alone: some of its models reject the field.
        if (s.Provider != "openai" && useTemperature) body["temperature"] = 0.2;
        if (s.Provider is "openai" or "groq" or "openrouter")
            body["stream_options"] = new JObject { ["include_usage"] = true };
        if (tools is { Count: > 0 })
            body["tools"] = new JArray(tools.Select(t => new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema },
            }));

        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        Providers.ApplyAuth(req, s.Provider, key);
        req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        return req;
    }

    private static HttpRequestMessage BuildClaude(string baseUrl, string model, string key, string system,
        List<Msg> history, IReadOnlyList<ToolSpec>? tools)
    {
        var msgs = new JArray();
        JObject? lastUser = null;

        void AddUser(JObject block)
        {
            if (lastUser == null)
            {
                lastUser = new JObject { ["role"] = "user", ["content"] = new JArray() };
                msgs.Add(lastUser);
            }
            ((JArray)lastUser["content"]!).Add(block);
        }

        foreach (var m in history)
        {
            switch (m.Role)
            {
                case "user":
                    AddUser(new JObject { ["type"] = "text", ["text"] = m.Text });
                    break;
                case "tool":
                    AddUser(new JObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = m.CallId,
                        ["content"] = string.IsNullOrEmpty(m.Text) ? "(no output)" : m.Text,
                        ["is_error"] = m.IsError,
                    });
                    break;
                case "assistant":
                {
                    lastUser = null;
                    var blocks = new JArray();
                    if (!string.IsNullOrWhiteSpace(m.Text)) blocks.Add(new JObject { ["type"] = "text", ["text"] = m.Text });
                    foreach (var c in m.Calls)
                        blocks.Add(new JObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = c.Args });
                    if (blocks.Count == 0) blocks.Add(new JObject { ["type"] = "text", ["text"] = "(empty)" });
                    msgs.Add(new JObject { ["role"] = "assistant", ["content"] = blocks });
                    break;
                }
            }
        }

        var body = new JObject
        {
            ["model"] = model, ["max_tokens"] = 8192, ["stream"] = true,
            ["system"] = system, ["messages"] = msgs,
        };
        if (tools is { Count: > 0 })
            body["tools"] = new JArray(tools.Select(t => new JObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Schema,
            }));

        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        return req;
    }

    // ── SSE ───────────────────────────────────────────────────────────────────
    private static async IAsyncEnumerable<(string Event, string Data)> Sse(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string ev = "";
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(120));
                try { line = await reader.ReadLineAsync(idle.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new LlmException(0, "The model stopped responding (timeout)."); }
            }
            if (line == null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (ev, data.ToString());
                ev = ""; data.Clear();
                continue;
            }
            if (line[0] == ':') continue;
            if (line.StartsWith("event:")) ev = line[6..].Trim();
            else if (line.StartsWith("data:"))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        if (data.Length > 0) yield return (ev, data.ToString());
    }

    private static JObject? TryParse(string data)
    {
        try { return JObject.Parse(data); } catch { return null; }
    }

    private static JObject ParseArgs(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JObject();
        try { return JObject.Parse(json); }
        catch { return new JObject { ["__error"] = "The tool arguments were not valid JSON." }; }
    }

    // ── OpenAI-compatible stream ──────────────────────────────────────────────
    private sealed class PartialCall { public string Id = ""; public string Name = ""; public StringBuilder Args = new(); }

    private static async Task<LlmResult> ParseOpenAi(Stream st, Action<string> onText, CancellationToken ct)
    {
        var r = new LlmResult();
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, PartialCall>();
        int next = 0, last = 0;

        await foreach (var (_, data) in Sse(st, ct))
        {
            if (data == "[DONE]") break;
            var o = TryParse(data);
            if (o == null) continue;
            if (o["error"] is JObject err) throw new LlmException(0, (string?)err["message"] ?? "Provider error");

            if (o["usage"] is JObject u)
            {
                r.InTokens = (int?)u["prompt_tokens"] ?? r.InTokens;
                r.OutTokens = (int?)u["completion_tokens"] ?? r.OutTokens;
            }

            if ((o["choices"] as JArray)?.FirstOrDefault() is not JObject ch || ch["delta"] is not JObject delta) continue;

            string? content = delta["content"]?.Type == JTokenType.String ? (string?)delta["content"] : null;
            if (!string.IsNullOrEmpty(content)) { text.Append(content); onText(content); }

            if (delta["tool_calls"] is JArray tcs)
                foreach (var tc in tcs.OfType<JObject>())
                {
                    string? id = (string?)tc["id"];
                    int idx = (int?)tc["index"] ?? (!string.IsNullOrEmpty(id) ? next : last);
                    if (!calls.TryGetValue(idx, out var pc)) { pc = new PartialCall(); calls[idx] = pc; }
                    next = Math.Max(next, idx + 1);
                    last = idx;
                    if (!string.IsNullOrEmpty(id)) pc.Id = id;
                    if (tc["function"] is JObject fn)
                    {
                        if (!string.IsNullOrEmpty((string?)fn["name"])) pc.Name = (string)fn["name"]!;
                        if (fn["arguments"]?.Type == JTokenType.String) pc.Args.Append((string?)fn["arguments"]);
                    }
                }
        }

        r.Text = text.ToString();
        foreach (var (idx, pc) in calls)
        {
            if (string.IsNullOrEmpty(pc.Name)) continue;
            r.Calls.Add(new ToolCall(string.IsNullOrEmpty(pc.Id) ? $"call_{Guid.NewGuid():N}"[..20] : pc.Id, pc.Name, ParseArgs(pc.Args.ToString())));
        }
        return r;
    }

    // ── Anthropic stream ──────────────────────────────────────────────────────
    private sealed class Block { public string Type = ""; public string Id = ""; public string Name = ""; public StringBuilder Json = new(); }

    private static async Task<LlmResult> ParseClaude(Stream st, Action<string> onText, CancellationToken ct)
    {
        var r = new LlmResult();
        var text = new StringBuilder();
        var blocks = new SortedDictionary<int, Block>();

        await foreach (var (ev, data) in Sse(st, ct))
        {
            var o = TryParse(data);
            if (o == null) continue;
            string type = (string?)o["type"] ?? ev;
            switch (type)
            {
                case "message_start":
                {
                    var u = o["message"]?["usage"];
                    r.InTokens = ((int?)u?["input_tokens"] ?? 0) + ((int?)u?["cache_read_input_tokens"] ?? 0) + ((int?)u?["cache_creation_input_tokens"] ?? 0);
                    break;
                }
                case "content_block_start":
                {
                    int idx = (int?)o["index"] ?? 0;
                    var cb = o["content_block"] as JObject;
                    blocks[idx] = new Block { Type = (string?)cb?["type"] ?? "", Id = (string?)cb?["id"] ?? "", Name = (string?)cb?["name"] ?? "" };
                    break;
                }
                case "content_block_delta":
                {
                    int idx = (int?)o["index"] ?? 0;
                    var d = o["delta"] as JObject;
                    string dt = (string?)d?["type"] ?? "";
                    if (dt == "text_delta")
                    {
                        string t = (string?)d?["text"] ?? "";
                        if (t.Length > 0) { text.Append(t); onText(t); }
                    }
                    else if (dt == "input_json_delta" && blocks.TryGetValue(idx, out var b))
                        b.Json.Append((string?)d?["partial_json"]);
                    break;
                }
                case "message_delta":
                    r.OutTokens = (int?)o["usage"]?["output_tokens"] ?? r.OutTokens;
                    break;
                case "error":
                    throw new LlmException(0, (string?)o["error"]?["message"] ?? "Provider error");
            }
        }

        r.Text = text.ToString();
        foreach (var b in blocks.Values.Where(b => b.Type == "tool_use"))
            r.Calls.Add(new ToolCall(b.Id, b.Name, ParseArgs(b.Json.ToString())));
        return r;
    }
}
