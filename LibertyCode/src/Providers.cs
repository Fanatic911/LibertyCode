using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace LibertyCode;

internal record Provider(string Label, string BaseUrl);

internal sealed class ModelsResult
{
    public List<string> Models { get; init; } = [];
    public int Status { get; init; }
    public string? Error { get; init; }
    public bool Verified { get; init; }

    public bool AuthRejected => Status is 401 or 403;
    public bool Ok => Error == null && Models.Count > 0;
}

internal static class Providers
{
    public static readonly Dictionary<string, Provider> All = new()
    {
        ["openai"]     = new("OpenAI",        "https://api.openai.com/v1"),
        ["claude"]     = new("Anthropic",     "https://api.anthropic.com/v1"),
        ["gemini"]     = new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai"),
        ["deepseek"]   = new("DeepSeek",      "https://api.deepseek.com/v1"),
        ["mistral"]    = new("Mistral",       "https://api.mistral.ai/v1"),
        ["ollama"]     = new("Ollama (Local)","http://localhost:11434/v1"),
        ["groq"]       = new("Groq",          "https://api.groq.com/openai/v1"),
        ["nvidia"]     = new("NVIDIA NIM",    "https://integrate.api.nvidia.com/v1"),
        ["github"]     = new("GitHub Models", "https://models.github.ai/inference"),
        ["openrouter"] = new("OpenRouter",    "https://openrouter.ai/api/v1"),
    };

    public static readonly Dictionary<string, List<string>> StaticModels = new()
    {
        ["openai"]     = ["gpt-4o", "gpt-4o-mini"],
        ["claude"]     = ["claude-opus-4-5", "claude-sonnet-4-5", "claude-haiku-4-5"],
        ["gemini"]     = ["gemini-2.0-flash", "gemini-1.5-pro", "gemini-1.5-flash"],
        ["deepseek"]   = ["deepseek-chat", "deepseek-reasoner"],
        ["mistral"]    = ["mistral-large-latest", "mistral-small-latest", "codestral-latest"],
        ["ollama"]     = ["llama3.2", "qwen2.5-coder", "mistral"],
        ["groq"]       = ["llama-3.3-70b-versatile", "llama-3.1-8b-instant"],
        ["github"]     = ["openai/gpt-4o", "openai/gpt-4o-mini"],
        ["openrouter"] = ["openai/gpt-4o", "anthropic/claude-sonnet-4-5"],
        ["nvidia"]     = ["meta/llama-3.3-70b-instruct"],
    };

    public static string BaseUrl(string provider)
    {
        string? o = Environment.GetEnvironmentVariable("LIBERTY_BASE_URL");
        return !string.IsNullOrWhiteSpace(o) ? o.TrimEnd('/') : All[provider].BaseUrl;
    }

    public static string ShortLabel(string provider) => Label(provider).Replace(" (Local)", "");
    public static string Label(string provider) => All.TryGetValue(provider, out var p) ? p.Label : provider;

    private static bool BaseOverridden => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIBERTY_BASE_URL"));

    private static string ListUrl(string provider) => provider switch
    {
        "github" when !BaseOverridden => "https://models.github.ai/catalog/models",
        "gemini" when !BaseOverridden => "https://generativelanguage.googleapis.com/v1beta/models",
        _ => BaseUrl(provider) + "/models",
    };

    private static readonly string[] NotChat =
    [
        "embed", "whisper", "tts", "dall-e", "moderation", "davinci", "babbage", "audio", "realtime", "transcribe",
        "image", "rerank", "guard", "sora", "flux", "stable-diffusion", "stabilityai", "parakeet", "riva", "reward", "nemoguard",
    ];

    // NVIDIA's catalog also lists embedding, vision-only, speech, biology and safety models: none of them chat.
    private static readonly string[] NvidiaNotChat =
    [
        "retriev", "clip", "vila", "neva", "paligemma", "kosmos", "fuyu", "deplot", "parse", "safety", "pii", "gliner",
        "cosmos", "genmol", "diffdock", "esm2", "molmim", "fourcastnet", "corrdiff", "canary", "maxine", "bge-", "e5-",
        "arctic-embed", "synthetic", "translate", "topic-control", "jailbreak", "studiovoice", "audio2face", "magpie",
        "usdcode", "streampetr", "bevformer", "sparsedrive", "nv-embed", "nv-rerank", "vlm", "ocr",
    ];

    // Models that practically always answer on the hosted API; used to check that a key really works.
    private static readonly string[] NvidiaProbeModels =
        ["meta/llama-3.3-70b-instruct", "meta/llama-3.1-8b-instruct", "nvidia/llama-3.1-nemotron-70b-instruct"];

