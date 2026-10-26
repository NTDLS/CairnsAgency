using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency.Einkr;

namespace VPT.Einkr.CairnsAgency.Runner;

/// <summary>Runs one tool the model asked for. Returns the result text the model sees; never throws for a tool-level
/// failure (those are "Error: ..." strings, same as every tool in the desktop app).</summary>
public interface IToolExecutor
{
    Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct);
}

/// <summary>Per-turn configuration, mirroring what the desktop app's AgentRunner is constructed with for an ordinary Chat
/// turn (see app.js send(), the plain `toolBinding` branch).</summary>
public sealed class RunnerOptions
{
    public string ModelName { get; set; } = string.Empty;
    public int PersonalityId { get; set; }
    public bool EnableThinking { get; set; }
    public bool PreserveThinking { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public bool EnableShellExecute { get; set; }
    public bool EnableWritableChat { get; set; }

    /// <summary>NotesStore.Snapshot() taken ONCE at the start of the turn (see the NotesStore comment).</summary>
    public string? SessionNotes { get; set; }

    public List<JsonElement> ToolDefinitions { get; set; } = [];

    /// <summary>The model's real window (GET /api/models); 0 = unknown. Scales pruning and the oversized-result limit.</summary>
    public int ContextWindowSize { get; set; }

    public bool EnablePruning { get; set; } = true;
    public double PruneFraction { get; set; } = 0.5;
    public int MaxIterations { get; set; } = 250;
}

/// <summary>Everything the runner reports outward. All optional.</summary>
public sealed class RunnerHooks
{
    public Action<string>? OnToken { get; set; }
    public Action<string>? OnThinking { get; set; }
    public Action<string>? OnStatus { get; set; }

    /// <summary>name, argumentsJson, result -- fired for every tool call, in order (the recorder's source).</summary>
    public Action<string, string, string>? OnToolCall { get; set; }
    public Action<int>? OnPromptTokens { get; set; }

    /// <summary>Called at the top of every round. A non-null reason makes the runner stop starting new work and ask the
    /// model for a short wrap-up instead -- how a wake ends gracefully when Josh arrives or the budget is spent.</summary>
    public Func<string?>? WindDownReason { get; set; }

    /// <summary>Saves an oversized tool result and returns (id, filePath), or null when it cannot.</summary>
    public Func<string, Task<(string Id, string FilePath)?>>? SpillToolOutput { get; set; }
}

public sealed record RunnerResult(string FinalText, int Rounds, int ToolCalls, string EndReason, int LastPromptTokens);

/// <summary>C# port of the desktop app's AgentRunner.runTurn (renderer/agent-runner.js), for the plain Chat path: repeatedly
/// calls /turn, executes whatever tool calls come back locally, feeds the results back as role="tool" messages, and loops
/// until the model answers in text. Everything that keeps that loop honest is carried over -- the adjacent-loop window, the
/// result-aware repeat guard, the no-progress ceiling, the behaviour-only "you are repeating yourself" intervention, the
/// automatic remedy lookup for a tool that keeps failing, oversized-result spilling, old-tool-result pruning, truncated-
/// argument handling, transport retry and the no-tools salvage call -- so an unattended wake gets the same safety net a
/// watched turn does. What it deliberately leaves out is what a Chat turn does not use either: steering, compaction (the
/// server compacts between turns), the Build Agent's gates, checkpoints and terminal tools.</summary>
public sealed class AgentRunner(EinkrApiClient api, IToolExecutor tools, ILogger logger)
{
    // ---- constants lifted from agent-runner.js ---------------------------------------------------------
    private const int LoopWindowSize = 4;
    private const int RepeatResultLimit = 5;
    private const int MaxTrackedSignatures = 1000;
    private const int NoProgressLimit = 100;
    private const int MaxLoopRefusals = 5;
    private const int MaxLoopInterventions = 2;
    private const int MaxTransportRetries = 4;

    private const int RemedyAfterFailures = 2;
    private const int RemedyMaxPerTurn = 6;
    private const int RemedyResultMaxChars = 4000;
    private const int RemedyListingMaxChars = 3000;
    private const int RemedyRecentCalls = 4;

    private const int PruneProtectTokens = 40_000;
    private const int PruneMinimumTokens = 20_000;
    private const string PrunedPlaceholder = "[Older tool result cleared to free context. Call the tool again if you still need it.]";

