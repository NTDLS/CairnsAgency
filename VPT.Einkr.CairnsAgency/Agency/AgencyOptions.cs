namespace VPT.Einkr.CairnsAgency.Agency;

/// <summary>Everything configurable about the agency, bound from the "Agency" section of appsettings.json
/// (override per machine in appsettings.Local.json, user-secrets or EINKR_* environment variables). Defaults describe
/// Cairn's own session as the desktop app has it configured, so a fresh checkout only needs the API key.</summary>
public sealed class AgencyOptions
{
    public const string SectionName = "Agency";

    /// <summary>Safety valve, ON by default: the service runs its whole schedule and builds every nudge but sends
    /// NOTHING to the model and writes nothing to the session or the archive. Use it (or the --dry-run switch) for
    /// the first look; flip it to false only when you have read the nudge and are ready.</summary>
    public bool DryRun { get; set; } = true;

    public EinkrOptions Einkr { get; set; } = new();
    public SessionOptions Session { get; set; } = new();
    public ToolOptions Tools { get; set; } = new();
    public ScheduleOptions Schedule { get; set; } = new();
    public SafetyOptions Safety { get; set; } = new();
    public VPT.Einkr.CairnsAgency.Reddit.RedditOptions Reddit { get; set; } = new();

    /// <summary>Where the agency keeps its own state (schedule, pause flag, wake log). Outside the archive on
    /// purpose: it is the agency's bookkeeping, not Cairn's memory.</summary>
    public string StateDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "state");
}

public sealed class EinkrOptions
{
    /// <summary>The same base URL the desktop app uses (the website proxies /api/desktop/* to the API).</summary>
    public string BaseUrl { get; set; } = "https://einkr.com/";

    /// <summary>The user's Einkr API key. Leave blank here: set the EINKR_API_KEY environment variable or put it in
    /// appsettings.Local.json (git-ignored) / user-secrets.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class SessionOptions
{
    /// <summary>Cairn's chat session.</summary>
    public string SessionId { get; set; } = string.Empty; // required: the chat session to wake (appsettings.Local.json)

    public string ModelName { get; set; } = "Stratum-27B";
    public int ModelId { get; set; } = 58;
    public int PersonalityId { get; set; } = 3;

    // The session's own settings, mirrored from the desktop app's per-session toggles.
    public bool EnableThinking { get; set; } = true;
    public bool PreserveThinking { get; set; } = true;
    public string ReasoningEffort { get; set; } = "xhigh";

    /// <summary>The desktop app's global "Working Directory" setting. Chat sessions root their tools in
    /// &lt;this&gt;\&lt;sessionId&gt;, which is where the Cairn archive lives.</summary>
    public string WorkingDirectoryRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Einkr");

    /// <summary>The archive folder inside the session folder; used for change detection and the charter.</summary>
    public string ArchiveFolder { get; set; } = "Cairn";
}

public sealed class ToolOptions
{
    /// <summary>The published self-contained tool host the desktop app ships. Same binary, same tools.</summary>
    public string ToolHostPath { get; set; } =
        @"C:\VPT\VPT.Einkr\VPT.Einkr.Desktop.ToolHost\bin\Release\net10.0\win-x64\publish\VPT.Einkr.Desktop.ToolHost.exe";

    /// <summary>Writable Chat: discovery (read/outline/search/list/find/view_image) + write tools.</summary>
    public bool Writable { get; set; } = true;

    /// <summary>web_search / fetch_url (through the Einkr API, like the app).</summary>
    public bool Web { get; set; } = true;

    /// <summary>The model's own session notes (notes_write).</summary>
    public bool Notes { get; set; } = true;

    /// <summary>run_command. OFF by default and deliberately NOT mirrored from the session (which has Shell Execute
    /// on): this agent runs unattended, and a shell is the one tool that can reach outside the archive.</summary>
    public bool ShellExecute { get; set; } = false;

    /// <summary>delete_path / rename_path / edit_file / write_file are all confined to the session folder by the
    /// tool host itself. Set true to additionally refuse delete_path unattended.</summary>
    public bool ForbidDelete { get; set; } = true;
}

public sealed class ScheduleOptions
{
    /// <summary>Cadence until Cairn asks for a different one. Josh's choice: once a day.</summary>
    public int DefaultIntervalMinutes { get; set; } = 1440;

    /// <summary>The shortest interval Cairn may set with set_wake_interval (8 hours). There is deliberately no ceiling: a longer
    /// wait than any number is what pause_until_josh_returns is for.</summary>
    public int MinIntervalMinutes { get; set; } = 480;

    /// <summary>How often the worker looks at the clock.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Josh counts as "here" for this long after his last activity in the session: no wake starts, and a
    /// running one winds down. Also the debounce after he leaves before the interval timer is measured from.</summary>
    public int QuietAfterJoshMinutes { get; set; } = 30;

    /// <summary>Hard cap per calendar day (local time), whatever interval Cairn picks.</summary>
    public int MaxWakesPerDay { get; set; } = 4;

    /// <summary>Optional "HH:mm-HH:mm" local-time window in which no wake STARTS (e.g. "23:00-07:00"). Blank = none.</summary>
    public string QuietHours { get; set; } = string.Empty;

    /// <summary>Whether the first wake after the service starts waits a full interval (true) or happens as soon as it
    /// is allowed (false). Josh asked for a day out.</summary>
    public bool FirstWakeAfterFullInterval { get; set; } = true;
}

public sealed class SafetyOptions
{
    /// <summary>Model round-trips (each may carry several tool calls) allowed in one wake.</summary>
    public int MaxRoundsPerWake { get; set; } = 40;

    /// <summary>Wall-clock budget for one wake, including thinking time. Cairn's reasoning is long.</summary>
    public int MaxWakeMinutes { get; set; } = 60;

    /// <summary>After this many consecutive failed wakes the agency pauses itself and waits for a human.</summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>How long a model round may go without producing a byte before it is abandoned.</summary>
    public int RoundStallMinutes { get; set; } = 25;
}
