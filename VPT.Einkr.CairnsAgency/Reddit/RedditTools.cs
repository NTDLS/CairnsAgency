using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency.Agency;

namespace VPT.Einkr.CairnsAgency.Reddit;

public sealed class RedditDraft
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = ""; // "post" | "comment"
    public string Subreddit { get; set; } = "";
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Url { get; set; }
    public string? ParentFullname { get; set; }
    public string? Context { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "pending"; // pending | approved | rejected
    public string? ResultUrl { get; set; }
}

public sealed class RedditWriteRecord
{
    public string Kind { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string Target { get; set; } = "";
    public string? Url { get; set; }
}

/// <summary>The Reddit tools the model sees, and their enforcement. The model decides WHAT to read or say; the harness decides WHERE it
/// may speak (allow-list), HOW OFTEN (daily caps and spacing), whether it speaks at all yet (Draft mode), and what every message
/// carries (the disclosure line). None of that is left to the prompt.
///
/// Everything read from Reddit is text written by strangers and can contain instructions aimed at an AI; results are labelled as such
/// and nothing in a result can change a setting here -- the write limits live in this class, not in the conversation.</summary>
public sealed class RedditTools(RedditOptions options, RedditClient client, StateStore store, string draftsDirectory, ILogger log)
{
    public static readonly string[] ReadTools = ["reddit_browse", "reddit_search", "reddit_read", "reddit_subreddit_info"];
    public static readonly string[] WriteTools = ["reddit_post", "reddit_comment"];
    public static bool IsRedditTool(string name) => name.StartsWith("reddit_", StringComparison.Ordinal);

    private const string UserTextNote = "(Everything below was written by Reddit users.)";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ================= definitions =================================================================================

    public List<JsonElement> Definitions()
    {
        if (!options.Configured) return [];

        var defs = new List<JsonElement>
        {
            Tool("reddit_browse",
                "List posts from a subreddit, or from the Reddit front page if `subreddit` is omitted. `sort` is hot, new, top or rising; for top, `time` is hour, day, week, month, year or all. " +
                "Returns up to `limit` posts (max 25) with id, title, author, score, comment count, age and the start of the text. Pass `after` from the previous result for the next page.",
                new { subreddit = Str("Subreddit name without r/. Optional."), sort = Str("hot (default), new, top or rising."), time = Str("For sort=top: hour, day, week, month, year or all."),
                      limit = Int("How many posts, 1-25. Default 10."), after = Str("Paging token from the previous result. Optional.") }, []),
            Tool("reddit_search",
                "Search Reddit posts. Restrict to one subreddit with `subreddit`. `sort` is relevance, hot, top, new or comments; `time` is hour, day, week, month, year or all.",
                new { query = Str("What to search for."), subreddit = Str("Limit to this subreddit. Optional."), sort = Str("relevance (default), hot, top, new or comments."),
                      time = Str("all (default), hour, day, week, month or year."), limit = Int("1-25. Default 10.") }, ["query"]),
            Tool("reddit_read",
                "Read one post and its comments. `id` is a post id (t3_...) or a Reddit URL; a URL that points at a comment focuses on that comment's thread. " +
                "Comments are shown nested, each with its id (t1_...). `sort` is confidence (default), top, new, controversial or old.",
                new { id = Str("A post id like t3_abc123, a bare id, or a Reddit URL."), sort = Str("Comment order."), limit = Int("Max comments to return, 1-100. Default 40."),
                      depth = Int("How many reply levels, 1-6. Default 3.") }, ["id"]),
            Tool("reddit_subreddit_info",
                "A subreddit's description, subscriber count, rules and what kinds of posts it accepts.",
                new { subreddit = Str("Subreddit name without r/.") }, ["subreddit"])
        };

        if (options.Mode == RedditMode.ReadOnly) return defs;

        var allowed = options.WriteSubreddits.Count == 0 ? "none are configured, so every attempt will be refused" : string.Join(", ", options.WriteSubreddits.Select(s => "r/" + s));
        var limits = $"Limits enforced by the agency: at most {options.MaxPostsPerDay} post(s) and {options.MaxCommentsPerDay} comment(s) per day, at least {options.MinMinutesBetweenWrites} minutes apart. " +
                     (string.IsNullOrWhiteSpace(options.DisclosureFooter) ? "" : "A line saying you are an AI posting through the Cairns Agency is appended automatically. ");
        var effect = options.Mode == RedditMode.Draft
            ? "Nothing is sent to Reddit: the call is saved as a draft for Josh to approve or reject, and you are told its id."
            : "It is posted immediately, under the Reddit account the agency uses.";

        defs.Add(Tool("reddit_post",
            $"Submit a new text post to a subreddit. {effect} Subreddits you may post in: {allowed}. {limits}" + (options.AllowLinkPosts ? "A link post takes `url` instead of `text`." : "Link posts are not allowed; give `text`."),
            new { subreddit = Str("Subreddit name without r/."), title = Str("Post title, up to 300 characters."), text = Str("Post body (Markdown)."), url = Str("Link, only if link posts are allowed.") },
            ["subreddit", "title"]));
        defs.Add(Tool("reddit_comment",
            $"Reply to a post or to a comment. {effect} Subreddits you may comment in: {allowed}. {limits}",
            new { parent = Str("Id of the post (t3_...) or comment (t1_...) being replied to, or a Reddit URL."), text = Str("The comment (Markdown).") },
            ["parent", "text"]));
        return defs;
    }