    private static readonly Regex OpenAiChat = new(@"^(gpt-|chatgpt-|o\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] OpenAiNo = ["instruct", "codex", "search", "deep-research", "computer-use", "-pro", "-realtime", "-audio"];

    private static bool IsChatModel(string provider, string id, JObject m)
    {
        if (NotChat.Any(n => id.Contains(n, StringComparison.OrdinalIgnoreCase))) return false;

        if (m["active"] is JValue { Type: JTokenType.Boolean } active && !(bool)active) return false;
        if (m["supported_output_modalities"] is JArray o1 && !o1.Any(x => (string?)x == "text")) return false;
        if (m["architecture"]?["output_modalities"] is JArray o2 && !o2.Any(x => (string?)x == "text")) return false;

        if (provider == "nvidia" && NvidiaNotChat.Any(n => id.Contains(n, StringComparison.OrdinalIgnoreCase))) return false;
        if (provider == "openai")
            return OpenAiChat.IsMatch(id) && !OpenAiNo.Any(n => id.Contains(n, StringComparison.OrdinalIgnoreCase));
        if (provider == "gemini")
            return id.Contains("gemini", StringComparison.OrdinalIgnoreCase)
                && !id.Contains("embedding", StringComparison.OrdinalIgnoreCase)
                && !id.Contains("aqa", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LibertyCode/1.0");
        return c;
    }

    public static async Task<ModelsResult> GetModelsAsync(string provider, string apiKey, int timeoutSec = 20, bool probeKey = false)
    {
        // Ollama: local server, no auth needed — just list what's pulled
        if (provider == "ollama")
            return await GetOllamaModelsAsync(timeoutSec);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        Logger.Trace("MODEL", $"Listing models: provider={provider}, key={Logger.Mask(apiKey)}, timeout={timeoutSec}s");

        var raw = new List<string>();
        string? after = null;
        int status = 0;

        for (int page = 0; page < 20; page++)
        {
            string url = ListUrl(provider);
            if (provider == "claude")
                url += "?limit=1000" + (after != null ? "&after_id=" + Uri.EscapeDataString(after) : "");
            if (provider == "gemini")
                url += "?key=" + Uri.EscapeDataString(apiKey);

            var (st, body, netError) = await GetAsync(url, provider, apiKey, cts.Token);
            status = st;

            if (netError != null) return Fail(0, netError);
            if (st is 404 or 405 && page == 0) return Fallback(provider, st, body);
            if (st < 200 || st >= 300) return Fail(st, DescribeFailure(provider, st, body));

            JToken root;
            try { root = JToken.Parse(body); }
            catch { return Fail(st, "The provider answered, but not with a model list."); }

            // Gemini uses {"models": [...]} with "name" field
            JArray? arr = provider == "gemini"
                ? (root as JObject)?["models"] as JArray
                : root as JArray ?? ((root as JObject)?["data"] ?? (root as JObject)?["models"]) as JArray;

            if (arr == null) return Fail(st, "The provider answered, but not with a model list.");

            foreach (var item in arr.OfType<JObject>())
            {
                string id = provider == "gemini"
                    ? ((string?)item["name"] ?? "").Replace("models/", "")
                    : (string?)item["id"] ?? (string?)item["name"] ?? "";
                if (id.Length > 0 && IsChatModel(provider, id, item)) raw.Add(id);
            }

            if (provider == "claude" && (bool?)(root as JObject)?["has_more"] == true && (string?)(root as JObject)?["last_id"] is { Length: > 0 } last)
            { after = last; continue; }
            break;
        }

        var models = raw.Distinct().ToList();
        if (provider != "claude") models = models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        Logger.Trace("MODEL", $"{models.Count} chat model(s) available for {provider}");
        if (models.Count == 0) return Fail(status, "The provider returned no usable chat model for this key.");

        if (probeKey && !string.IsNullOrWhiteSpace(apiKey))
        {
            var bad = await ProbeKeyAsync(provider, apiKey, models[0], cts.Token);
            if (bad != null) return bad;
        }
        return new ModelsResult { Models = models, Status = status, Verified = true };
    }

    private static async Task<ModelsResult> GetOllamaModelsAsync(int timeoutSec)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        string url = BaseUrl("ollama").Replace("/v1", "") + "/api/tags";
        Logger.Trace("MODEL", $"Ollama: listing from {url}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await Http.SendAsync(req, cts.Token);
            string body = await res.Content.ReadAsStringAsync(cts.Token);
            if (!res.IsSuccessStatusCode)
                return Fallback("ollama", (int)res.StatusCode, body);

            var root = JObject.Parse(body);
            var arr = root["models"] as JArray;
            if (arr == null || arr.Count == 0)
                return Fallback("ollama", 200, "");

            var models = arr.OfType<JObject>()
                .Select(m => (string?)m["name"] ?? "")
                .Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ModelsResult { Models = models, Status = 200, Verified = true };
        }
        catch (Exception ex)
        {
            Logger.Trace("ERROR", "Ollama unreachable: " + ex.Message);
            return Fallback("ollama", 0, ex.Message);
        }
    }

    private static ModelsResult Fail(int status, string error)
    {
        Logger.Trace("MODEL", $"Listing failed (HTTP {status}): {error}");
        return new ModelsResult { Status = status, Error = error };
    }

    private static ModelsResult Fallback(string provider, int status, string body)
    {
        if (StaticModels.TryGetValue(provider, out var list))
        {
            Logger.Warn($"{Label(provider)} has no model-listing endpoint (HTTP {status}): using the built-in list, not verified.");
            return new ModelsResult { Models = list.ToList(), Status = status, Verified = false };
        }
        return Fail(status, DescribeFailure(provider, status, body));
    }

    private static string DescribeFailure(string provider, int status, string body) => status switch
    {
        401 or 403 => $"{Label(provider)} rejected the API key (HTTP {status}).",
        429        => $"{Label(provider)} rate limit reached (HTTP 429). Try again in a moment.",
        _          => $"{Label(provider)} answered HTTP {status}: {ErrorText(body)}",
    };

    private static string ErrorText(string body)
    {
        try
        {
            var o = JObject.Parse(body);
            string? m = (string?)o["error"]?["message"] ?? (string?)o["message"] ?? (o["error"] as JValue)?.ToString();
            if (!string.IsNullOrWhiteSpace(m)) return m!;
        }
        catch { }
        return Logger.Clip(body.Trim(), 240);
    }

    private static async Task<(int Status, string Body, string? NetError)> GetAsync(string url, string provider, string apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(req, provider, apiKey);
            LogRequest(req);

            using var res = await Http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            Logger.Trace("RES", $"HTTP {(int)res.StatusCode} {res.ReasonPhrase} in {sw.ElapsedMilliseconds} ms ({body.Length} chars)\n{Logger.Clip(body, 700)}");
            return ((int)res.StatusCode, body, null);
        }
        catch (OperationCanceledException)
        {
            Logger.Trace("ERROR", $"GET {url}: timeout after {sw.ElapsedMilliseconds} ms");
            return (0, "", "The provider did not answer in time (network or timeout).");
        }
        catch (HttpRequestException ex)
        {
            Logger.Trace("ERROR", $"GET {url}: {ex.Message}");
            return (0, "", "Network error: " + ex.Message);
        }
    }

