using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VPT.Einkr.CairnsAgency.Tests;

/// <summary>One scripted model round: what the fake /turn endpoint streams back.</summary>
public sealed class Round
{
    public string[] Thinking { get; init; } = [];
    public string[] Tokens { get; init; } = [];
    public string? Text { get; init; }
    public (string Id, string Name, string Args)[] ToolCalls { get; init; } = [];
    public int PromptTokens { get; init; } = 1000;
    public int DelayMs { get; init; }
    public int? HttpStatus { get; init; }

    public static Round Say(string text) => new() { Tokens = [text], Text = text };
    public static Round Call(string name, string args, string id = "call_1") => new() { ToolCalls = [(id, name, args)] };
}

public sealed record Seen(string Method, string Path, string Body)
{
    public JsonDocument Json() => JsonDocument.Parse(Body);
}

/// <summary>An in-process stand-in for the Einkr API: just the endpoints the agency calls, scripted. Everything the agency
/// sends is recorded so tests can assert on the exact wire traffic.</summary>
public sealed class FakeEinkr : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly object _gate = new();

    public List<Seen> Requests { get; } = [];
    public Queue<Func<JsonElement, Round>> Rounds { get; } = new();

    public Guid SessionId { get; }
    public string? SessionNotes { get; set; }
    public string BaseUrl { get; }
    public int NextChatLogId { get; set; } = 901;
    public string WebFetchResult { get; set; } = "page";

    /// <summary>Null = the endpoint 404s (an API that predates /activity).</summary>
    public Func<object?>? Activity { get; set; }

    private FakeEinkr(WebApplication app, string baseUrl, Guid sessionId)
    {
        _app = app;
        BaseUrl = baseUrl;
        SessionId = sessionId;
    }

    public void Enqueue(Round round) => Rounds.Enqueue(_ => round);
    public void Enqueue(Func<JsonElement, Round> round) => Rounds.Enqueue(round);

    /// <summary>Only the model calls (paths ending in /turn, not /turn-begin etc.).</summary>
    public List<Seen> Turns() { lock (_gate) return Requests.Where(r => r.Path.EndsWith("/turn")).ToList(); }

    public IEnumerable<Seen> To(string pathContains) { lock (_gate) return Requests.Where(r => r.Path.Contains(pathContains)).ToList(); }

    public static async Task<FakeEinkr> StartAsync(Guid sessionId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        var app = builder.Build();
        FakeEinkr? self = null;

        async Task<string> Body(HttpContext c)
        {
            using var reader = new StreamReader(c.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            lock (self!._gate) self.Requests.Add(new Seen(c.Request.Method, c.Request.Path + c.Request.QueryString, body));
            return body;
        }
        void Note(HttpContext c)
        {
            lock (self!._gate) self.Requests.Add(new Seen(c.Request.Method, c.Request.Path + c.Request.QueryString, ""));
        }

        app.MapGet("/api/models", (HttpContext c) => { Note(c); return Results.Json(new[] { new { id = 58, name = "Stratum-27B", contextWindowSize = 245760, compactionTokenSize = 212992 } }); });
        app.MapGet("/api/desktop/sessions", (HttpContext c) =>
        {
            Note(c);
            return Results.Json(new[] { new { id = self!.SessionId, name = "Cairn", lastUsedUTC = DateTime.UtcNow, sessionNotes = self.SessionNotes } });
        });
        app.MapGet("/api/desktop/sessions/{id:guid}/activity", (HttpContext c) =>
        {
            Note(c);
            var a = self!.Activity?.Invoke();
            return a is null ? Results.NotFound() : Results.Json(a);
        });
        app.MapPost("/api/desktop/sessions/{id:guid}/turn-begin", async (HttpContext c) => { await Body(c); return Results.Json(new { chatLogId = self!.NextChatLogId++ }); });
        foreach (var path in new[] { "turn-tool-calls", "turn-segments", "turn-complete", "session-notes" })
            app.MapPost($"/api/desktop/sessions/{{id:guid}}/{path}", async (HttpContext c) => { await Body(c); return Results.Json(new { ok = true }); });
        app.MapPost("/api/desktop/notes/validate", async (HttpContext c) =>
        {
            var doc = JsonDocument.Parse(await Body(c));
            var notes = doc.RootElement.GetProperty("notes").EnumerateArray().Select(n => n.GetString()).ToList();
            return Results.Json(new { kept = notes, dropped = Array.Empty<object>() });
        });
        app.MapPost("/api/desktop/tools/web-search", async (HttpContext c) => { await Body(c); return Results.Json(new { result = "1. A result\n   https://example.com" }); });
        app.MapPost("/api/desktop/tools/web-fetch", async (HttpContext c) => { await Body(c); return Results.Json(new { result = self!.WebFetchResult }); });
        app.MapPost("/api/desktop/tools/suggest-remedy", async (HttpContext c) => { await Body(c); return Results.Json(new { tool = (string?)null, argumentsJson = (string?)null, reason = "none" }); });

        app.MapPost("/api/desktop/sessions/{id:guid}/turn", async (HttpContext c) =>
        {
            var body = await Body(c);
            using var doc = JsonDocument.Parse(body);
            Func<JsonElement, Round>? next;
            lock (self!._gate) next = self.Rounds.Count > 0 ? self.Rounds.Dequeue() : null;
            if (next is null) { c.Response.StatusCode = 500; await c.Response.WriteAsync("no scripted round left"); return; }

            var round = next(doc.RootElement.Clone());
            if (round.HttpStatus is { } status) { c.Response.StatusCode = status; await c.Response.WriteAsync("scripted failure"); return; }
            if (round.DelayMs > 0) await Task.Delay(round.DelayMs);

            c.Response.ContentType = "text/event-stream";
            async Task Event(string name, object data)
            {
                await c.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n");
                await c.Response.Body.FlushAsync();
            }
            foreach (var t in round.Thinking) await Event("Thinking", new { content = t });
            foreach (var t in round.Tokens) await Event("Token", new { content = t });
            if (round.ToolCalls.Length > 0)
                await Event("Done", new
                {
                    finishReason = "tool_calls", text = round.Text, promptTokens = round.PromptTokens, completionTokens = 10,
                    toolCalls = round.ToolCalls.Select(t => new { id = t.Id, name = t.Name, argumentsJson = t.Args, argumentsTruncated = false, rawArguments = (string?)null })
                });
            else
                await Event("Done", new { finishReason = "stop", text = round.Text ?? string.Concat(round.Tokens), promptTokens = round.PromptTokens, completionTokens = 10 });
        });

        await app.StartAsync();
        var baseUrl = app.Urls.First();
        self = new FakeEinkr(app, baseUrl, sessionId);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
