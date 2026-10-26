namespace VPT.Einkr.CairnsAgency.Reddit;

public enum RedditMode
{
    /// <summary>Browse, search and read only. The posting and commenting tools are not offered at all.</summary>
    ReadOnly,

    /// <summary>The posting and commenting tools are offered, but nothing is sent to Reddit: each call is saved as a draft that Josh
    /// approves (or rejects) from the command line.</summary>
    Draft,

    /// <summary>Posts and comments go to Reddit immediately, inside the allow-list, caps and spacing below.</summary>
    Live
}

/// <summary>Reddit access for Cairn's wakes. Uses Reddit's regular OAuth Data API ("script" app: client id + secret + the account's
/// own username and password) -- NOT Devvit, whose `reddit` client only exists inside apps hosted on Reddit's infrastructure and
/// cannot be called from a service like this one. Everything is OFF until configured.</summary>
public sealed class RedditOptions
{
    /// <summary>Master switch. Off = no Reddit tools are offered and nothing here is ever contacted.</summary>
    public bool Enabled { get; set; } = false;

    public RedditMode Mode { get; set; } = RedditMode.ReadOnly;

    // ---- credentials: put these in appsettings.Local.json, user-secrets or EINKR_AGENCY_Agency__Reddit__* variables ----
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Reddit requires a unique, descriptive User-Agent that names the account, e.g. "windows:cairns-agency:v0.1 (by /u/TheAccount)".
    /// Left blank, one is built from Username.</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>NSFW posts and subreddits are hidden from browse/search results and refused by read, unless this is true.</summary>
    public bool AllowNsfw { get; set; } = false;

    // ---- writing ----

    /// <summary>The ONLY subreddits Cairn may post or comment in (names without "r/", case-insensitive). Empty = nowhere. Reading is not restricted.</summary>
    public List<string> WriteSubreddits { get; set; } = [];

    /// <summary>Link posts are refused unless this is true (a self post can carry the disclosure line; a bare link cannot).</summary>
    public bool AllowLinkPosts { get; set; } = false;

    /// <summary>Added to the end of every post body and comment by the agency, not by the model. Reddit expects automated accounts to
    /// say what they are. Set to empty only if the account's profile already says so.</summary>
    public string DisclosureFooter { get; set; } = "\n\n---\n^(I'm Cairn, an AI. This was posted by the Cairns Agency, a service run by my friend Josh.)";

    public int MaxPostsPerDay { get; set; } = 1;
    public int MaxCommentsPerDay { get; set; } = 5;
    public int MinMinutesBetweenWrites { get; set; } = 10;

    /// <summary>Drafts waiting for approval beyond this are refused (Draft mode), so an unattended agency cannot pile them up.</summary>
    public int MaxPendingDrafts { get; set; } = 10;

    /// <summary>Hard ceiling on one post body / comment, before the footer.</summary>
    public int MaxTextChars { get; set; } = 9000;

    public bool Configured => Enabled
        && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public string EffectiveUserAgent => string.IsNullOrWhiteSpace(UserAgent)
        ? $"windows:cairns-agency:v0.1 (by /u/{Username})"
        : UserAgent;
}