    private const double ResultLimitFraction = 0.12;
    private const int ResultLimitMinChars = 40_000;
    private const int ResultLimitMaxChars = 140_000;
    private const int ResultLimitDefaultChars = 100_000;
    private const int SpillPreviewChars = 6_000;

    private static readonly HashSet<string> ProgressTools = ["write_file", "edit_file", "delete_path", "rename_path", "todowrite"];
    private static readonly HashSet<string> DiscoveryProgressTools =
        ["read_file", "get_file", "read_section", "outline_file", "list_dir", "search_code", "find_files", "run_command"];
    private static readonly HashSet<string> RemedyTools = ["list_dir", "find_files", "search_code", "read_file", "outline_file"];
    private static readonly HashSet<string> SpillToolNames = ["read_tool_output", "search_tool_output"];

    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static int EstimateTokens(string? text) => (int)Math.Ceiling((text?.Length ?? 0) / 3.5);

    internal static string TruncateMiddle(string text, int limitChars)
    {
        var head = (int)Math.Floor(limitChars * 0.7);
        var tail = limitChars - head;
        return $"{text[..head]}\n\n[... {(text.Length - limitChars):N0} characters omitted from the " +
               $"middle of this {text.Length:N0}-character result ...]\n\n{text[(text.Length - tail)..]}";
    }

    internal static string PreviewHead(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf('\n', maxChars);
        return text[..(cut > maxChars * 0.5 ? cut : maxChars)];
    }

    /// <summary>MCP-style results are often minified JSON, which the line-oriented read tools cannot window; re-indent.</summary>
    private static string PrettyForSpill(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try { using var doc = JsonDocument.Parse(text); return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
            catch (JsonException) { /* not JSON after all */ }
        }
        return text;
    }

