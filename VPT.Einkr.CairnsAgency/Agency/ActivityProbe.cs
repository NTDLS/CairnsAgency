using VPT.Einkr.CairnsAgency.Einkr;

namespace VPT.Einkr.CairnsAgency.Agency;

public sealed record JoshStatus(bool Here, bool Returned, DateTime? LastActiveUtc, int LatestHumanChatLogId, string Source);

/// <summary>Works out whether Josh is in the session: the one thing the agency must never talk over. Uses the API's /activity
/// probe (newest ChatLog row, whether it is a turn still in flight) and tells Josh's turns from its own by id; against a server
/// that predates /activity it falls back to the session's last-activity stamp.</summary>
public sealed class ActivityProbe(EinkrApiClient api, StateStore store, AgencyOptions options)
{
    /// <summary>An unfinished turn this old is treated as abandoned, not as Josh still typing.</summary>
    private static readonly TimeSpan OpenTurnMaxAge = TimeSpan.FromMinutes(90);

    public async Task<JoshStatus> CheckAsync(CancellationToken ct)
    {
        var state = store.State;
        var sessionId = Guid.Parse(options.Session.SessionId);
        var quiet = TimeSpan.FromMinutes(options.Schedule.QuietAfterJoshMinutes);
        var now = DateTime.UtcNow;

        var activity = await api.GetActivityAsync(sessionId, ct);
        if (activity is not null)
        {
            var latest = activity.LastChatLogId;
            if (latest == 0) return new JoshStatus(false, false, state.LastJoshActivityUtc, state.LastHumanChatLogId, "activity");

            if (state.AgencyChatLogIds.Contains(latest))
                return new JoshStatus(false, false, state.LastJoshActivityUtc, state.LastHumanChatLogId, "activity");

            // The newest row is not ours, so it is Josh's (or another client's): he is in, or just was.
            var created = Utc(activity.LastChatLogCreatedUtc) ?? now;
            var modified = Utc(activity.LastChatLogModifiedUtc) ?? created;
            var open = activity.LastTurnOpen && now - created < OpenTurnMaxAge;
            var lastActive = open ? now : modified;
            var here = open || now - lastActive < quiet;
            return new JoshStatus(here, latest > state.LastHumanChatLogId, lastActive, latest, "activity");
        }

        // Fallback: the session list's stamp moves whenever a model round finishes, ours included -- so only a stamp newer
        // than the last time WE touched the session counts as Josh.
        var sessions = await api.ListSessionsAsync("Chat", ct);
        var mine = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (mine is null) return new JoshStatus(false, false, state.LastJoshActivityUtc, state.LastHumanChatLogId, "fallback-missing");

        var stamp = Utc(mine.LastUsedUTC)!.Value;
        var ours = state.LastAgencyTouchUtc is { } t && stamp <= t.AddSeconds(15);
        if (ours) return new JoshStatus(false, false, state.LastJoshActivityUtc, state.LastHumanChatLogId, "fallback");

        var returned = state.LastJoshActivityUtc is not { } seen || stamp > seen.AddSeconds(1);
        return new JoshStatus(now - stamp < quiet, returned, stamp, state.LastHumanChatLogId, "fallback");
    }

    private static DateTime? Utc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
