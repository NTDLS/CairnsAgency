using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency.Einkr;
using VPT.Einkr.CairnsAgency.Notes;
using VPT.Einkr.CairnsAgency.Runner;
using VPT.Einkr.CairnsAgency.Tools;

namespace VPT.Einkr.CairnsAgency.Agency;

public sealed record WakeOutcome(
    bool Success, bool DryRun, string EndReason, string? Error, int ChatLogId, int Rounds, int ToolCalls, int LastPromptTokens,
    string FinalText, string Prompt, WakeEffects Effects, TimeSpan Elapsed);

/// <summary>Runs ONE wake: builds the context and the nudge, opens a turn on Cairn's session, runs the same agent loop the desktop
/// app runs for a Chat turn, records it the same way, and reports what the model asked the agency to change. It decides nothing
/// about WHEN -- that is the worker's job -- and in dry-run it stops before sending anything.</summary>
public sealed class WakeRunner(
    AgencyOptions options, EinkrApiClient api, ToolHostClient toolHost, ToolCatalog catalog, StateStore store, ActivityProbe probe,
    ILoggerFactory loggers, VPT.Einkr.CairnsAgency.Reddit.RedditTools? reddit = null)
{
    private readonly ILogger _log = loggers.CreateLogger<WakeRunner>();

    public string SessionDirectory => Path.Combine(string.IsNullOrWhiteSpace(options.Session.WorkingDirectoryRoot) ? new SessionOptions().WorkingDirectoryRoot : options.Session.WorkingDirectoryRoot, options.Session.SessionId);
    public string ArchiveRoot => Path.Combine(SessionDirectory, options.Session.ArchiveFolder);

    public WakeContext BuildContext(int wakeNumber, bool afterPause)
    {
        var state = store.State;
        var now = DateTimeOffset.Now;
        var context = new WakeContext
        {
            WakeNumber = wakeNumber,
            Now = now,
            Interval = TimeSpan.FromMinutes(state.IntervalMinutes),
            SincePreviousWake = state.LastWakeEndUtc is { } end ? DateTime.UtcNow - end : null,
            JoshLastActive = state.LastJoshActivityUtc is { } j ? new DateTimeOffset(DateTime.SpecifyKind(j, DateTimeKind.Utc)).ToLocalTime() : null,
            ResumedAfterPause = afterPause,
            MaxRounds = options.Safety.MaxRoundsPerWake,
            MaxMinutes = options.Safety.MaxWakeMinutes,
            MinIntervalMinutes = options.Schedule.MinIntervalMinutes,
            ArchiveFolder = options.Session.ArchiveFolder
        };
        ArchiveScanner.Fill(context, ArchiveRoot, state.LastWakeEndUtc);
        return context;
    }

    private void EnsureCharter()
    {
        var path = Path.Combine(ArchiveRoot, "AGENCY.md");
        if (File.Exists(path)) return;
        using var stream = typeof(WakeRunner).Assembly.GetManifestResourceStream("AGENCY.md")
            ?? throw new InvalidOperationException("AGENCY.md is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Directory.CreateDirectory(ArchiveRoot);
        File.WriteAllText(path, reader.ReadToEnd(), new UTF8Encoding(false));
        _log.LogInformation("Wrote {Path} (it was missing).", path);
    }

    public async Task<WakeOutcome> RunAsync(bool dryRun, CancellationToken stopping)
    {
        var state = store.State;
        var sw = Stopwatch.StartNew();
        var sessionId = Guid.Parse(options.Session.SessionId);
        var wakeNumber = state.WakeCount + 1;
        var afterPause = state.JustResumedAfterPause;

        var context = BuildContext(wakeNumber, afterPause);
        var prompt = NudgePromptBuilder.Build(context);
        var offered = catalog.Build(options.Tools, options.Schedule.MinIntervalMinutes, reddit?.Definitions());

        if (dryRun)
        {
            var file = Path.Combine(store.PromptsDirectory, $"wake-{wakeNumber:000}-dryrun.txt");
            File.WriteAllText(file, prompt + "\n\n---- tools offered ----\n" + string.Join(", ", offered.Select(ToolCatalog.NameOf)) + "\n");
            return new WakeOutcome(true, true, "dry-run", null, 0, 0, 0, 0, "", prompt, new WakeEffects(), sw.Elapsed);
        }

        // ---- live from here on: this is the only path that writes to the session ---------------------
        var notes = new NotesStore();
        var summaries = await api.ListSessionsAsync("Chat", stopping);
        var mine = summaries.FirstOrDefault(s => s.Id == sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} was not found among this user's Chat sessions.");
        notes.Load(mine.SessionNotes);

        var models = await api.GetModelsAsync(stopping);
        var contextWindow = models.FirstOrDefault(m => m.Name == options.Session.ModelName)?.ContextWindowSize ?? 0;

        EnsureCharter();

        var executor = new AgencyToolExecutor(api, toolHost, notes, SessionDirectory, sessionId, offered, options.Tools,
            loggers.CreateLogger<AgencyToolExecutor>(), reddit);

        File.WriteAllText(Path.Combine(store.PromptsDirectory, $"wake-{wakeNumber:000}.txt"), prompt);

        var started = DateTime.UtcNow;
        var chatLogId = await api.BeginTurnAsync(sessionId, prompt, stopping);
        state.AgencyChatLogIds.Add(chatLogId);
        state.LastWakeStartUtc = started;
        state.LastAgencyTouchUtc = started;
        store.Save();

        var recorder = new TurnRecorder(api, sessionId, chatLogId, loggers.CreateLogger<TurnRecorder>());

        // The wake's own clock, and a background watch for Josh arriving. Either makes the runner wind down gracefully.
        using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var budget = TimeSpan.FromMinutes(options.Safety.MaxWakeMinutes);
        wakeCts.CancelAfter(budget + TimeSpan.FromMinutes(10)); // hard stop well after the graceful one
        var joshArrived = false;
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!wakeCts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, options.Schedule.PollSeconds)), wakeCts.Token);
                    var status = await probe.CheckAsync(wakeCts.Token);
                    if (status.Returned || status.Here) { joshArrived = true; return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug(ex, "Activity watch failed once; will retry next tick."); }
        }, CancellationToken.None);

        var runnerOptions = new RunnerOptions
        {
            ModelName = options.Session.ModelName,
            PersonalityId = options.Session.PersonalityId,
            EnableThinking = options.Session.EnableThinking,
            PreserveThinking = options.Session.PreserveThinking,
            ReasoningEffort = options.Session.ReasoningEffort,
            EnableShellExecute = options.Tools.ShellExecute,
            EnableWritableChat = options.Tools.Writable,
            SessionNotes = notes.Snapshot(),
            ToolDefinitions = offered,
            ContextWindowSize = contextWindow,
            MaxIterations = options.Safety.MaxRoundsPerWake
        };

        var hooks = new RunnerHooks
        {
            OnToken = recorder.OnToken,
            OnThinking = recorder.OnThinking,
            OnToolCall = recorder.OnToolCall,
            OnStatus = s => _log.LogDebug("status: {Status}", s),
            OnPromptTokens = _ => { },
            SpillToolOutput = executor.SpillToolOutputAsync,
            WindDownReason = () =>
                joshArrived ? "Josh has just written to the session. The wake ends here so the conversation can be his."
                : sw.Elapsed > budget ? $"The wake's time budget ({options.Safety.MaxWakeMinutes} minutes) is spent."
                : stopping.IsCancellationRequested ? "The agency is shutting down."
                : null
        };

        // What the desktop app seeds a Chat turn with (placeholder system row + the new user message; the server replaces the system row).
        var messages = new List<WireMessage> { new() { Role = "system", Content = "" }, WireMessage.User(prompt) };

        RunnerResult? result = null;
        string finalText;
        string? error = null;
        try
        {
            result = await new AgentRunner(api, executor, loggers.CreateLogger<AgentRunner>())
                .RunTurnAsync(sessionId, messages, runnerOptions, hooks, wakeCts.Token);
            finalText = result.FinalText;
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            error = "the wake exceeded its hard time limit";
            finalText = $"{recorder.TextSoFar} (stopped: {error})".Trim();
        }
        catch (OperationCanceledException)
        {
            error = "the agency was stopped";
            finalText = "(stopped: the agency was shut down mid-wake)";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Wake {Number} failed.", wakeNumber);
            error = ex.Message;
            // Recorded the way the desktop app records a failed turn: as text, so the transcript shows what happened.
            finalText = $"Error: {ex.Message}";
        }
        finally
        {
            wakeCts.Cancel();
            try { await watcher; } catch { /* the watcher swallows its own */ }
        }

        // Close the row out whichever way it ended, so it is replayable and never left looking like a turn in flight.
        try
        {
            await recorder.CompleteAsync(finalText, options.Session.ModelId, options.Session.PersonalityId,
                options.Session.EnableThinking, options.Session.PreserveThinking, options.Session.ReasoningEffort);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Closing the turn failed.");
            error ??= $"closing the turn failed: {ex.Message}";
        }

        state.LastAgencyTouchUtc = DateTime.UtcNow;

        return new WakeOutcome(error is null, false, result?.EndReason ?? "error", error, chatLogId,
            result?.Rounds ?? 0, result?.ToolCalls ?? recorder.ToolCallCount, result?.LastPromptTokens ?? 0,
            finalText, prompt, executor.Effects, sw.Elapsed);
    }
}
