using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency.Agency;
using VPT.Einkr.CairnsAgency.Einkr;
using VPT.Einkr.CairnsAgency.Notes;
using VPT.Einkr.CairnsAgency.Runner;

namespace VPT.Einkr.CairnsAgency.Tools;

/// <summary>What the model asked the agency itself to change during a wake. Applied by the worker only after the wake
/// ends, and only inside the configured bounds.</summary>
public sealed class WakeEffects
{
    public double? RequestedIntervalHours { get; set; }
    public string? IntervalReason { get; set; }
    public bool PauseRequested { get; set; }
    public string? PauseReason { get; set; }
}

/// <summary>Executes the tools a Chat session's model calls -- the counterpart of #buildToolBinding's executeTool in the
/// desktop app. File and discovery tools go to the real tool host rooted at the session folder; web and view_image go
/// through the Einkr API; notes_write, get_date_time and the tool-output look-around tools run in-process (they are
/// session state or a local clock, never the tool host), as they do in the renderer; the two agency tools only record a
/// request in <see cref="WakeEffects"/>.</summary>
public sealed class AgencyToolExecutor : IToolExecutor
{
    private readonly EinkrApiClient _api;
    private readonly ToolHostClient _toolHost;
    private readonly NotesStore _notes;
    private readonly string _sessionDir;
    private readonly HashSet<string> _offered;
    private readonly ToolOptions _options;
    private readonly Guid _sessionId;
    private readonly ILogger _log;
    private readonly VPT.Einkr.CairnsAgency.Reddit.RedditTools? _reddit;

    public WakeEffects Effects { get; } = new();

    public AgencyToolExecutor(EinkrApiClient api, ToolHostClient toolHost, NotesStore notes, string sessionDir, Guid sessionId,
        IEnumerable<JsonElement> offeredTools, ToolOptions options, ILogger log, VPT.Einkr.CairnsAgency.Reddit.RedditTools? reddit = null)
    {
        _api = api;
        _toolHost = toolHost;
        _notes = notes;
        _sessionDir = sessionDir;
        _sessionId = sessionId;
        _offered = offeredTools.Select(ToolCatalog.NameOf).ToHashSet(StringComparer.Ordinal);
        _options = options;
        _log = log;
        _reddit = reddit;
    }

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        // Belt-and-suspenders, like the app: the model is only ever OFFERED the enabled tools, but a stale name from earlier
        // history could still arrive -- refuse it rather than trust the schema filter alone.
        if (!_offered.Contains(name))
            return $"Error: '{name}' is unavailable -- its tool category is disabled for this session.";

        try
        {
            switch (name)
            {
                case "notes_write": return await HandleNotesWriteAsync(args, ct);
                case "get_date_time": return DescribeCurrentDateTime(DateTimeOffset.Now);
                case "read_tool_output":
                case "search_tool_output": return await HandleToolOutputAsync(name, args, ct);
                case "view_image": return await HandleViewImageAsync(args, ct);
                case "web_search":
                    return await _api.WebSearchAsync(Str(args, "query"), Int(args, "count", 5), ct);
                case "fetch_url":
                    return await _api.WebFetchAsync(Str(args, "url"), ct);
                case var n when _reddit != null && VPT.Einkr.CairnsAgency.Reddit.RedditTools.IsRedditTool(n): return await _reddit.ExecuteAsync(n, args, ct);
                case ToolCatalog.SetWakeInterval: return HandleSetInterval(args);
                case ToolCatalog.PauseUntilJoshReturns: return HandlePause(args);
            }

            Directory.CreateDirectory(_sessionDir); // like the app's ensureDirectory: a brand-new folder must exist before list_dir
            return await _toolHost.CallAsync(_sessionDir, name, args, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tool {Tool} failed.", name);
            return $"Error: {ex.Message}";
        }
    }

