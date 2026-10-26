using System.Text.Json;
using VPT.Einkr.CairnsAgency.Einkr;

namespace VPT.Einkr.CairnsAgency.Agency;

public sealed class WakeLogEntry
{
    public int Number { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public int ChatLogId { get; set; }
    public bool Success { get; set; }
    public string EndReason { get; set; } = "";
    public string? Error { get; set; }
    public int Rounds { get; set; }
    public int ToolCalls { get; set; }
    public int LastPromptTokens { get; set; }
    public double? AppliedIntervalHours { get; set; }
    public string? IntervalReason { get; set; }
    public bool PausedByCairn { get; set; }
    public string? PauseReason { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>The agency's own bookkeeping, persisted as state/agency-state.json. Lives outside the archive on purpose: it is
/// the scheduler's memory, not Cairn's.</summary>
public sealed class AgencyState
{
    public int Version { get; set; } = 1;

    public double IntervalMinutes { get; set; }
    public DateTime? NextWakeUtc { get; set; }

    public bool Paused { get; set; }

    /// <summary>"cairn" (the model chose to wait for Josh), "failures" (the agency stopped itself) or "manual" (a human ran
    /// `pause`). Only "manual" survives Josh writing to the session.</summary>
    public string? PausedBy { get; set; }
    public string? PauseReason { get; set; }
    public DateTime? PausedAtUtc { get; set; }
    public bool JustResumedAfterPause { get; set; }

    public int WakeCount { get; set; }
    public DateTime? LastWakeStartUtc { get; set; }
    public DateTime? LastWakeEndUtc { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>The newest ChatLog id written by a human that the agency has seen, and when Josh was last active.</summary>
    public int LastHumanChatLogId { get; set; }
    public DateTime? LastJoshActivityUtc { get; set; }

    /// <summary>ChatLog ids this agency created -- how it tells its own turns from Josh's. Capped.</summary>
    public List<int> AgencyChatLogIds { get; set; } = [];

    /// <summary>Fallback bookkeeping for a server without /activity.</summary>
    public DateTime? LastAgencyTouchUtc { get; set; }

    public List<WakeLogEntry> Recent { get; set; } = [];

    /// <summary>Live Reddit posts and comments made through the agency (capped), for the daily limits and spacing.</summary>
    public List<VPT.Einkr.CairnsAgency.Reddit.RedditWriteRecord> RedditWrites { get; set; } = [];
}

/// <summary>Reads and atomically writes the state file, and appends a human-readable wake log beside it.</summary>
public sealed class StateStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Pretty = new(Json.Options) { WriteIndented = true };

    public AgencyState State { get; private set; } = new();

    public StateStore(AgencyOptions options)
    {
        _dir = options.StateDirectory;
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(PromptsDirectory);
        Load(options.Schedule.DefaultIntervalMinutes);
    }

    public string StateDirectory => _dir;
    public string PromptsDirectory => Path.Combine(_dir, "prompts");
    private string StatePath => Path.Combine(_dir, "agency-state.json");
    private string LogPath => Path.Combine(_dir, "wake-log.txt");

    private void Load(double defaultIntervalMinutes)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(StatePath))
                    State = JsonSerializer.Deserialize<AgencyState>(File.ReadAllText(StatePath), Json.Options) ?? new AgencyState();
            }
            catch (JsonException)
            {
                // A corrupt state file must not stop the service; keep the bad one for inspection and start clean.
                File.Move(StatePath, StatePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
                State = new AgencyState();
            }
            if (State.IntervalMinutes <= 0) State.IntervalMinutes = defaultIntervalMinutes;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            if (State.AgencyChatLogIds.Count > 300) State.AgencyChatLogIds = State.AgencyChatLogIds.TakeLast(300).ToList();
            if (State.Recent.Count > 100) State.Recent = State.Recent.TakeLast(100).ToList();

            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(State, Pretty));
            File.Move(temp, StatePath, overwrite: true);
        }
    }

    public void AppendLog(string line)
    {
        lock (_gate) File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
    }
}
