using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VPT.Einkr.CairnsAgency.Einkr;

public class EinkrApiException(string message, HttpStatusCode? status = null, bool transient = false) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>A severed connection / dropped stream rather than a logical error -- worth retrying the round.</summary>
    public bool IsTransient { get; } = transient;
}

/// <summary>Callbacks for the events a /turn SSE stream carries besides the terminal Done.</summary>
public sealed class TurnStreamHandlers
{
    public Action<string>? OnToken { get; set; }
    public Action<string>? OnThinking { get; set; }
    public Action<string>? OnStatus { get; set; }
    public Action<int, string, string>? OnToolCallStart { get; set; }
}

/// <summary>C# counterpart of the desktop app's DesktopApiClient (renderer/api-client.js): the same
/// /api/desktop/* endpoints, the same Bearer API key, the same SSE protocol. Only what a Chat session's turn
/// actually calls is here.</summary>
public sealed class EinkrApiClient
{
    private readonly HttpClient _http;

    public EinkrApiClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-call timeouts below; a turn legitimately streams for many minutes
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = string.IsNullOrWhiteSpace(body) ? $"{response.ReasonPhrase ?? "no further detail"} -- possibly too large" : body;
        throw new EinkrApiException($"Einkr API returned {(int)response.StatusCode}: {detail}", response.StatusCode);
    }

    private async Task<T> GetAsync<T>(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var response = await _http.GetAsync(path, cts.Token);
        await EnsureSuccessAsync(response, cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        return Json.Deserialize<T>(json) ?? throw new EinkrApiException($"Empty response from {path}.");
    }

    private async Task<string> PostAsync(string path, object body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cts.Token);
        await EnsureSuccessAsync(response, cts.Token);
        return await response.Content.ReadAsStringAsync(cts.Token);
    }

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(5);

    // ---- read-only lookups -------------------------------------------------------------------------------

    public Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct) => GetAsync<List<ModelInfo>>("api/models", Quick, ct);

    public Task<List<SessionSummary>> ListSessionsAsync(string modeName, CancellationToken ct)
        => GetAsync<List<SessionSummary>>($"api/desktop/sessions?modeName={Uri.EscapeDataString(modeName)}", Quick, ct);

    public Task<List<TranscriptTurn>> GetTranscriptAsync(Guid sessionId, CancellationToken ct)
        => GetAsync<List<TranscriptTurn>>($"api/desktop/sessions/{sessionId}/transcript", Slow, ct);

    /// <summary>Null when the API predates the /activity endpoint (404) -- the caller then falls back to the
    /// session list's LastUsedUTC.</summary>
    public async Task<SessionActivity?> GetActivityAsync(Guid sessionId, CancellationToken ct)
    {
        try { return await GetAsync<SessionActivity>($"api/desktop/sessions/{sessionId}/activity", Quick, ct); }
        catch (EinkrApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) { return null; }
    }

    // ---- the turn itself ---------------------------------------------------------------------------------

    /// <summary>One model call, streamed as SSE. Resolves with the Done event's payload; throws on an Error event.</summary>
    public async Task<TurnResponse> SubmitTurnAsync(Guid sessionId, TurnRequest request, TurnStreamHandlers handlers, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/desktop/sessions/{sessionId}/turn")
        {
            Content = new StringContent(Json.Serialize(request), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? eventName = null;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal)) { eventName = line["event:".Length..].Trim(); continue; }
            if (!line.StartsWith("data:", StringComparison.Ordinal) || eventName is null) continue;

            using var doc = JsonDocument.Parse(line["data:".Length..].Trim());
            var data = doc.RootElement;

            switch (eventName)
            {
                case "Done":
                    return data.Deserialize<TurnResponse>(Json.Options) ?? new TurnResponse();
                case "Error":
                    throw new EinkrApiException($"Einkr API error: {(data.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error.")}");
                case "Token": handlers.OnToken?.Invoke(Str(data, "content")); break;
                case "Thinking": handlers.OnThinking?.Invoke(Str(data, "content")); break;
                case "Status": handlers.OnStatus?.Invoke(Str(data, "message")); break;
                case "ToolCallStart":
                    handlers.OnToolCallStart?.Invoke(data.TryGetProperty("index", out var i) ? i.GetInt32() : 0, Str(data, "name"), Str(data, "id"));
                    break;
            }
        }

        // Closed before Done: the connection was severed mid-turn, not a logical error. Tagged transient so the runner
        // can reconnect and retry the round -- same as the desktop app.
        throw new EinkrApiException($"Stream ended without a Done event from api/desktop/sessions/{sessionId}/turn.", transient: true);

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    }

    // ---- recording (turn-begin / tool calls / segments / complete) ---------------------------------------

    public async Task<int> BeginTurnAsync(Guid sessionId, string prompt, CancellationToken ct)
    {
        var json = await PostAsync($"api/desktop/sessions/{sessionId}/turn-begin", new { prompt }, Quick, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("chatLogId").GetInt32();
    }

    public Task AppendToolCallsAsync(Guid sessionId, int chatLogId, IReadOnlyList<ToolCallRecord> toolCalls, CancellationToken ct)
        => PostAsync($"api/desktop/sessions/{sessionId}/turn-tool-calls", new { chatLogId, toolCalls }, Slow, ct);

    public Task AppendSegmentsAsync(Guid sessionId, int chatLogId, IReadOnlyList<TurnSegment> segments, CancellationToken ct)
        => PostAsync($"api/desktop/sessions/{sessionId}/turn-segments", new { chatLogId, segments }, Quick, ct);

    public Task CompleteTurnAsync(Guid sessionId, object request, CancellationToken ct)
        => PostAsync($"api/desktop/sessions/{sessionId}/turn-complete", request, Slow, ct);

    public Task SaveSessionNotesAsync(Guid sessionId, string? notes, CancellationToken ct)
        => PostAsync($"api/desktop/sessions/{sessionId}/session-notes", new { notes }, Quick, ct);

    // ---- tool backends the server owns -------------------------------------------------------------------

    public async Task<string> WebSearchAsync(string query, int count, CancellationToken ct)
        => ResultText(await PostAsync("api/desktop/tools/web-search", new { query, count }, Slow, ct));

    public async Task<string> WebFetchAsync(string url, CancellationToken ct)
        => ResultText(await PostAsync("api/desktop/tools/web-fetch", new { url }, Slow, ct));

    public async Task<string> ViewImageAsync(string imageBase64, string mimeType, string? question, CancellationToken ct)
        => ResultText(await PostAsync("api/desktop/tools/view-image", new { imageBase64, mimeType, question }, Slow, ct));

    public async Task<ValidateNotesResponse> ValidateNotesAsync(IReadOnlyList<string> notes, CancellationToken ct)
        => Json.Deserialize<ValidateNotesResponse>(await PostAsync("api/desktop/notes/validate", new { notes }, Slow, ct)) ?? new();

    /// <summary>Never throws: a best-effort assist, any failure just means "no suggestion" (same contract as the app).</summary>
    public async Task<RemedyResponse> SuggestToolRemedyAsync(object payload, CancellationToken ct)
    {
        try { return Json.Deserialize<RemedyResponse>(await PostAsync("api/desktop/tools/suggest-remedy", payload, Slow, ct)) ?? new(); }
        catch { return new RemedyResponse { Reason = "unavailable" }; }
    }

    private static string ResultText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
    }
}