    internal static void ApplyAuth(HttpRequestMessage req, string provider, string apiKey)
    {
        apiKey = CryptoHelper.CleanKey(apiKey);
        switch (provider)
        {
            case "claude":
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case "gemini":
                // Key is passed as query param for listing; for chat we use Bearer
                if (!req.RequestUri!.Query.Contains("key="))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                break;
            case "ollama":
                break; // no auth
            default:
                if (!string.IsNullOrWhiteSpace(apiKey))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                break;
        }

        if (provider == "github")
        {
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    private static void LogRequest(HttpRequestMessage req)
    {
        if (!Logger.DebugEnabled) return;
        var lines = req.Headers.Select(h => $"  {h.Key}: {Logger.MaskHeader(h.Key, string.Join(",", h.Value))}");
        Logger.Trace("REQ", $"{req.Method} {req.RequestUri}\n{string.Join("\n", lines)}");
    }

    /// <summary>Sends a 1-token chat request. Returns the HTTP status, or 0 when it timed out or the network failed.</summary>
    public static async Task<int> TestModelAsync(string provider, string apiKey, string model, CancellationToken ct = default)
        => (await ProbeAsync(provider, apiKey, model, ct)).Status;

    private static async Task<(int Status, TimeSpan? RetryAfter)> ProbeAsync(string provider, string apiKey, string model, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // no answer in 10s = timeout (status 0)
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl(provider) + "/chat/completions")
            {
                Content = new StringContent(
                    new JObject { ["model"] = model, ["max_tokens"] = 16, ["stream"] = false,
                                  ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "hi" }) }
                        .ToString(Newtonsoft.Json.Formatting.None),
                    System.Text.Encoding.UTF8, "application/json"),
            };
            ApplyAuth(req, provider, apiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            LogRequest(req);
            using var res = await Http.SendAsync(req, cts.Token);
            string body = await res.Content.ReadAsStringAsync(cts.Token);
            Logger.Trace("RES", $"Model test {model}: HTTP {(int)res.StatusCode}\n{Logger.Clip(body, 400)}");
            return ((int)res.StatusCode, res.Headers.RetryAfter?.Delta);
        }
        catch (Exception ex)
        {
            Logger.Trace("ERROR", $"Model test {model}: {ex.Message}");
            return (0, null);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Working = new();
    private static string CacheKey(string apiKey, string model) => CryptoHelper.CleanKey(apiKey) + "|" + model;
    public static void MarkWorking(string apiKey, string model) => Working[CacheKey(apiKey, model)] = true;
    public static bool IsKnownWorking(string apiKey, string model) => Working.TryGetValue(CacheKey(apiKey, model), out var ok) && ok;
    public static bool IsKnownBroken(string apiKey, string model) => Working.TryGetValue(CacheKey(apiKey, model), out var ok) && !ok;
    public static void MarkBroken(string apiKey, string model) => Working[CacheKey(apiKey, model)] = false;

    /// <summary>The model answered (or is merely throttled / picky about parameters): it exists and the key can use it.</summary>
    private static bool IsAnswering(int status) => status is >= 200 and < 300 or 400 or 422 or 429;

    /// <summary>
    /// Background check of every model, one request at a time with a pause between them, so the provider never rate-limits us.
    /// Results land in the cache as they come. A throttled (429) or failing (5xx) model is retried after a pause, never marked broken.
    /// </summary>
    public static async Task ScanSlowlyAsync(string apiKey, List<string> models, CancellationToken ct)
    {
        int gapMs = 600, timeouts = 0, done = 0;
        try
        {
            await Task.Delay(3000, ct); // let the startup finish first
            foreach (var m in models)
            {
                if (Working.ContainsKey(CacheKey(apiKey, m))) continue;
                int st = 0;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    TimeSpan? wait;
                    (st, wait) = await ProbeAsync("nvidia", apiKey, m, ct);
                    if (st != 429 && st < 500) break;
                    gapMs = Math.Min(gapMs * 2, 5000);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait?.TotalSeconds ?? 5 * (attempt + 1), 1, 30)), ct);
                }
                if (st == 429 || st >= 500) continue;           // still throttled: unknown, tested when picked
                timeouts = st == 0 ? timeouts + 1 : 0;
                if (timeouts >= 3) break;                       // offline or provider down: stop instead of timing out 50 times
                Working[CacheKey(apiKey, m)] = IsAnswering(st);
                done++;
                gapMs = Math.Max(600, gapMs - 200);
                await Task.Delay(gapMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        Logger.Trace("MODEL", $"Background scan: {done} model(s) checked, {models.Count(x => IsKnownWorking(apiKey, x))}/{models.Count} answer");
    }

