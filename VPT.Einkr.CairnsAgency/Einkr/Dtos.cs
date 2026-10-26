using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPT.Einkr.CairnsAgency.Einkr;

/// <summary>JSON settings shared by every call to the Einkr API: camelCase on the wire, case-insensitive on the way
/// in -- the convention the desktop app and DesktopEndpoints already speak.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>One tool call the model asked for, as the /turn Done event carries it.</summary>
public sealed class WireToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public bool ArgumentsTruncated { get; set; }
    public string? RawArguments { get; set; }
}

/// <summary>One entry of the running message array a logical turn sends with every /turn round -- the shape the
/// desktop app's AgentRunner keeps in `messages` (role, content, toolCalls on an assistant message, toolCallId on a
/// tool result).</summary>
public sealed class WireMessage
{
    public string Role { get; set; } = string.Empty;
    public string? Content { get; set; }
    public List<WireToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    public static WireMessage User(string content) => new() { Role = "user", Content = content };
}

/// <summary>The terminal Done event of one /turn round.</summary>
public sealed class TurnResponse
{
    public string FinishReason { get; set; } = "stop";
    public string? Text { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public List<WireToolCall>? ToolCalls { get; set; }
}

/// <summary>The body of POST .../turn. Property names are the DesktopTurnRequest contract; what AgentRunner sends
/// for an ordinary Chat turn, flag for flag.</summary>
public sealed class TurnRequest
{
    public string ModelName { get; set; } = string.Empty;
    public List<WireMessage> Messages { get; set; } = [];
    public List<JsonElement>? Tools { get; set; }
    public bool EnableThinking { get; set; }
    public bool PreserveThinking { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public bool EnableShellExecute { get; set; }
    public string? ForceToolName { get; set; }
    public int? PersonalityId { get; set; }
    public bool EnableWritableChat { get; set; }
    public bool EnablePlanning { get; set; }
    public bool EnableImplementation { get; set; }
    public int? ExpertId { get; set; }
    public bool EnableSqlTools { get; set; }
    public bool IsolatedContext { get; set; }
    public bool EnableVerification { get; set; }
    public bool EnableBuildAgent { get; set; }
    public bool EnableAskUser { get; set; }
    public bool EnablePlaywrightTools { get; set; }
    public List<int> McpPromptIds { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public string? SessionNotes { get; set; }
}

public sealed class TurnSegment
{
    public int Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class ToolCallRecord
{
    public string ToolName { get; set; } = string.Empty;
    public string? ArgumentsJson { get; set; }
    public string? Result { get; set; }
}

public sealed class ModelInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ContextWindowSize { get; set; }
    public int? CompactionTokenSize { get; set; }
}

public sealed class SessionSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime LastUsedUTC { get; set; }
    public string? SessionNotes { get; set; }
}

public sealed class TranscriptTurn
{
    public int ChatLogId { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
}

/// <summary>GET .../activity (added for the agency). When the server predates it, callers fall back to
/// SessionSummary.LastUsedUTC.</summary>
public sealed class SessionActivity
{
    public int LastChatLogId { get; set; }
    public DateTime? LastChatLogCreatedUtc { get; set; }
    public DateTime? LastChatLogModifiedUtc { get; set; }
    public bool LastTurnOpen { get; set; }
    public string? LastPromptStart { get; set; }
    public DateTime LastUserActivityUtc { get; set; }
}

public sealed class RemedyResponse
{
    public string? Tool { get; set; }
    public string? ArgumentsJson { get; set; }
    public string? Reason { get; set; }
}

public sealed class ValidateNotesResponse
{
    public List<string> Kept { get; set; } = [];
    public List<DroppedNote> Dropped { get; set; } = [];
}

public sealed class DroppedNote
{
    public string Text { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
