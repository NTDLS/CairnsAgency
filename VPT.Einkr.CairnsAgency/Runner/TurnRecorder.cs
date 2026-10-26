using System.Text;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency.Einkr;

namespace VPT.Einkr.CairnsAgency.Runner;

/// <summary>Records a turn the way the desktop app does (app.js: #logToolCall / #flushToolCalls / #closeTurnSegment /
/// #flushSegments / #completeSubAgentTurn), so a wake reads in the Electron transcript exactly like a turn Josh ran: the same
/// ChatLog row, the same tool-call rows, the same segments (reasoning blocks and text blocks, closed whenever something else
/// happens), and the same final turn-complete. Tool calls and segments are flushed AS THEY HAPPEN, so a wake that is cut off
/// leaves a true record of how far it got. Writes are best-effort: an audit trail failing must never take down the run.</summary>
public sealed class TurnRecorder
{
    private const int RecordedResultLimit = 65536; // app.js RECORDED_RESULT_LIMIT

    private readonly EinkrApiClient _api;
    private readonly Guid _sessionId;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writes = new(1, 1);

    private readonly StringBuilder _openText = new();
    private readonly StringBuilder _openThinking = new();
    private string _pendingWhitespace = "";
    private readonly StringBuilder _thinkingAll = new();
    private readonly List<TurnSegment> _segments = [];
    private int _segmentsFlushed;
    private readonly List<ToolCallRecord> _toolCalls = [];
    private int _toolCallsFlushed;

    public int ChatLogId { get; }

    public TurnRecorder(EinkrApiClient api, Guid sessionId, int chatLogId, ILogger log)
    {
        _api = api;
        _sessionId = sessionId;
        ChatLogId = chatLogId;
        _log = log;
    }

    public string AllThinking { get { lock (_lock) return _thinkingAll.ToString(); } }
    public bool HasText { get { lock (_lock) return _segments.Any(s => s.Kind == "text") || _openText.ToString().Trim().Length > 0; } }
    /// <summary>Everything the model has said so far, closed blocks and the open one -- what is kept if a wake is cut off.</summary>
    public string TextSoFar
    {
        get
        {
            lock (_lock)
                return string.Join("\n\n", _segments.Where(s => s.Kind == "text").Select(s => s.Content).Append(_openText.ToString()).Where(t => t.Trim().Length > 0));
        }
    }
    public int ToolCallCount { get { lock (_lock) return _toolCalls.Count; } }

    public void OnToken(string token)
    {
        lock (_lock)
        {
            if (_openThinking.Length > 0) CloseThinkingLocked();
            _openText.Append(token);
        }
    }

    public void OnThinking(string token)
    {
        lock (_lock)
        {
            _thinkingAll.Append(token); // the whole stream, including whitespace-only deltas (ChatLog.Thinking)

            if (_openThinking.Length == 0)
            {
                // A delta that is only whitespace is not reasoning: hold it rather than splitting the text block mid-sentence.
                if (token.Trim().Length == 0) { _pendingWhitespace += token; return; }
                CloseTextLocked();
                _openThinking.Append(_pendingWhitespace);
                _pendingWhitespace = "";
            }
            _openThinking.Append(token);
        }
    }

    public void OnToolCall(string name, string argumentsJson, string result)
    {
        lock (_lock)
        {
            CloseTextLocked();
            CloseThinkingLocked();
            _toolCalls.Add(new ToolCallRecord { ToolName = name, ArgumentsJson = argumentsJson, Result = Truncate(result) });
        }
        _ = FlushAsync();
    }

    private void CloseThinkingLocked()
    {
        _pendingWhitespace = "";
        var text = _openThinking.ToString();
        _openThinking.Clear();
        if (text.Trim().Length > 0) _segments.Add(new TurnSegment { Kind = "thinking", Content = text });
    }

    private void CloseTextLocked()
    {
        var raw = _openText.ToString();
        _openText.Clear();
        if (raw.Trim().Length > 0) _segments.Add(new TurnSegment { Kind = "text", Content = raw });
    }

    private static string Truncate(string? result)
    {
        var text = result ?? "";
        return text.Length <= RecordedResultLimit ? text
            : $"{text[..RecordedResultLimit]}\n... [truncated for recording, {text.Length - RecordedResultLimit} more characters]";
    }

    /// <summary>Sends whatever tool calls and segments have closed since the last flush. The cursors advance BEFORE the request,
    /// so a failed flush drops that batch instead of re-sending it -- duplicated tool calls would be worse than a gap.</summary>
    public async Task FlushAsync()
    {
        await _writes.WaitAsync();
        try
        {
            List<ToolCallRecord> tools;
            List<TurnSegment> segs;
            lock (_lock)
            {
                tools = _toolCalls.Skip(_toolCallsFlushed).ToList();
                _toolCallsFlushed = _toolCalls.Count;
                var from = _segmentsFlushed;
                segs = _segments.Skip(from).Select((s, i) => new TurnSegment { Sequence = from + i, Kind = s.Kind, Content = s.Content }).ToList();
                _segmentsFlushed = _segments.Count;
            }

            if (tools.Count > 0)
            {
                try { await _api.AppendToolCallsAsync(_sessionId, ChatLogId, tools, CancellationToken.None); }
                catch (Exception ex) { _log.LogWarning(ex, "Flushing tool calls failed; a gap in the record, the wake continues."); }
            }
            if (segs.Count > 0)
            {
                try { await _api.AppendSegmentsAsync(_sessionId, ChatLogId, segs, CancellationToken.None); }
                catch (Exception ex) { _log.LogWarning(ex, "Flushing segments failed; a gap in the transcript, the wake continues."); }
            }
        }
        finally { _writes.Release(); }
    }

    /// <summary>Closes the turn: flushes what is left and writes the response -- the same payload app.js's completeTurn sends.</summary>
    public async Task CompleteAsync(string finalText, int modelId, int personalityId, bool enableThinking, bool preserveThinking, string reasoningEffort)
    {
        lock (_lock)
        {
            CloseTextLocked();
            CloseThinkingLocked();
            // The reply arrived only in the Done event (nothing streamed): keep it as the turn's text so a replay shows it.
            if (!_segments.Any(s => s.Kind == "text") && finalText.Trim().Length > 0)
                _segments.Add(new TurnSegment { Kind = "text", Content = finalText });
        }
        await FlushAsync();

        await _api.CompleteTurnAsync(_sessionId, new
        {
            chatLogId = ChatLogId,
            prompt = (string?)null, // only sent when steering rewrote it; a wake takes none
            response = finalText,
            thinking = AllThinking,
            modelId,
            personalityId,
            enableThinking,
            preserveThinking,
            reasoningEffort,
            segments = new List<TurnSegment>() // the flush above already wrote every one
        }, CancellationToken.None);
    }
}