    private static string Str(JsonElement a, string n) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static int Int(JsonElement a, string n, int d) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var p) && p.TryGetInt32(out var v) ? v : d;

    // ---- notes_write (app.js #handleNotesWrite) ----------------------------------------------------------

    private async Task<string> HandleNotesWriteAsync(JsonElement args, CancellationToken ct)
    {
        // Validate on the Analysis Model first: drop open/pending items, keep settled facts. Fails OPEN -- if the validator is
        // unavailable, keep what the model proposed rather than lose a note.
        var dropped = new List<DroppedNote>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("notes", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            try
            {
                var incoming = arr.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToList();
                var verdict = await _api.ValidateNotesAsync(incoming, ct);
                if (verdict.Kept is { } kept)
                {
                    args = JsonDocument.Parse(JsonSerializer.Serialize(new { notes = kept })).RootElement.Clone();
                    dropped = verdict.Dropped ?? [];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* validator unavailable -- keep what was proposed */ }
        }

        var result = _notes.Write(args);
        try { await _api.SaveSessionNotesAsync(_sessionId, _notes.ToJson(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Saving session notes failed; the next write retries."); }

        if (dropped.Count == 0) return result;
        var lines = string.Join("\n", dropped.Select(d => $"- \"{d.Text}\"{(string.IsNullOrEmpty(d.Reason) ? "" : $" -- {d.Reason}")}"));
        return $"{result}\n\nThe following were NOT kept: they are open questions or pending/unconfirmed " +
               "decisions, not settled facts. Settle them with the user (or use ask_user) rather than noting " +
               $"them -- re-add one only once it is actually decided:\n{lines}";
    }

    // ---- get_date_time (tool-definitions.js describeCurrentDateTime) -------------------------------------

    public static string DescribeCurrentDateTime(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var offsetText = $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";

        string iana = zone.Id;
        if (zone.Id == "UTC" || zone.Id == "Etc/UTC") iana = "UTC";
        else if (!zone.Id.Contains('/') && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var mapped)) iana = mapped;

        // "EDT"/"EST": initials of the zone's own standard/daylight name, which is how Windows spells them out.
        var longName = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
        var abbreviation = new string(longName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).Select(w => w[0]).ToArray());
        if (zone.Id == "UTC" || zone.BaseUtcOffset == TimeSpan.Zero && zone.StandardName.StartsWith("Coordinated", StringComparison.OrdinalIgnoreCase)) abbreviation = "UTC";
        else if (abbreviation.Length < 2) abbreviation = $"UTC{sign}{Math.Abs(offset.Hours)}";

        var inv = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return $"Today's date: {local.ToString("dddd, MMMM d, yyyy", inv)} ({local:yyyy-MM-dd})\n" +
               $"Local time: {local.ToString("h:mm tt", inv)} {abbreviation} ({iana}, {offsetText})";
    }

    // ---- read_tool_output / search_tool_output (app.js #handleToolOutputTool) ----------------------------

    public string ToolOutputDirectory => Path.Combine(_sessionDir, "tool-output");

    /// <summary>Saves an oversized tool result for the runner. The id is a hash of the saved text, so the same result spills
    /// to the same file instead of piling up copies when a model repeats a call.</summary>
    public async Task<(string Id, string FilePath)?> SpillToolOutputAsync(string text)
    {
        Directory.CreateDirectory(ToolOutputDirectory);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var id = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()[..12];
        var path = Path.Combine(ToolOutputDirectory, $"{id}.txt");
        await File.WriteAllBytesAsync(path, bytes);
        return (id, path);
    }

    private async Task<string> HandleToolOutputAsync(string name, JsonElement args, CancellationToken ct)
    {
        var id = Str(args, "id").Trim();
        if (id.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        if (!Regex.IsMatch(id, "^[0-9a-f]{6,40}$", RegexOptions.IgnoreCase))
            return "Error: id must be the tool output id from the stand-in message (letters a-f and digits), not a path.";

        if (!File.Exists(Path.Combine(ToolOutputDirectory, $"{id}.txt")))
            return $"Error: no saved tool output \"{id}\" in this session. Use an id from a stand-in message.";

        static int ToInt(JsonElement a, string n, int fallback) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var p) && p.TryGetDouble(out var d) && d > 0 ? (int)Math.Floor(d) : fallback;

        string json;
        if (name == "read_tool_output")
        {
            json = JsonSerializer.Serialize(new { path = $"{id}.txt", offset = ToInt(args, "offset", 1), limit = ToInt(args, "limit", 300) });
        }
        else
        {
            var pattern = Str(args, "pattern");
            if (pattern.Length == 0) return "Error: pattern is required.";
            var maxResults = Math.Min(100, Math.Max(1, ToInt(args, "max_results", 30)));
            var ctx = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("context_lines", out var cl) && cl.TryGetInt32(out var cv) ? Math.Min(10, Math.Max(0, cv)) : 2;
            var ci = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("case_insensitive", out var cip) && cip.ValueKind == JsonValueKind.True;
            json = JsonSerializer.Serialize(new { pattern, file_glob = $"{id}.txt", max_results = maxResults, context_lines = ctx, case_insensitive = ci });
        }

        using var doc = JsonDocument.Parse(json);
        return await _toolHost.CallAsync(ToolOutputDirectory, name == "read_tool_output" ? "read_file" : "search_code", doc.RootElement.Clone(), ct);
    }

    // ---- view_image (app.js #handleViewImage) -------------------------------------------------------------

    private async Task<string> HandleViewImageAsync(JsonElement args, CancellationToken ct)
    {
        var path = Str(args, "path");
        if (path.Length == 0) return "Error: 'path' is required.";

        string imageBase64, mimeType;
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
            var raw = await _toolHost.CallAsync(_sessionDir, "__read_image_base64", doc.RootElement.Clone(), ct);
            using var parsed = JsonDocument.Parse(raw);
            if (parsed.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String) return $"Error: {err.GetString()}";
            imageBase64 = parsed.RootElement.GetProperty("base64").GetString() ?? "";
            mimeType = parsed.RootElement.GetProperty("mimeType").GetString() ?? "image/png";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: could not read image '{path}' -- {ex.Message}";
        }

        try
        {
            var description = await _api.ViewImageAsync(imageBase64, mimeType, Str(args, "question"), ct);
            return string.IsNullOrEmpty(description) ? "The image was read but no description could be produced." : description;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: could not describe the image -- {ex.Message}. The Coding Model may not be configured or may not support image input.";
        }
    }

    // ---- the agency's own two tools ------------------------------------------------------------------------

    private string HandleSetInterval(JsonElement args)
    {
        if (!(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("hours", out var h) && h.TryGetDouble(out var hours) && hours > 0))
            return "Error: 'hours' is required and must be a positive number.";

        Effects.RequestedIntervalHours = hours;
        Effects.IntervalReason = Str(args, "reason");
        return $"Noted: next wake in about {hours:0.##} hours from when this wake ends (the agency will apply its own floor and ceiling and may adjust the exact figure).";
    }

    private string HandlePause(JsonElement args)
    {
        Effects.PauseRequested = true;
        Effects.PauseReason = Str(args, "reason");
        return "Noted. No further wakes will start until Josh writes to the session. You can finish this wake normally.";
    }
}