    /// <summary>Blanks tool results older than the most recent <paramref name="protectTokens"/> worth, only when that frees at
    /// least PruneMinimumTokens. Only tool RESULTS are touched, and never the round about to be answered.</summary>
    internal static int PruneOldToolResults(List<WireMessage> messages, int protectTokens)
    {
        var seen = 0;
        var reclaimable = 0;
        var candidates = new List<WireMessage>();

        var lastAssistant = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == "assistant") { lastAssistant = i; break; }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m.Role != "tool" || m.Content == PrunedPlaceholder) continue;
            var size = EstimateTokens(m.Content);
            seen += size;
            if (i > lastAssistant) continue;
            if (seen <= protectTokens) continue;
            reclaimable += size;
            candidates.Add(m);
        }

        if (reclaimable < PruneMinimumTokens) return 0;
        foreach (var m in candidates) m.Content = PrunedPlaceholder;
        return reclaimable;
    }

    internal static string FormatArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return string.Empty;
        return string.Join(", ", args.EnumerateObject().Select(p =>
        {
            // JSON.stringify(value): compact, non-ASCII left as is.
            var rendered = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : JsonSerializer.Serialize(p.Value, CompactJson);
            if (rendered.Length > 60) rendered = rendered[..60] + "…";
            return $"{p.Name}={rendered}";
        }));
    }

    private static bool IsFailedResult(string name, string result)
    {
        if (result.StartsWith("Error", StringComparison.Ordinal)) return true;
        if (name is "read_file" or "get_file")
        {
            var sections = Regex.Matches(result, @"^===== .* =====$", RegexOptions.Multiline).Count;
            var errors = Regex.Matches(result, @"^Error:", RegexOptions.Multiline).Count;
            return sections > 0 && errors >= sections;
        }
        return false;
    }

    private static string ToolName(JsonElement def) =>
        (def.TryGetProperty("function", out var f) ? f : def).GetProperty("name").GetString() ?? "";

    // ------------------------------------------------------------------------------------------------------

    public async Task<RunnerResult> RunTurnAsync(Guid sessionId, List<WireMessage> messages, RunnerOptions o, RunnerHooks hooks, CancellationToken ct)
    {
        string? finalText = null;
        var endReason = "answered";
        var rounds = 0;
        var toolCallCount = 0;
        var lastPromptTokens = 0;

        // Loop guard: rolling window of the last few call signatures (name + raw arguments).
        var recent = new List<string>();
        string? CheckForToolCallLoop(string signature)
        {
            recent.Add(signature);
            if (recent.Count > LoopWindowSize) recent.RemoveAt(0);
            if (recent.Count < LoopWindowSize) return null;
            if (recent.All(s => s == recent[0]))
                return $"Error: you've called this tool with identical arguments {LoopWindowSize} times in a row. " +
                       "This tool isn't going to give you a different result. If you're stuck because you're missing a " +
                       "capability, say so in your next response instead of repeating this call.";
            if (recent[0] == recent[2] && recent[1] == recent[3] && recent[0] != recent[1])
                return "Error: you've repeated the same two-call sequence twice in a row. This isn't converging on " +
                       "anything new -- if you're stuck, say so in your next response instead of repeating this pattern.";
            return null;
        }

        // Result-aware repeat guard (never a permanent block: a different result resets it).
        var repeated = new Dictionary<string, (string Digest, int Count)>();
        static string Digest(string r) => $"{r.Length}:{(r.Length > 96 ? r[..96] : r)}";
        string? CheckForRepeatedCall(string signature)
        {
            if (!repeated.TryGetValue(signature, out var seen) || seen.Count < RepeatResultLimit) return null;
            return $"Error: you have run this exact call {seen.Count} times and gotten the same result every " +
                   "time -- it will not change. Use the answer you already have. If you need something different, " +
                   "change the call or do something else; repeating this one will keep being refused.";
        }
        void RecordCallResult(string signature, string result)
        {
            var digest = Digest(result);
            if (repeated.TryGetValue(signature, out var seen) && seen.Digest == digest) { repeated[signature] = (digest, seen.Count + 1); return; }
            if (repeated.Count >= MaxTrackedSignatures) repeated.Clear();
            repeated[signature] = (digest, 1);
        }

        var roundsSinceProgress = 0;
        var loopRefusals = 0;
        var loopInterventions = 0;
        var abandon = false;
        string? abandonReason = null;

        // Automatic remedy lookup: new FACTS (not advice) for a tool that keeps failing.
        var toolFailureStreak = new Dictionary<string, int>();
        var remediedSignatures = new HashSet<string>();
        var recentCallLog = new List<object>();
        var remediesThisTurn = 0;

        var resultLimitChars = o.ContextWindowSize > 0
            ? Math.Min(ResultLimitMaxChars, Math.Max(ResultLimitMinChars, (int)Math.Floor(o.ContextWindowSize * ResultLimitFraction * 3.5)))
            : ResultLimitDefaultChars;
        var canSpill = hooks.SpillToolOutput != null && o.ToolDefinitions.Any(d => ToolName(d) == "read_tool_output");

        async Task<string> ContainLargeResultAsync(string result)
        {
            if (canSpill)
            {
                try
                {
                    var body = PrettyForSpill(result);
                    var saved = await hooks.SpillToolOutput!(body);
                    if (saved is { } s)
                    {
                        var lineCount = body.Split('\n').Length;
                        return $"[This tool result is {result.Length:N0} characters (about " +
                               $"{(int)Math.Ceiling(result.Length / 3.5):N0} tokens), too large to keep in the " +
                               $"conversation, so it was saved as tool output \"{s.Id}\" ({s.FilePath}), {lineCount:N0} lines.\n" +
                               "Read it with read_tool_output (id, offset, limit -- the footer gives the next offset) or find " +
                               "something in it with search_tool_output (id, pattern). Do not call the original tool again just " +
                               $"to see it; you already have it.\n\nFirst part of it:\n{PreviewHead(body, SpillPreviewChars)}\n" +
                               $"[... preview ends -- {s.Id} has the rest ...]]";
                    }
                }
                catch { /* a failed save must never put the whole result back into the conversation */ }
            }
            return TruncateMiddle(result, resultLimitChars);
        }

        TurnRequest BuildRequest(List<JsonElement> toolDefs, bool forceNothing) => new()
        {
            ModelName = o.ModelName,
            Messages = messages,
            Tools = toolDefs,
            EnableThinking = o.EnableThinking,
            PreserveThinking = o.PreserveThinking,
            ReasoningEffort = o.ReasoningEffort,
            EnableShellExecute = forceNothing ? false : o.EnableShellExecute,
            ForceToolName = null,
            PersonalityId = o.PersonalityId,
            EnableWritableChat = o.EnableWritableChat,
            EnablePlanning = false,
            EnableImplementation = false,
            ExpertId = null,
            EnableSqlTools = false,
            IsolatedContext = false,
            EnableVerification = false,
            EnableBuildAgent = false,
            EnableAskUser = false,
            EnablePlaywrightTools = false,
            McpPromptIds = [],
            WorkingDirectory = null, // Chat roots its tools in a per-session folder and sends no path (app.js #promptWorkingDirectory)
            SessionNotes = o.SessionNotes
        };

        var handlers = new TurnStreamHandlers
        {
            OnToken = t => hooks.OnToken?.Invoke(t),
            OnThinking = t => hooks.OnThinking?.Invoke(t),
            OnStatus = s => hooks.OnStatus?.Invoke(s)
        };

        // Reconnect-and-retry for a dropped SSE connection: only TRANSPORT failures, bounded with backoff.
        async Task<TurnResponse> SubmitWithRetryAsync(TurnRequest request)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await api.SubmitTurnAsync(sessionId, request, handlers, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested && attempt < MaxTransportRetries
                                           && (ex is EinkrApiException { IsTransient: true } || ex is HttpRequestException))
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, attempt)));
                    hooks.OnStatus?.Invoke($"Connection dropped -- reconnecting (attempt {attempt + 1}/{MaxTransportRetries})...");
                    logger.LogWarning(ex, "Transport error on round; retrying in {Delay}.", delay);
                    await Task.Delay(delay, ct);
                }
            }
        }

        // Salvage: ONE final model call with NO tools, forcing a text answer from everything learned so far.
        async Task<string?> SalvageAsync(string situation, string instruction)
        {
            messages.Add(WireMessage.User($"{situation}\n\n{instruction}"));
            try
            {
                var response = await SubmitWithRetryAsync(BuildRequest([], forceNothing: true));
                if (response.PromptTokens > 0) hooks.OnPromptTokens?.Invoke(response.PromptTokens);
                var text = (response.Text ?? "").Trim();
                return text.Length > 0 ? text : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Salvage call failed.");
                return null;
            }
        }

        async Task<string?> RequestRemedyAsync(WireToolCall call, string failedResult)
        {
            try
            {
                var available = new List<object>();
                var availableNames = new HashSet<string>();
                foreach (var def in o.ToolDefinitions)
                {
                    var fn = def.TryGetProperty("function", out var f) ? f : def;
                    var name = fn.GetProperty("name").GetString() ?? "";
                    if (!RemedyTools.Contains(name)) continue;
                    availableNames.Add(name);

                    var required = fn.TryGetProperty("parameters", out var pr) && pr.TryGetProperty("required", out var rq)
                        ? rq.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
                    var parms = fn.TryGetProperty("parameters", out var pp) && pp.TryGetProperty("properties", out var props)
                        ? string.Join(", ", props.EnumerateObject().Select(p =>
                            $"{p.Name}: {(p.Value.TryGetProperty("type", out var ty) ? ty.GetString() : "any")}{(required.Contains(p.Name) ? " (required)" : "")}"))
                        : "";
                    var desc = (fn.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "").Split('\n')[0];
                    if (desc.Length > 200) desc = desc[..200];
                    available.Add(new { name, signature = $"{name}({parms})", description = desc });
                }
                if (availableNames.Count == 0) return null;

                hooks.OnStatus?.Invoke("Looking up what the failing call needs...");

                var listing = "";
                try
                {
                    using var doc = JsonDocument.Parse("""{"path":"."}""");
                    var raw = await tools.ExecuteAsync("list_dir", doc.RootElement.Clone(), ct);
                    if (!raw.StartsWith("Error", StringComparison.Ordinal)) listing = raw.Length > RemedyListingMaxChars ? raw[..RemedyListingMaxChars] : raw;
                }
                catch { /* the listing is context, not required */ }

                // The "user request": the first user message of this turn.
                var userRequest = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
                if (userRequest.Length > 1500) userRequest = userRequest[..1500];

                var response = await api.SuggestToolRemedyAsync(new
                {
                    toolName = call.Name,
                    argumentsJson = call.ArgumentsJson ?? "{}",
                    error = failedResult.Length > 1500 ? failedResult[..1500] : failedResult,
                    userRequest,
                    workingDirectoryListing = listing,
                    recentCalls = recentCallLog.TakeLast(RemedyRecentCalls).ToList(),
                    tools = available
                }, ct);

                var tool = response.Tool;
                if (string.IsNullOrEmpty(tool) || !availableNames.Contains(tool)) return null;

                JsonElement args;
                try { args = string.IsNullOrWhiteSpace(response.ArgumentsJson) ? JsonDocument.Parse("{}").RootElement : JsonDocument.Parse(response.ArgumentsJson).RootElement.Clone(); }
                catch (JsonException) { return null; }
                if (args.ValueKind != JsonValueKind.Object) return null;

                var suggested = $"{tool}:{args.GetRawText()}";
                try
                {
                    var failed = $"{call.Name}:{JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson).RootElement.GetRawText()}";
                    if (suggested == failed) return null; // the exact call that just failed is no remedy
                }
                catch (JsonException) { /* malformed original; fall through */ }

                var output = await tools.ExecuteAsync(tool, args, ct);
                if (output.StartsWith("Error", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(output)) return null;

                var shown = output.Length > RemedyResultMaxChars ? output[..RemedyResultMaxChars] + "\n[...truncated]" : output;
                return $"\n\n[Automatic lookup, run for you because this call keeps failing: {tool}({args.GetRawText()})]\n{shown}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        for (var iteration = 0; iteration < o.MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // Wind-down (Josh arrived / budget spent): no more work, ask for a short wrap-up in text.
            var windDown = hooks.WindDownReason?.Invoke();
            if (windDown != null)
            {
                hooks.OnStatus?.Invoke($"Winding down: {windDown}");
                var wrap = await SalvageAsync(windDown,
                    "Stop calling tools now -- no more tool calls will run. Wrap up in a few sentences: say what you were doing, " +
                    "anything half-finished that you want to pick up next time (or leave in a note), and nothing else.");
                finalText = wrap ?? "[Wake ended early.]";
                endReason = "wound-down";
                break;
            }

            var reclaimed = o.EnablePruning
                ? PruneOldToolResults(messages, o.ContextWindowSize > 0
                    ? Math.Max(PruneProtectTokens, (int)Math.Floor(o.ContextWindowSize * o.PruneFraction))
                    : PruneProtectTokens)
                : 0;
            if (reclaimed > 0) hooks.OnStatus?.Invoke($"Cleared ~{Math.Round(reclaimed / 1000.0)}k tokens of older tool results to free context.");

            var response = await SubmitWithRetryAsync(BuildRequest(o.ToolDefinitions, forceNothing: false));
            rounds++;

            if (response.PromptTokens > 0)
            {
                lastPromptTokens = response.PromptTokens;
                hooks.OnPromptTokens?.Invoke(response.PromptTokens);
            }

            if (response.FinishReason != "tool_calls" || response.ToolCalls is not { Count: > 0 })
            {
                finalText = response.Text ?? "";
                break;
            }

            messages.Add(new WireMessage { Role = "assistant", Content = response.Text, ToolCalls = response.ToolCalls });

            var progressedThisRound = false;
            foreach (var call in response.ToolCalls)
            {
                JsonElement args;
                try { args = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? JsonDocument.Parse("{}").RootElement.Clone() : JsonDocument.Parse(call.ArgumentsJson).RootElement.Clone(); }
                catch (JsonException) { args = JsonDocument.Parse("{}").RootElement.Clone(); } // let the tool's own validation surface the error

                // A call whose arguments were cut off is not a call; executing it hands the tool an empty argument object.
                if (call.ArgumentsTruncated)
                {
                    var preview = (call.RawArguments ?? "");
                    if (preview.Length > 400) preview = preview[..400];
                    var truncatedResult = "Error: your arguments for this call were cut off before they were "
                        + "complete, so the call could not be made. This is an output budget limit, not a "
                        + "problem with the tool, the file, or its permissions. The argument either ran to the "
                        + "limit without ever finishing, or it got stuck repeating itself -- a long run of the same "
                        + "character or line, such as a comment banner copied from source, is a common cause.\n\n"
                        + (preview.Length > 0 ? $"The call began:\n{preview}\n\n" : "")
                        + "Retrying the same call will fail the same way. Make it much smaller and write it fresh: "
                        + "keep each call to one short piece of content, never copy long runs of repeated characters, "
                        + "and for a large file write or edit one section at a time.";
                    hooks.OnToolCall?.Invoke(call.Name, call.RawArguments ?? "{}", truncatedResult);
                    messages.Add(new WireMessage { Role = "tool", ToolCallId = call.Id, Content = truncatedResult });
                    toolCallCount++;
                    continue;
                }

                var toolLabel = $"{call.Name}({FormatArgs(args)})";
                hooks.OnStatus?.Invoke($"Running {toolLabel}...");
                toolCallCount++;

                var signature = $"{call.Name}:{call.ArgumentsJson}";
                var loopError = CheckForToolCallLoop(signature) ?? CheckForRepeatedCall(signature);
                string result;
                if (loopError != null)
                {
                    result = loopError;
                    if (++loopRefusals >= MaxLoopRefusals) { abandon = true; abandonReason = "loop"; }

                    // The refusal text alone is the same words on repeat; bring something NEW into context.
                    if (remediesThisTurn < RemedyMaxPerTurn && remediedSignatures.Add(signature))
                    {
                        remediesThisTurn++;
                        var extra = await RequestRemedyAsync(call, loopError);
                        if (extra != null) result += extra;
                    }
                }
                else
                {
                    result = await tools.ExecuteAsync(call.Name, args, ct);
                    loopRefusals = 0;
                    RecordCallResult(signature, result);

                    if (IsFailedResult(call.Name, result))
                    {
                        var streak = toolFailureStreak.GetValueOrDefault(call.Name) + 1;
                        toolFailureStreak[call.Name] = streak;
                        if (streak >= RemedyAfterFailures && remediesThisTurn < RemedyMaxPerTurn && remediedSignatures.Add(signature))
                        {
                            remediesThisTurn++;
                            var extra = await RequestRemedyAsync(call, result);
                            if (extra != null) result += extra;
                        }
                    }
                    else toolFailureStreak.Remove(call.Name);

                    recentCallLog.Add(new
                    {
                        name = call.Name,
                        argumentsJson = (call.ArgumentsJson ?? "").Length > 300 ? call.ArgumentsJson![..300] : call.ArgumentsJson ?? "",
                        resultPreview = result.Length > 160 ? result[..160] : result
                    });
                    if (recentCallLog.Count > 12) recentCallLog.RemoveAt(0);

                    if (ProgressTools.Contains(call.Name)
                        || (DiscoveryProgressTools.Contains(call.Name) && !result.StartsWith("Error:", StringComparison.Ordinal)))
                        progressedThisRound = true;
                }

                // Too big to keep in the conversation: move it out before it reaches `messages` or the tool log.
                if (result.Length > resultLimitChars && !SpillToolNames.Contains(call.Name))
                    result = await ContainLargeResultAsync(result);

                hooks.OnToolCall?.Invoke(call.Name, call.ArgumentsJson ?? "{}", result);
                messages.Add(new WireMessage { Role = "tool", ToolCallId = call.Id, Content = result });
            }

            if (progressedThisRound) roundsSinceProgress = 0; else roundsSinceProgress++;
            if (!abandon && roundsSinceProgress >= NoProgressLimit) { abandon = true; abandonReason = "no-progress"; }

            if (abandon)
            {
                // A bounded, behaviour-only intervention before giving up (no tool names -- naming one makes the model latch on).
                if (abandonReason == "loop" && loopInterventions < MaxLoopInterventions)
                {
                    loopInterventions++;
                    loopRefusals = 0;
                    abandon = false;
                    abandonReason = null;
                    recent.Clear();
                    messages.Add(WireMessage.User(
                        "Stop -- you are repeating the same step over and over and it will not give you anything "
                        + "new. You already have what you need from it. Do not do that again. Move on and finish what "
                        + "this wake was for, and produce the real result now."));
                    hooks.OnStatus?.Invoke("Looks stuck -- nudging it to move on.");
                    continue;
                }

                var situation = abandonReason == "no-progress"
                    ? "You have gone many rounds without changing any files."
                    : "You are repeating the same tool call and it is being refused; you are not making progress.";
                var salvaged = await SalvageAsync(situation,
                    "Stop calling tools now -- no more tool calls will run. Using everything you have already learned this wake, "
                    + "say briefly what you did, what you concluded, and what remains. Do not apologise or describe being stuck.");
                finalText = salvaged ?? (abandonReason == "no-progress"
                    ? "[Stopped: many rounds with no progress.]"
                    : "[Stopped: caught in a repeated tool-call loop.]");
                endReason = abandonReason ?? "abandoned";
                break;
            }
        }

        if (finalText is null)
        {
            var salvaged = await SalvageAsync("You have used all the tool-call rounds available for this wake.",
                "Stop calling tools now -- no more tool calls will run. Say briefly what you did and what remains.");
            finalText = salvaged ?? $"[Stopped after {o.MaxIterations} tool-call rounds without a final response.]";
            endReason = "max-rounds";
        }

        return new RunnerResult(finalText, rounds, toolCallCount, endReason, lastPromptTokens);
    }
}
