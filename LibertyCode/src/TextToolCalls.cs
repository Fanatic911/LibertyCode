using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace LibertyCode;

/// <summary>
/// Small models sometimes print their tool calls as plain text (list_dir{}&lt;tool_call|&gt;, &lt;tool_call&gt;{json}&lt;/tool_call&gt;...)
/// instead of using the API. This finds them, hides them from the screen and turns them into real calls.
/// </summary>
internal static class TextToolCalls
{
    private const string Q = "<|\"|>";
    private static readonly string[] Markers = ["<|tool_call>", "<tool_call>", "<tool_call|>", "</tool_call>", "<function=", "call:", Q];
    private static readonly string Names = string.Join("|", Tools.Specs.Select(t => Regex.Escape(t.Name)));

    private static readonly Regex JsonRx = new(@"<\|?tool_call\|?>\s*(\{.*?\})\s*(?:</tool_call>|<tool_call\|>)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex XmlRx = new(@"<function=(\w+)>(.*?)</function>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ParamRx = new(@"<parameter=(\w+)>\s*(.*?)\s*</parameter>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StartRx = new($@"(?:<\|?tool_call\|?>\s*)?(?:call:\s*)?(?<![\w.])({Names})\s*\{{", RegexOptions.Compiled);
    private static readonly Regex TrailRx = new(@"\G\s*(?:</?\|?tool_call\|?>)?", RegexOptions.Compiled);
    private static readonly Regex StrayRx = new(@"</?\|?tool_call\|?>|<\|""\|>", RegexOptions.Compiled);

    private static bool Known(string? name) => name != null && Tools.Specs.Any(t => t.Name == name);
    private static ToolCall Make(string name, JObject args) => new($"call_{Guid.NewGuid():N}"[..20], name, args);

    /// <summary>Returns the text without the tool calls, and the calls found in it.</summary>
    public static (string Clean, List<ToolCall> Calls) Extract(string text)
    {
        var found = new List<(int S, int E, ToolCall Call)>();
        bool Free(int s, int e) => !found.Any(f => s < f.E && e > f.S);

        foreach (Match m in JsonRx.Matches(text))
        {
            try
            {
                var o = JObject.Parse(m.Groups[1].Value);
                string? name = o["name"]?.Type == JTokenType.String ? (string?)o["name"] : null;
                JToken? a = o["arguments"] ?? o["parameters"];
                JObject args = a is JObject ao ? ao : a?.Type == JTokenType.String ? JObject.Parse((string)a!) : new JObject();
                if (Known(name)) found.Add((m.Index, m.Index + m.Length, Make(name!, args)));
            }
            catch { }
        }

        foreach (Match m in XmlRx.Matches(text))
        {
            if (!Known(m.Groups[1].Value) || !Free(m.Index, m.Index + m.Length)) continue;
            var args = new JObject();
            foreach (Match p in ParamRx.Matches(m.Groups[2].Value)) args[p.Groups[1].Value] = p.Groups[2].Value;
            found.Add((m.Index, m.Index + m.Length, Make(m.Groups[1].Value, args)));
        }

        foreach (Match m in StartRx.Matches(text))
        {
            int end = ParseBraces(text, m.Index + m.Length, out var args);
            if (end < 0) continue;
            end += TrailRx.Match(text, end).Length;
            if (Free(m.Index, end)) found.Add((m.Index, end, Make(m.Groups[1].Value, args)));
        }

        found.Sort((x, y) => x.S.CompareTo(y.S));
        var sb = new StringBuilder();
        int pos = 0;
        foreach (var f in found) { sb.Append(text, pos, f.S - pos); pos = f.E; }
        sb.Append(text, pos, text.Length - pos);
        return (StrayRx.Replace(sb.ToString(), "").TrimEnd(), found.Select(f => f.Call).ToList());
    }

    /// <summary>Parses  key:value,key:value}  (values as &lt;|"|&gt;text&lt;|"|&gt;, "text", number or bare word). Returns the index after '}', or -1.</summary>
    private static int ParseBraces(string t, int i, out JObject args)
    {
        args = new JObject();
        while (true)
        {
            while (i < t.Length && (char.IsWhiteSpace(t[i]) || t[i] == ',')) i++;
            if (i >= t.Length) return -1;
            if (t[i] == '}') return i + 1;

            int colon = t.IndexOf(':', i);
            if (colon < 0) return -1;
            string key = t[i..colon].Trim().Trim('"', '\'');
            if (!Regex.IsMatch(key, @"^\w+$")) return -1;
            i = colon + 1;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i >= t.Length) return -1;

            if (string.CompareOrdinal(t, i, Q, 0, Q.Length) == 0)
            {
                int e = t.IndexOf(Q, i + Q.Length, StringComparison.Ordinal);
                if (e < 0) return -1;
                args[key] = t.Substring(i + Q.Length, e - i - Q.Length);
                i = e + Q.Length;
            }
            else if (t[i] == '"')
            {
                int e = i + 1;
                while (e < t.Length && t[e] != '"') e += t[e] == '\\' ? 2 : 1;
                if (e >= t.Length) return -1;
                try { args[key] = JToken.Parse(t.Substring(i, e - i + 1)); } catch { return -1; }
                i = e + 1;
            }
            else
            {
                int e = i;
                while (e < t.Length && t[e] != ',' && t[e] != '}') e++;
                if (e >= t.Length) return -1;
                string raw = t[i..e].Trim();
                if (bool.TryParse(raw, out var b)) args[key] = b;
                else if (long.TryParse(raw, out var n)) args[key] = n;
                else args[key] = raw;
                i = e;
            }
        }
    }

    /// <summary>Index where a tool call starts in the text, or -1.</summary>
    public static int FindStart(string text)
    {
        int best = StartRx.Match(text) is { Success: true } m ? m.Index : -1;
        foreach (string mk in Markers)
        {
            if (mk == "call:" || mk == "</tool_call>") continue;
            int i = text.IndexOf(mk, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    /// <summary>How many trailing characters could still turn into a tool call and must be held back while streaming.</summary>
    public static int HeldTail(string text)
    {
        for (int i = Math.Max(0, text.Length - 24); i < text.Length; i++)
        {
            string tail = text[i..];
            if (Markers.Any(mk => mk.StartsWith(tail, StringComparison.Ordinal))) return text.Length - i;
            string word = tail.TrimEnd();
            bool atWordStart = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
            if (atWordStart && word.Length > 0 && Tools.Specs.Any(s => s.Name.StartsWith(word, StringComparison.Ordinal))) return text.Length - i;
        }
        return 0;
    }
}

/// <summary>Sits between the model stream and the screen: lets normal text through, holds back anything that looks like a tool call.</summary>
internal sealed class LeakFilter
{
    private readonly StringBuilder _all = new();
    private int _shown, _cut = -1;

    public string Push(string chunk)
    {
        _all.Append(chunk);
        string t = _all.ToString();
        if (_cut < 0)
        {
            int from = Math.Max(0, _shown - 60); // only the new part needs scanning
            int c = TextToolCalls.FindStart(t[from..]);
            if (c >= 0) _cut = from + c;
        }
        int limit = _cut >= 0 ? _cut : t.Length - TextToolCalls.HeldTail(t);
        if (limit <= _shown) return "";
        string s = t[_shown..limit];
        _shown = limit;
        return s;
    }

    /// <summary>What is still to be shown once the stream ended, given the text cleaned of tool calls.</summary>
    public string Rest(string clean) => _shown < clean.Length ? clean[_shown..] : "";
}