    /// <summary>true when the model is certainly unusable with this key (auth, not found, gone).</summary>
    public static bool IsUnusable(int status) => status is 401 or 403 or 404 or 410;

    private static async Task<ModelsResult?> ProbeKeyAsync(string provider, string apiKey, string sampleModel, CancellationToken ct)
    {
        if (provider is not ("nvidia" or "openrouter")) return null;
        if (provider == "nvidia")
        {
            // /models is public on NVIDIA: it proves nothing about the key. Ask a model that is normally available.
            int denied = 0;
            foreach (var m in NvidiaProbeModels)
            {
                int st = await TestModelAsync(provider, apiKey, m, ct);
                if (st == 401) return Fail(401, DescribeFailure(provider, 401, ""));
                if (st == 403) { denied++; continue; }
                if (st is >= 200 and < 300 or 400 or 429) return null;   // key accepted
            }
            // 403 "Authorization failed" on every common model = the key's organization lacks the "Public API Endpoints" permission.
            if (denied == NvidiaProbeModels.Length)
                return Fail(403, "NVIDIA refuses this key for every model, it has probably expired. Create a new one on build.nvidia.com.");
            return null; // inconclusive (probe models unavailable or network): do not block
        }
        var sw = Stopwatch.StartNew();
        try
        {
            using HttpRequestMessage req = provider == "openrouter"
                ? new(HttpMethod.Get, BaseUrl(provider) + "/auth/key")
                : new(HttpMethod.Post, BaseUrl(provider) + "/chat/completions")
                {
                    Content = new StringContent(
                        new JObject { ["model"] = sampleModel, ["max_tokens"] = 1, ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "hi" }) }.ToString(Newtonsoft.Json.Formatting.None),
                        System.Text.Encoding.UTF8, "application/json"),
                };
            ApplyAuth(req, provider, apiKey);
            LogRequest(req);
            using var res = await Http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            Logger.Trace("RES", $"Key check: HTTP {(int)res.StatusCode} in {sw.ElapsedMilliseconds} ms\n{Logger.Clip(body, 500)}");
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return Fail(401, DescribeFailure(provider, 401, body));
        }
        catch (Exception ex)
        {
            Logger.Trace("ERROR", "Key check inconclusive: " + ex.Message);
        }
        return null;
    }
}