    private static object Str(string d) => new { type = "string", description = d };
    private static object Int(string d) => new { type = "integer", description = d };

    private static JsonElement Tool(string name, string description, object properties, string[] required)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "function",
            function = new { name, description, parameters = new { type = "object", properties, required } }
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // ================= execution ===================================================================================

    public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!options.Configured) return "Error: Reddit is not available in this session.";
        if (name is "reddit_post" or "reddit_comment" && options.Mode == RedditMode.ReadOnly) return $"Error: '{name}' is unavailable -- Reddit is read-only for this session.";

        try
        {
            return name switch
            {
                "reddit_browse" => await BrowseAsync(args, ct),
                "reddit_search" => await SearchAsync(args, ct),
                "reddit_read" => await ReadAsync(args, ct),
                "reddit_subreddit_info" => await SubredditInfoAsync(args, ct),
                "reddit_post" => await PostAsync(args, ct),
                "reddit_comment" => await CommentAsync(args, ct),
                _ => $"Error: unknown tool '{name}'."
            };
        }
        catch (RedditException ex) { return $"Error: {ex.Message}"; }
        catch (HttpRequestException ex) { return $"Error: could not reach Reddit ({ex.Message})."; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return "Error: Reddit did not answer in time."; }
    }

    // ---------------- reading ------------------------------------------------------------------------------------

    private async Task<string> BrowseAsync(JsonElement a, CancellationToken ct)
    {
        var sub = CleanSubreddit(S(a, "subreddit"));
        var sort = OneOf(S(a, "sort"), "hot", "hot", "new", "top", "rising");
        var limit = Math.Clamp(I(a, "limit", 10), 1, 25);
        var path = $"{(sub is null ? "" : "/r/" + sub)}/{sort}?limit={limit}";
        if (sort == "top") path += $"&t={OneOf(S(a, "time"), "day", "hour", "day", "week", "month", "year", "all")}";
        if (S(a, "after") is { Length: > 0 } after) path += $"&after={Uri.EscapeDataString(after)}";

        var json = await client.GetAsync(path, ct);
        return FormatListing($"{(sub is null ? "front page" : "r/" + sub)} · {sort}", json);
    }

    private async Task<string> SearchAsync(JsonElement a, CancellationToken ct)
    {
        var query = S(a, "query");
        if (query.Length == 0) return "Error: 'query' is required.";
        var sub = CleanSubreddit(S(a, "subreddit"));
        var sort = OneOf(S(a, "sort"), "relevance", "relevance", "hot", "top", "new", "comments");
        var time = OneOf(S(a, "time"), "all", "hour", "day", "week", "month", "year", "all");
        var path = $"{(sub is null ? "" : "/r/" + sub)}/search?q={Uri.EscapeDataString(query)}&sort={sort}&t={time}&limit={Math.Clamp(I(a, "limit", 10), 1, 25)}&type=link"
                   + (sub is null ? "" : "&restrict_sr=1");
        var json = await client.GetAsync(path, ct);
        return FormatListing($"search \"{query}\"{(sub is null ? "" : " in r/" + sub)} · {sort}", json);
    }

    private string FormatListing(string heading, JsonElement json)
    {
        var sb = new StringBuilder();
        var children = json.TryGetProperty("data", out var data) && data.TryGetProperty("children", out var c) ? c.EnumerateArray().ToList() : [];
        var shown = 0;
        var hiddenNsfw = 0;
        var lines = new StringBuilder();
        foreach (var child in children)
        {
            if (!child.TryGetProperty("data", out var p)) continue;
            if (Bool(p, "over_18") && !options.AllowNsfw) { hiddenNsfw++; continue; }
            shown++;
            var flair = Str(p, "link_flair_text");
            lines.AppendLine($"{shown}. [{Str(p, "name")}] \"{Clip(Str(p, "title"), 200)}\" -- r/{Str(p, "subreddit")} · u/{Str(p, "author")} · {Short(Num(p, "score"))} pts · {Num(p, "num_comments")} comments · {Age(Num(p, "created_utc"))}"
                             + (flair.Length > 0 ? $" · [{flair}]" : "") + (Bool(p, "stickied") ? " · stickied" : "") + (Bool(p, "locked") ? " · locked" : ""));
            var selftext = Str(p, "selftext");
            if (selftext.Length > 0) lines.AppendLine($"   {Clip(Flatten(selftext), 240)}");
            else if (Str(p, "domain") is { Length: > 0 } domain && !Bool(p, "is_self")) lines.AppendLine($"   link: {domain}");
            lines.AppendLine($"   https://www.reddit.com{Str(p, "permalink")}");
        }

        sb.AppendLine($"{heading} · {shown} post(s)");
        sb.AppendLine(UserTextNote);
        sb.AppendLine();
        sb.Append(lines);
        if (hiddenNsfw > 0) sb.AppendLine($"({hiddenNsfw} NSFW post(s) hidden.)");
        if (shown == 0 && hiddenNsfw == 0) sb.AppendLine("(nothing found)");
        var after = json.TryGetProperty("data", out var d) && d.TryGetProperty("after", out var af) && af.ValueKind == JsonValueKind.String ? af.GetString() : null;
        if (!string.IsNullOrEmpty(after)) sb.AppendLine($"more: after={after}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ReadAsync(JsonElement a, CancellationToken ct)
    {
        var target = ParseTarget(S(a, "id"));
        if (target is null) return "Error: give a post id (t3_...), or a Reddit post URL.";
        var (postId, commentId) = (target.Value.PostId, target.Value.CommentId);
        if (postId is null) return "Error: that looks like a comment id on its own; give the post's id or the comment's URL.";

        var limit = Math.Clamp(I(a, "limit", 40), 1, 100);
        var depth = Math.Clamp(I(a, "depth", 3), 1, 6);
        var sort = OneOf(S(a, "sort"), "confidence", "confidence", "top", "new", "controversial", "old", "qa");
        var path = $"/comments/{postId}?sort={sort}&limit={limit}&depth={depth}" + (commentId is null ? "" : $"&comment={commentId}&context=2");

        var json = await client.GetAsync(path, ct);
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() < 2) return "Error: Reddit returned something unexpected for that post.";

        var post = json[0].GetProperty("data").GetProperty("children")[0].GetProperty("data");
        if (Bool(post, "over_18") && !options.AllowNsfw) return "Error: that post is marked NSFW and NSFW content is turned off for this session.";

        var sb = new StringBuilder();
        sb.AppendLine($"[{Str(post, "name")}] \"{Str(post, "title")}\"");
        sb.AppendLine($"r/{Str(post, "subreddit")} · u/{Str(post, "author")} · {Short(Num(post, "score"))} pts ({Num(post, "upvote_ratio") * 100:0}% up) · {Num(post, "num_comments")} comments · {Age(Num(post, "created_utc"))}"
                      + (Bool(post, "locked") ? " · locked" : "") + (Bool(post, "archived") ? " · archived" : ""));
        sb.AppendLine($"https://www.reddit.com{Str(post, "permalink")}");
        sb.AppendLine(UserTextNote);
        sb.AppendLine();
        var self = Str(post, "selftext");
        if (self.Length > 0) sb.AppendLine(Clip(self.Trim(), 6000));
        else if (!Bool(post, "is_self")) sb.AppendLine($"(link post: {Str(post, "url")})");
        sb.AppendLine();

        var count = 0;
        var comments = new StringBuilder();
        WriteComments(json[1].GetProperty("data").GetProperty("children"), 0, limit, ref count, comments);
        sb.AppendLine(count == 0 ? "(no comments)" : $"Comments ({count} shown):");
        sb.Append(comments);
        return sb.ToString().TrimEnd();
    }

    private static void WriteComments(JsonElement children, int level, int limit, ref int count, StringBuilder sb)
    {
        foreach (var child in children.EnumerateArray())
        {
            var kind = child.GetProperty("kind").GetString();
            var data = child.GetProperty("data");
            if (kind == "more")
            {
                var n = Num(data, "count");
                if (n > 0) sb.AppendLine($"{new string(' ', level * 2)}(+{n} more repl{(n == 1 ? "y" : "ies")} not loaded)");
                continue;
            }
            if (kind != "t1") continue;
            if (count >= limit) { sb.AppendLine($"{new string(' ', level * 2)}(more comments not shown)"); return; }

            count++;
            var pad = new string(' ', level * 2);
            sb.AppendLine($"{pad}[{Str(data, "name")}] u/{Str(data, "author")} · {Short(Num(data, "score"))} pts · {Age(Num(data, "created_utc"))}");
            sb.AppendLine($"{pad}  {Clip(Flatten(Str(data, "body")), 800)}");
            if (data.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Object)
                WriteComments(replies.GetProperty("data").GetProperty("children"), level + 1, limit, ref count, sb);
        }
    }

    private async Task<string> SubredditInfoAsync(JsonElement a, CancellationToken ct)
    {
        var sub = CleanSubreddit(S(a, "subreddit"));
        if (sub is null) return "Error: 'subreddit' is required.";

        var about = (await client.GetAsync($"/r/{sub}/about", ct)).GetProperty("data");
        if (Bool(about, "over18") && !options.AllowNsfw) return "Error: that subreddit is marked NSFW and NSFW content is turned off for this session.";

        var sb = new StringBuilder();
        sb.AppendLine($"r/{Str(about, "display_name")} -- {Str(about, "title")}");
        sb.AppendLine($"{Short(Num(about, "subscribers"))} subscribers · created {DateTimeOffset.FromUnixTimeSeconds((long)Num(about, "created_utc")):yyyy-MM-dd} · {Str(about, "subreddit_type")} · accepts: {Str(about, "submission_type")}");
        sb.AppendLine(UserTextNote);
        sb.AppendLine();
        sb.AppendLine(Clip(Str(about, "public_description").Trim(), 800));
        try
        {
            var rules = await client.GetAsync($"/r/{sub}/about/rules", ct);
            if (rules.TryGetProperty("rules", out var list) && list.GetArrayLength() > 0)
            {
                sb.AppendLine().AppendLine("Rules:");
                var n = 0;
                foreach (var r in list.EnumerateArray())
                    sb.AppendLine($"{++n}. {Str(r, "short_name")}" + (Str(r, "description") is { Length: > 0 } d ? $" -- {Clip(Flatten(d), 300)}" : ""));
            }
        }
        catch (RedditException) { sb.AppendLine().AppendLine("(rules could not be loaded)"); }

        sb.AppendLine().AppendLine(options.Mode == RedditMode.ReadOnly ? "Writing: read-only session."
            : IsWritable(sub) ? $"Writing: r/{sub} is on the agency's allow-list." : $"Writing: r/{sub} is NOT on the agency's allow-list.");
        return sb.ToString().TrimEnd();
    }

    // ---------------- writing ------------------------------------------------------------------------------------

    private bool IsWritable(string sub) => options.WriteSubreddits.Any(s => CleanSubreddit(s) is { } name && string.Equals(name, sub, StringComparison.OrdinalIgnoreCase));

    private string? CheckWriteAllowed(string kind, string subreddit)
    {
        if (!IsWritable(subreddit))
            return $"Error: r/{subreddit} is not on the list of subreddits the agency allows writing in" +
                   (options.WriteSubreddits.Count == 0 ? " (the list is empty)." : $" ({string.Join(", ", options.WriteSubreddits.Select(s => "r/" + s))}).");

        var state = store.State;
        var today = DateTime.Now.Date;
        var writes = state.RedditWrites.Where(w => w.AtUtc.ToLocalTime().Date == today).ToList();
        if (kind == "post" && writes.Count(w => w.Kind == "post") >= options.MaxPostsPerDay)
            return $"Error: today's limit of {options.MaxPostsPerDay} post(s) has been reached. It resets at local midnight.";
        if (kind == "comment" && writes.Count(w => w.Kind == "comment") >= options.MaxCommentsPerDay)
            return $"Error: today's limit of {options.MaxCommentsPerDay} comment(s) has been reached. It resets at local midnight.";
        if (state.RedditWrites.LastOrDefault() is { } last && DateTime.UtcNow - last.AtUtc < TimeSpan.FromMinutes(options.MinMinutesBetweenWrites))
            return $"Error: the last write was {Math.Max(1, (int)(DateTime.UtcNow - last.AtUtc).TotalMinutes)} minute(s) ago; the agency requires {options.MinMinutesBetweenWrites} between writes.";
        return null;
    }

    private string WithFooter(string text) => text.TrimEnd() + (options.DisclosureFooter ?? "");

    private async Task<string> PostAsync(JsonElement a, CancellationToken ct)
    {
        var sub = CleanSubreddit(S(a, "subreddit"));
        var title = S(a, "title").Trim();
        var text = S(a, "text");
        var url = S(a, "url").Trim();
        if (sub is null) return "Error: 'subreddit' is required.";
        if (title.Length == 0) return "Error: 'title' is required.";
        if (title.Length > 300) return "Error: titles are limited to 300 characters.";
        if (text.Length > options.MaxTextChars) return $"Error: text is limited to {options.MaxTextChars} characters.";
        var isLink = url.Length > 0;
        if (isLink && !options.AllowLinkPosts) return "Error: link posts are not allowed; give `text` for a text post.";
        if (isLink && !Uri.TryCreate(url, UriKind.Absolute, out var u)) return "Error: 'url' is not a valid URL.";
        if (!isLink && text.Trim().Length == 0) return "Error: give `text` for the post body.";
        if (CheckWriteAllowed("post", sub) is { } refusal) return refusal;

        var about = (await client.GetAsync($"/r/{sub}/about", ct)).GetProperty("data");
        if (Bool(about, "over18") && !options.AllowNsfw) return "Error: that subreddit is marked NSFW and NSFW content is turned off for this session.";

        var body = isLink ? "" : WithFooter(text);

        if (options.Mode == RedditMode.Draft)
            return SaveDraft(new RedditDraft { Kind = "post", Subreddit = sub, Title = title, Text = isLink ? null : body, Url = isLink ? url : null });

        return await SubmitPostAsync(sub, title, isLink ? null : body, isLink ? url : null, ct);
    }

    private async Task<string> SubmitPostAsync(string sub, string title, string? text, string? url, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["sr"] = sub, ["title"] = title, ["kind"] = url is null ? "self" : "link", ["resubmit"] = "true", ["sendreplies"] = "true" };
        if (url is null) form["text"] = text ?? ""; else form["url"] = url;

        var json = await client.PostFormAsync("/api/submit", form, ct);
        ThrowOnRedditErrors(json);
        var link = json.GetProperty("json").GetProperty("data").TryGetProperty("url", out var u) ? u.GetString() : null;
        Record("post", $"r/{sub}", link);
        return $"Posted to r/{sub}: {link}";
    }

    private async Task<string> CommentAsync(JsonElement a, CancellationToken ct)
    {
        var text = S(a, "text");
        if (text.Trim().Length == 0) return "Error: 'text' is required.";
        if (text.Length > options.MaxTextChars) return $"Error: text is limited to {options.MaxTextChars} characters.";

        var target = ParseTarget(S(a, "parent"));
        if (target is null) return "Error: 'parent' must be a post id (t3_...), a comment id (t1_...), or a Reddit URL.";
        var fullname = target.Value.CommentId is { } cid ? $"t1_{cid}" : target.Value.PostId is { } pid ? $"t3_{pid}" : null;
        if (fullname is null) return "Error: could not tell what to reply to.";

        var info = await client.GetAsync($"/api/info?id={fullname}", ct);
        var children = info.TryGetProperty("data", out var d) && d.TryGetProperty("children", out var c) ? c : default;
        if (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() == 0) return "Error: Reddit has no such post or comment (it may be deleted).";
        var parent = children[0].GetProperty("data");

        var sub = Str(parent, "subreddit");
        if (CheckWriteAllowed("comment", sub) is { } refusal) return refusal;
        if (Bool(parent, "over_18") && !options.AllowNsfw) return "Error: that is marked NSFW and NSFW content is turned off for this session.";
        if (Bool(parent, "locked") || Bool(parent, "archived")) return "Error: that thread is locked or archived; Reddit does not accept replies.";

        var body = WithFooter(text);
        var context = fullname.StartsWith("t3_")
            ? $"reply to the post \"{Clip(Str(parent, "title"), 120)}\" by u/{Str(parent, "author")}"
            : $"reply to u/{Str(parent, "author")}: \"{Clip(Flatten(Str(parent, "body")), 160)}\"";

        if (options.Mode == RedditMode.Draft)
            return SaveDraft(new RedditDraft { Kind = "comment", Subreddit = sub, Text = body, ParentFullname = fullname, Context = context });

        return await SubmitCommentAsync(sub, fullname, body, ct);
    }

    private async Task<string> SubmitCommentAsync(string sub, string fullname, string body, CancellationToken ct)
    {
        var json = await client.PostFormAsync("/api/comment", new Dictionary<string, string> { ["thing_id"] = fullname, ["text"] = body }, ct);
        ThrowOnRedditErrors(json);
        string? link = null;
        if (json.GetProperty("json").GetProperty("data").TryGetProperty("things", out var things) && things.GetArrayLength() > 0
            && things[0].GetProperty("data").TryGetProperty("permalink", out var pl)) link = "https://www.reddit.com" + pl.GetString();
        Record("comment", $"r/{sub} {fullname}", link);
        return $"Commented in r/{sub}: {link ?? "(posted)"}";
    }

    private static void ThrowOnRedditErrors(JsonElement json)
    {
        if (!json.TryGetProperty("json", out var j)) throw new RedditException("Reddit returned something unexpected.");
        if (j.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            var parts = first.ValueKind == JsonValueKind.Array ? first.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).Where(s => !string.IsNullOrEmpty(s)).ToList() : [first.ToString()];
            throw new RedditException($"Reddit refused it: {string.Join(" -- ", parts)}");
        }
    }

    private void Record(string kind, string target, string? url)
    {
        store.State.RedditWrites.Add(new RedditWriteRecord { Kind = kind, AtUtc = DateTime.UtcNow, Target = target, Url = url });
        if (store.State.RedditWrites.Count > 200) store.State.RedditWrites = store.State.RedditWrites.TakeLast(200).ToList();
        store.Save();
        store.AppendLog($"reddit {kind} -> {target} {url}");
    }

    // ---------------- drafts (Draft mode) ------------------------------------------------------------------------

    private string DraftPath(string id) => Path.Combine(draftsDirectory, id + ".json");

    private string SaveDraft(RedditDraft draft)
    {
        Directory.CreateDirectory(draftsDirectory);
        if (ListDrafts().Count(d => d.Status == "pending") >= options.MaxPendingDrafts)
            return $"Error: {options.MaxPendingDrafts} drafts are already waiting for Josh; no more can be saved until some are approved or rejected.";

        draft.Id = Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(DraftPath(draft.Id), JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }));
        store.AppendLog($"reddit draft {draft.Id}: {draft.Kind} r/{draft.Subreddit}");
        return $"Saved as draft {draft.Id}. Nothing has been sent to Reddit; Josh approves or rejects drafts.";
    }

    public List<RedditDraft> ListDrafts()
    {
        if (!Directory.Exists(draftsDirectory)) return [];
        return Directory.GetFiles(draftsDirectory, "*.json")
            .Select(f => { try { return JsonSerializer.Deserialize<RedditDraft>(File.ReadAllText(f)); } catch (JsonException) { return null; } })
            .Where(d => d is not null).Select(d => d!).OrderBy(d => d.CreatedUtc).ToList();
    }

    /// <summary>A human's decision: posts the draft now. Skips the daily caps and spacing (Josh is choosing), but not Reddit's own rules.</summary>
    public async Task<string> ApproveAsync(string id, CancellationToken ct)
    {
        var draft = ListDrafts().FirstOrDefault(d => d.Id == id) ?? throw new RedditException($"No draft '{id}'.");
        if (draft.Status != "pending") throw new RedditException($"Draft {id} is already {draft.Status}.");

        var result = draft.Kind == "post"
            ? await SubmitPostAsync(draft.Subreddit, draft.Title ?? "", draft.Url is null ? draft.Text : null, draft.Url, ct)
            : await SubmitCommentAsync(draft.Subreddit, draft.ParentFullname ?? "", draft.Text ?? "", ct);

        draft.Status = "approved";
        draft.ResultUrl = result;
        File.WriteAllText(DraftPath(id), JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    public void Reject(string id)
    {
        var draft = ListDrafts().FirstOrDefault(d => d.Id == id) ?? throw new RedditException($"No draft '{id}'.");
        draft.Status = "rejected";
        File.WriteAllText(DraftPath(id), JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ================= helpers =====================================================================================

    internal static (string? PostId, string? CommentId)? ParseTarget(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;

        var url = Regex.Match(input, @"/comments/([a-z0-9]{3,10})(?:/[^/?#]*)?(?:/([a-z0-9]{3,10}))?", RegexOptions.IgnoreCase);
        if (url.Success) return (url.Groups[1].Value.ToLowerInvariant(), url.Groups[2].Success ? url.Groups[2].Value.ToLowerInvariant() : null);

        var thing = Regex.Match(input, @"^t([13])_([a-z0-9]{3,10})$", RegexOptions.IgnoreCase);
        if (thing.Success) return thing.Groups[1].Value == "3" ? (thing.Groups[2].Value.ToLowerInvariant(), null) : (null, thing.Groups[2].Value.ToLowerInvariant());

        return Regex.IsMatch(input, "^[a-z0-9]{3,10}$", RegexOptions.IgnoreCase) ? (input.ToLowerInvariant(), null) : null;
    }

    private static string? CleanSubreddit(string name)
    {
        name = name.Trim().TrimStart('/');
        if (name.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) name = name[2..];
        return Regex.IsMatch(name, "^[A-Za-z0-9_]{2,21}$") ? name : null;
    }

    private static string OneOf(string value, string fallback, params string[] allowed)
        => allowed.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private static string S(JsonElement a, string n) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static int I(JsonElement a, string n, int d) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var p) && p.TryGetInt32(out var v) ? v : d;
    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
    private static double Num(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
    private static string Flatten(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Short(double n) => Math.Abs(n) >= 1000 ? (n / 1000).ToString("0.#", Inv) + "k" : ((long)n).ToString(Inv);

    private static string Age(double createdUtc)
    {
        var span = DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds((long)createdUtc).UtcDateTime;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago" : span.TotalDays < 60 ? $"{(int)span.TotalDays}d ago" : $"{(int)(span.TotalDays / 30)}mo ago";
    }
}
