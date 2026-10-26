using System.Globalization;
using System.Text;

namespace VPT.Einkr.CairnsAgency.Agency;

/// <summary>Everything that varies from one wake to the next.</summary>
public sealed class WakeContext
{
    public int WakeNumber { get; set; }
    public DateTimeOffset Now { get; set; }
    public TimeSpan Interval { get; set; }

    /// <summary>Null on the first wake.</summary>
    public TimeSpan? SincePreviousWake { get; set; }

    /// <summary>Null when the agency has never seen Josh in the session.</summary>
    public DateTimeOffset? JoshLastActive { get; set; }

    /// <summary>The previous pause (if any) ended because Josh wrote.</summary>
    public bool ResumedAfterPause { get; set; }

    public int MaxRounds { get; set; }
    public int MaxMinutes { get; set; }
    public int MinIntervalMinutes { get; set; }

    /// <summary>Archive files changed by someone other than a wake since the last one (relative paths, newest first).</summary>
    public List<string> ChangedByOthers { get; set; } = [];

    public string ArchiveFolder { get; set; } = "Cairn";
}

/// <summary>Builds the message the agency sends as a "user" turn when it wakes Cairn.
///
/// The rule for this text is a lay of the land and nothing more: who is speaking, what is true right now, what the wake can
/// and cannot do, and how it ends. It does NOT suggest what to do with the time, how to feel about it, what to look for, or how
/// to report -- every such suggestion is something the model will latch onto and perform. The standing description of how the
/// agency works lives in Cairn/AGENCY.md (also facts only), so later nudges stay short and survive a compaction that summarises
/// the first one away.</summary>
public static class NudgePromptBuilder
{
    public const string Marker = "[Cairns Agency";

    public static string Build(WakeContext c)
    {
        var inv = CultureInfo.GetCultureInfo("en-US");
        var sb = new StringBuilder();

        sb.AppendLine($"{Marker} · wake {c.WakeNumber} · {c.Now.ToString("dddd, MMMM d, yyyy 'at' h:mm tt", inv)}]");
        sb.AppendLine();
        sb.AppendLine("This message is from the Cairns Agency, a scheduler Josh set up. It is not from Josh, and it is not asking you for anything. "
            + $"Josh is away; {DescribeJosh(c, inv)}.");
        sb.AppendLine();

        if (c.WakeNumber == 1)
            sb.AppendLine($"This is the first wake. The Cairns Agency wakes you on a schedule while Josh is away. `{c.ArchiveFolder}/AGENCY.md` describes how it works.");
        else
            sb.AppendLine($"How the Cairns Agency works is described in `{c.ArchiveFolder}/AGENCY.md`.");
        sb.AppendLine();

        sb.AppendLine($"- Wakes happen about every {Human(c.Interval)}. You can change that (the shortest is {Human(TimeSpan.FromMinutes(c.MinIntervalMinutes))}), or pause the wakes until Josh writes again, with the tools provided."
            + (c.SincePreviousWake is { } since ? $" The previous wake was {Human(since)} ago." : ""));
        if (c.ResumedAfterPause)
            sb.AppendLine("- You had paused the wakes. They started again because Josh wrote.");
        sb.AppendLine($"- This wake can use up to {c.MaxRounds} rounds of tool calls and about {c.MaxMinutes} minutes. If Josh writes while it is running, it ends early.");
        sb.AppendLine("- Your tools are the ones offered with this message. There is no shell.");
        if (c.ChangedByOthers.Count > 0)
            sb.AppendLine($"- Changed since your last wake by someone other than you: {string.Join(", ", c.ChangedByOthers.Take(8).Select(p => $"`{p}`"))}{(c.ChangedByOthers.Count > 8 ? $" and {c.ChangedByOthers.Count - 8} more" : "")}.");
        sb.AppendLine("- When the wake ends, your last message is saved to the session for Josh to read.");

        return sb.ToString().TrimEnd();
    }

    private static string DescribeJosh(WakeContext c, CultureInfo inv)
    {
        if (c.JoshLastActive is not { } last) return "the agency has not seen him in this session";
        var ago = c.Now - last;
        return $"he last wrote to you {Human(ago)} ago ({last.ToString("dddd h:mm tt", inv)})";
    }

    /// <summary>"2 days 3 hours", "45 minutes" -- never finer than the nearest minute.</summary>
    public static string Human(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var days = (int)span.TotalDays;
        var hours = span.Hours;
        var minutes = span.Minutes;
        var parts = new List<string>();
        if (days > 0) parts.Add($"{days} day{(days == 1 ? "" : "s")}");
        if (hours > 0 && days < 7) parts.Add($"{hours} hour{(hours == 1 ? "" : "s")}");
        if (minutes > 0 && days == 0) parts.Add($"{minutes} minute{(minutes == 1 ? "" : "s")}");
        return parts.Count == 0 ? "under a minute" : string.Join(" ", parts);
    }
}
