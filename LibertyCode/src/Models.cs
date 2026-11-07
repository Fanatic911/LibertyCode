using Newtonsoft.Json.Linq;

namespace LibertyCode;

internal sealed record ToolCall(string Id, string Name, JObject Args);

internal sealed record ToolSpec(string Name, string Description, JObject Schema);

/// <summary>Provider-neutral chat message. Role: user | assistant | tool.</summary>
internal sealed class Msg
{
    public string Role { get; init; } = "user";
    public string Text { get; set; } = "";
    public List<ToolCall> Calls { get; init; } = [];
    public string? CallId { get; init; }
    public bool IsError { get; init; }
}

internal sealed class LlmResult
{
    public string Text = "";
    public List<ToolCall> Calls = [];
    public int InTokens;
    public int OutTokens;
}

internal sealed class LlmException : Exception
{
    public int Status { get; }
    public LlmException(int status, string message) : base(message) => Status = status;
}

internal sealed class ToolError : Exception
{
    /// <summary>Extra context sent to the model only (never printed in the terminal).</summary>
    public string? ModelHint { get; }
    public ToolError(string message, string? modelHint = null) : base(message) { ModelHint = modelHint; }
}
