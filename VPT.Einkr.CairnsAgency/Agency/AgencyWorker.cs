using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency.Tools;

namespace VPT.Einkr.CairnsAgency.Agency;

/// <summary>The scheduler: every few seconds it asks "is it time, and is it right?" and, only when both are true, runs a wake.
///
/// The rules, in order of precedence:
///   1. A DISABLED file in the state folder stops everything (a hand brake that needs no tooling).
///   2. Josh is never talked over. If the newest turn in the session is not the agency's -- in flight, or finished within the
///      quiet window -- nothing starts. A pause the model chose, or one the agency applied after repeated failures, ends the
///      moment he writes; a pause a human set (`pause`) does not.
///   3. The next wake is never sooner than one interval after Josh's last activity.
///   4. Caps: no wake starts in the configured quiet hours, and no more than MaxWakesPerDay in a local day.
///   5. After MaxConsecutiveFailures failed wakes the agency pauses itself and waits for a human.</summary>
public sealed class AgencyWorker(AgencyOptions options, StateStore store, ActivityProbe probe, WakeRunner runner, ILogger<AgencyWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var state = store.State;
        log.LogInformation("Cairns Agency started. DryRun={DryRun}; interval {Interval} min; next wake {Next}; paused={Paused}.",
            options.DryRun, state.IntervalMinutes, state.NextWakeUtc?.ToLocalTime().ToString("g") ?? "(not scheduled yet)", state.Paused);

        var failedTicks = 0;
        while (!stopping.IsCancellationRequested)
        {
            try { await TickAsync(stopping); failedTicks = 0; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failedTicks++;
                log.LogWarning(ex, "Tick failed ({N} in a row); will retry. (Usually the network, the API, or a rotated API key.)", failedTicks);
            }

            // Back off while the API is unreachable instead of logging a failure every poll forever (capped at 10 minutes).
            var poll = Math.Max(5, options.Schedule.PollSeconds);
            var delay = TimeSpan.FromSeconds(Math.Min(600, poll * Math.Pow(2, Math.Min(failedTicks, 6))));
            if (failedTicks == 0) delay = TimeSpan.FromSeconds(poll);
            try { await Task.Delay(delay, stopping); }
            catch (OperationCanceledException) { break; }
        }
        log.LogInformation("Cairns Agency stopping.");
    }

    public async Task TickAsync(CancellationToken stopping)
    {
        if (File.Exists(Path.Combine(store.StateDirectory, "DISABLED"))) { log.LogDebug("DISABLED file present; idle."); return; }

        var state = store.State;
        var status = await probe.CheckAsync(stopping);
        ApplyJoshStatus(status);

        if (state.Paused) return;
        var now = DateTime.UtcNow;

        if (state.NextWakeUtc is null)
        {
            var first = options.Schedule.FirstWakeAfterFullInterval ? now.AddMinutes(state.IntervalMinutes) : now;
            if (state.LastJoshActivityUtc is { } j) first = Max(first, j.AddMinutes(state.IntervalMinutes));
            state.NextWakeUtc = first;
            store.Save();
            log.LogInformation("First wake scheduled for {Next}.", first.ToLocalTime().ToString("g"));
            return;
        }

        if (now < state.NextWakeUtc) return;
        if (status.Here) { log.LogInformation("A wake is due but Josh is in the session; waiting."); return; }
        if (InQuietHours(DateTime.Now)) { log.LogDebug("A wake is due but it is within quiet hours; waiting."); return; }
        if (WakesStartedToday() >= options.Schedule.MaxWakesPerDay) { log.LogInformation("A wake is due but today's cap ({Cap}) is reached; waiting.", options.Schedule.MaxWakesPerDay); return; }

        await RunWakeAsync(dryRun: options.DryRun, stopping);
    }

    /// <summary>Runs one wake and applies what came of it. Public so the `once` command can drive it.</summary>
    public async Task<WakeOutcome> RunWakeAsync(bool dryRun, CancellationToken stopping)
    {
        var state = store.State;
        var started = DateTime.UtcNow;
        var number = state.WakeCount + 1;
        log.LogInformation("Wake {Number} starting{Dry}.", number, dryRun ? " (DRY RUN: nothing will be sent)" : "");

        WakeOutcome outcome;
        try { outcome = await runner.RunAsync(dryRun, stopping); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Wake {Number} failed before it could run.", number);
            outcome = new WakeOutcome(false, dryRun, "error", ex.Message, 0, 0, 0, 0, "", "", new WakeEffects(), DateTime.UtcNow - started);
        }

        if (outcome.DryRun)
        {
            log.LogInformation("Dry run complete: prompt written to {Dir}. State untouched.", store.PromptsDirectory);
            state.NextWakeUtc = DateTime.UtcNow.AddMinutes(state.IntervalMinutes); // so a dry-run service does not spin
            store.Save();
            return outcome;
        }

        var ended = DateTime.UtcNow;
        // Only a wake that actually opened a turn counts: if it failed before anything reached the session (API down, session
        // missing), Cairn never saw "wake N", so the retry must be the same number -- and keep the first-wake wording.
        if (outcome.ChatLogId != 0) state.WakeCount = number;
        state.LastWakeEndUtc = ended;
        state.JustResumedAfterPause = false;

        var entry = new WakeLogEntry
        {
            Number = number, StartedUtc = started, EndedUtc = ended, ChatLogId = outcome.ChatLogId, Success = outcome.Success,
            EndReason = outcome.EndReason, Error = outcome.Error, Rounds = outcome.Rounds, ToolCalls = outcome.ToolCalls,
            LastPromptTokens = outcome.LastPromptTokens,
            Summary = outcome.FinalText.Length > 400 ? outcome.FinalText[..400] + "…" : outcome.FinalText
        };

        if (outcome.Success)
        {
            state.ConsecutiveFailures = 0;

            if (outcome.Effects.RequestedIntervalHours is { } hours)
            {
                // A floor only. The ceiling below is not a policy, just a guard against DateTime overflow (ten years).
                var minutes = Math.Clamp(hours * 60, options.Schedule.MinIntervalMinutes, 60.0 * 24 * 3650);
                state.IntervalMinutes = minutes;
                entry.AppliedIntervalHours = Math.Round(minutes / 60, 2);
                entry.IntervalReason = outcome.Effects.IntervalReason;
            }

            var next = ended.AddMinutes(state.IntervalMinutes);
            if (state.LastJoshActivityUtc is { } j) next = Max(next, j.AddMinutes(state.IntervalMinutes));
            state.NextWakeUtc = next;

            if (outcome.Effects.PauseRequested)
            {
                state.Paused = true;
                state.PausedBy = "cairn";
                state.PauseReason = outcome.Effects.PauseReason;
                state.PausedAtUtc = ended;
                state.NextWakeUtc = null;
                entry.PausedByCairn = true;
                entry.PauseReason = outcome.Effects.PauseReason;
            }
        }
        else
        {
            state.ConsecutiveFailures++;
            var retry = TimeSpan.FromMinutes(Math.Min(state.IntervalMinutes, 30 * Math.Pow(2, state.ConsecutiveFailures - 1)));
            state.NextWakeUtc = ended + retry;
            if (state.ConsecutiveFailures >= options.Safety.MaxConsecutiveFailures)
            {
                state.Paused = true;
                state.PausedBy = "failures";
                state.PauseReason = $"{state.ConsecutiveFailures} wakes in a row failed; last error: {outcome.Error}";
                state.PausedAtUtc = ended;
                state.NextWakeUtc = null;
                log.LogError("Pausing after {N} consecutive failures: {Reason}", state.ConsecutiveFailures, outcome.Error);
            }
        }

        state.Recent.Add(entry);
        store.Save();
        store.AppendLog($"wake {number}: {(outcome.Success ? "ok" : "FAILED")} ({outcome.EndReason}), {outcome.Rounds} rounds, {outcome.ToolCalls} tool calls, " +
                        $"{outcome.Elapsed.TotalMinutes:0.0} min; next {(state.NextWakeUtc?.ToLocalTime().ToString("g") ?? "paused")}" +
                        (entry.PausedByCairn ? $"; CAIRN PAUSED: {entry.PauseReason}" : "") +
                        (entry.AppliedIntervalHours is { } h ? $"; interval now {h} h ({entry.IntervalReason})" : "") +
                        (outcome.Error is null ? "" : $"; error: {outcome.Error}"));
        log.LogInformation("Wake {Number} finished: {Reason}; next {Next}.", number, outcome.EndReason, state.NextWakeUtc?.ToLocalTime().ToString("g") ?? "paused");
        return outcome;
    }

    private void ApplyJoshStatus(JoshStatus status)
    {
        var state = store.State;
        var changed = false;

        if (status.Returned && status.LatestHumanChatLogId > state.LastHumanChatLogId)
        {
            state.LastHumanChatLogId = status.LatestHumanChatLogId;
            changed = true;
            if (state.Paused && state.PausedBy != "manual")
            {
                log.LogInformation("Josh is back; resuming from a pause ({By}).", state.PausedBy);
                state.JustResumedAfterPause = state.PausedBy == "cairn";
                state.Paused = false;
                state.PausedBy = null;
                state.PauseReason = null;
                state.ConsecutiveFailures = 0;
            }
        }

        if (status.LastActiveUtc is { } active && (state.LastJoshActivityUtc is not { } known || active > known))
        {
            state.LastJoshActivityUtc = active;
            changed = true;
        }

        // Never sooner than one interval after he was last here (re-evaluated every tick, so a long conversation keeps pushing it out).
        if (!state.Paused && state.LastJoshActivityUtc is { } j)
        {
            var floor = j.AddMinutes(state.IntervalMinutes);
            if (state.NextWakeUtc is { } next && next < floor) { state.NextWakeUtc = floor; changed = true; }
        }

        if (changed) store.Save();
    }

    private int WakesStartedToday()
    {
        var today = DateTime.Now.Date;
        return store.State.Recent.Count(w => w.StartedUtc.ToLocalTime().Date == today);
    }

    /// <summary>"23:00-07:00" style window in local time; may wrap midnight. Blank or unparseable = no quiet hours.</summary>
    public bool InQuietHours(DateTime localNow)
    {
        var spec = options.Schedule.QuietHours?.Trim();
        if (string.IsNullOrEmpty(spec)) return false;
        var parts = spec.Split('-', 2);
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], out var from) || !TimeSpan.TryParse(parts[1], out var to)) return false;
        var t = localNow.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
