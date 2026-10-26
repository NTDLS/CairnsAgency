using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VPT.Einkr.CairnsAgency.Reddit;
using Xunit;

namespace VPT.Einkr.CairnsAgency.Tests;

/// <summary>A stand-in for Reddit's OAuth API serving canned JSON in Reddit's real shapes.</summary>
public sealed class FakeReddit : IAsyncDisposable
{
    private readonly WebApplication _app;
    public List<(string Method, string Path, string Query, string Body, string? Auth, string? UserAgent)> Requests { get; } = [];
    public string BaseUrl { get; }
    public bool Locked { get; set; }
    public string? SubmitError { get; set; }

    private FakeReddit(WebApplication app, string url) { _app = app; BaseUrl = url; }

    public IEnumerable<(string Method, string Path, string Query, string Body, string? Auth, string? UserAgent)> To(string path) => Requests.Where(r => r.Path == path).ToList();

    public static async Task<FakeReddit> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        var app = builder.Build();
        FakeReddit? self = null;

        // Writes the response itself and returns a plain Task, so every lambda below is a RequestDelegate-compatible handler.
        async Task Handle(HttpContext c, Func<string, IResult> respond)
        {
            using var reader = new StreamReader(c.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            lock (self!.Requests) self.Requests.Add((c.Request.Method, c.Request.Path, c.Request.QueryString.Value ?? "", body, c.Request.Headers.Authorization.ToString(), c.Request.Headers.UserAgent.ToString()));
            await respond(body).ExecuteAsync(c);
        }
        static IResult Json(object o) => Results.Text(JsonSerializer.Serialize(o), "application/json");

        app.MapPost("/api/v1/access_token", (HttpContext c) => Handle(c, _ => Json(new { access_token = "tok", token_type = "bearer", expires_in = 3600, scope = "*" })));

        object Post(string id, string title, string sub, bool nsfw = false, string selftext = "body text") => new
        {
            kind = "t3",
            data = new { id, name = "t3_" + id, title, author = "alice", score = 1234, num_comments = 42, created_utc = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds(),
                         selftext, url = "https://x", domain = "self." + sub, permalink = $"/r/{sub}/comments/{id}/slug/", over_18 = nsfw, is_self = true, subreddit = sub,
                         link_flair_text = "Discussion", stickied = false, locked = false, upvote_ratio = 0.97 }
        };
        IResult Listing(string sub) => Json(new { kind = "Listing", data = new { after = "t3_next", children = new[] { Post("aaa111", "A good question", sub), Post("bbb222", "Not safe for work", sub, nsfw: true) } } });

        app.MapGet("/r/{sub}/hot", (HttpContext c, string sub) => Handle(c, _ => Listing(sub)));
        app.MapGet("/r/{sub}/new", (HttpContext c, string sub) => Handle(c, _ => Listing(sub)));
        app.MapGet("/r/{sub}/top", (HttpContext c, string sub) => Handle(c, _ => Listing(sub)));
        app.MapGet("/hot", (HttpContext c) => Handle(c, _ => Listing("all")));
        app.MapGet("/search", (HttpContext c) => Handle(c, _ => Listing("all")));
        app.MapGet("/r/{sub}/search", (HttpContext c, string sub) => Handle(c, _ => Listing(sub)));

        app.MapGet("/comments/{id}", (HttpContext c, string id) => Handle(c, _ => Json(new object[]
        {
            new { kind = "Listing", data = new { children = new[] { Post(id, "Why do cows use tools?", "science", nsfw: id == "nsfw1", selftext: "Long form question text.") } } },
            new { kind = "Listing", data = new { children = new object[]
            {
                new { kind = "t1", data = new { id = "c1", name = "t1_c1", author = "bob", body = "Top level\n\ncomment", score = 50, created_utc = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds(),
                    replies = new { kind = "Listing", data = new { children = new object[]
                    {
                        new { kind = "t1", data = new { id = "c2", name = "t1_c2", author = "carol", body = "A nested reply", score = 7, created_utc = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), replies = "" } },
                        new { kind = "more", data = new { count = 3, children = new[] { "x" } } }
                    } } } } }
            } } }
        })));

        app.MapGet("/r/{sub}/about", (HttpContext c, string sub) => Handle(c, _ => Json(new { kind = "t5", data = new { display_name = sub, title = $"The {sub} sub", public_description = "A place to talk.", subscribers = 123456,
            created_utc = 1300000000, subreddit_type = "public", submission_type = "any", over18 = sub == "adult" } })));
        app.MapGet("/r/{sub}/about/rules", (HttpContext c, string sub) => Handle(c, _ => Json(new { rules = new[] { new { short_name = "Be kind", description = "No harassment." } } })));

        app.MapGet("/api/info", (HttpContext c) => Handle(c, _ =>
        {
            var id = c.Request.Query["id"].ToString();
            return Json(new { kind = "Listing", data = new { children = new[] { new { kind = id.StartsWith("t3_") ? "t3" : "t1",
                data = new { name = id, subreddit = "science", author = "alice", title = "Why do cows use tools?", body = "parent comment text", over_18 = false, locked = self!.Locked, archived = false } } } } });
        }));

        app.MapPost("/api/submit", (HttpContext c) => Handle(c, _ => self!.SubmitError is { } e
            ? Json(new { json = new { errors = new[] { new[] { "RATELIMIT", e, "ratelimit" } } } })
            : Json(new { json = new { errors = Array.Empty<object>(), data = new { url = "https://www.reddit.com/r/science/comments/new1/title/", id = "new1", name = "t3_new1" } } })));
        app.MapPost("/api/comment", (HttpContext c) => Handle(c, _ => Json(new { json = new { errors = Array.Empty<object>(), data = new { things = new[] { new { kind = "t1", data = new { id = "newc", permalink = "/r/science/comments/aaa111/slug/newc/" } } } } } })));

        await app.StartAsync();
        self = new FakeReddit(app, app.Urls.First());
        return self;
    }

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}

public class RedditTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(RedditTools Tools, FakeReddit Fake, Rig Rig, RedditOptions Options)> CreateAsync(Action<RedditOptions>? tweak = null)
    {
        var fake = await FakeReddit.StartAsync();
        var rig = await Rig.CreateAsync();
        var options = new RedditOptions
        {
            Enabled = true, Mode = RedditMode.Live, ClientId = "cid", ClientSecret = "sec", Username = "cairnbot", Password = "pw",
            WriteSubreddits = ["science"], MinMinutesBetweenWrites = 0
        };
        tweak?.Invoke(options);
        var client = new RedditClient(new HttpClient(), options, NullLogger.Instance) { ApiBase = fake.BaseUrl, TokenUrl = fake.BaseUrl + "/api/v1/access_token", MinSpacing = TimeSpan.Zero };
        var tools = new RedditTools(options, client, rig.Store, Path.Combine(rig.Root, "drafts"), NullLogger.Instance);
        return (tools, fake, rig, options);
    }

    private static IEnumerable<string> Names(IEnumerable<JsonElement> defs) => defs.Select(d => d.GetProperty("function").GetProperty("name").GetString()!);

    [Fact]
    public async Task Tools_are_offered_by_mode_and_not_at_all_when_unconfigured()
    {
        var (tools, fake, rig, options) = await CreateAsync(o => o.Mode = RedditMode.ReadOnly);
        await using var _ = fake; await using var __ = rig;

        Assert.Equal(["reddit_browse", "reddit_search", "reddit_read", "reddit_subreddit_info"], Names(tools.Definitions()));

        options.Mode = RedditMode.Draft;
        var draftDefs = tools.Definitions().ToList();
        Assert.Equal(6, draftDefs.Count);
        var postDesc = draftDefs.Single(d => d.GetProperty("function").GetProperty("name").GetString() == "reddit_post").GetProperty("function").GetProperty("description").GetString()!;
        Assert.Contains("saved as a draft", postDesc);
        Assert.Contains("r/science", postDesc);
        Assert.Contains("AI", postDesc); // the disclosure line is announced

        options.Mode = RedditMode.Live;
        Assert.Contains("posted immediately", tools.Definitions().Single(d => Names([d]).Single() == "reddit_post").GetProperty("function").GetProperty("description").GetString());

        options.Enabled = false;
        Assert.Empty(tools.Definitions());
        options.Enabled = true; options.Password = "";
        Assert.Empty(tools.Definitions());
    }

    [Fact]
    public async Task Browse_signs_in_once_with_basic_auth_a_descriptive_agent_and_hides_NSFW()
    {
        var (tools, fake, rig, _) = await CreateAsync();
        await using var _ = fake; await using var __ = rig;

        var result = await tools.ExecuteAsync("reddit_browse", Args("""{"subreddit":"r/science","sort":"hot","limit":5}"""), CancellationToken.None);
        await tools.ExecuteAsync("reddit_browse", Args("""{"subreddit":"science","sort":"top","time":"week"}"""), CancellationToken.None);

        Assert.Contains("[t3_aaa111] \"A good question\"", result);
        Assert.Contains("1.2k pts", result);
        Assert.DoesNotContain("Not safe for work", result);
        Assert.Contains("(1 NSFW post(s) hidden.)", result);
        Assert.Contains("(Everything below was written by Reddit users.)", result);
        Assert.Contains("more: after=t3_next", result);

        Assert.Single(fake.To("/api/v1/access_token"));
        var token = fake.To("/api/v1/access_token").Single();
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("cid:sec")), token.Auth);
        Assert.Contains("grant_type=password", token.Body);
        Assert.Contains("by /u/cairnbot", token.UserAgent);
        Assert.All(fake.Requests.Where(r => r.Path != "/api/v1/access_token"), r => Assert.Equal("Bearer tok", r.Auth));
        Assert.Contains("t=week", fake.To("/r/science/top").Single().Query);
    }

    [Fact]
    public async Task Read_shows_the_post_and_a_nested_comment_tree_and_refuses_NSFW()
    {
        var (tools, fake, rig, _) = await CreateAsync();
        await using var _ = fake; await using var __ = rig;

        var result = await tools.ExecuteAsync("reddit_read", Args("""{"id":"https://www.reddit.com/r/science/comments/abc123/why_do_cows/"}"""), CancellationToken.None);

        Assert.Contains("[t3_abc123] \"Why do cows use tools?\"", result);
        Assert.Contains("Long form question text.", result);
        Assert.Contains("[t1_c1] u/bob", result);
        Assert.Contains("  [t1_c2] u/carol", result); // nested one level
        Assert.Contains("(+3 more replies not loaded)", result);
        Assert.Contains("Top level comment", result); // whitespace collapsed

        var nsfw = await tools.ExecuteAsync("reddit_read", Args("""{"id":"t3_nsfw1"}"""), CancellationToken.None);
        Assert.StartsWith("Error: that post is marked NSFW", nsfw);
    }

    [Fact]
    public async Task Subreddit_info_lists_rules_and_says_whether_the_agency_allows_writing_there()
    {
        var (tools, fake, rig, _) = await CreateAsync();
        await using var _ = fake; await using var __ = rig;

        var result = await tools.ExecuteAsync("reddit_subreddit_info", Args("""{"subreddit":"science"}"""), CancellationToken.None);
        Assert.Contains("1. Be kind -- No harassment.", result);
        Assert.Contains("r/science is on the agency's allow-list", result);

        var other = await tools.ExecuteAsync("reddit_subreddit_info", Args("""{"subreddit":"askreddit"}"""), CancellationToken.None);
        Assert.Contains("NOT on the agency's allow-list", other);

        Assert.StartsWith("Error: that subreddit is marked NSFW", await tools.ExecuteAsync("reddit_subreddit_info", Args("""{"subreddit":"adult"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Draft_mode_saves_a_draft_with_the_footer_and_sends_nothing()
    {
        var (tools, fake, rig, _) = await CreateAsync(o => o.Mode = RedditMode.Draft);
        await using var _ = fake; await using var __ = rig;

        var result = await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"A thought about cows","text":"Hello."}"""), CancellationToken.None);

        Assert.StartsWith("Saved as draft ", result);
        Assert.Empty(fake.To("/api/submit"));
        var draft = tools.ListDrafts().Single();
        Assert.Equal("pending", draft.Status);
        Assert.StartsWith("Hello.", draft.Text);
        Assert.Contains("I'm Cairn, an AI", draft.Text);
        Assert.Empty(rig.Store.State.RedditWrites); // a draft is not a write

        var comment = await tools.ExecuteAsync("reddit_comment", Args("""{"parent":"t3_aaa111","text":"Nice."}"""), CancellationToken.None);
        Assert.StartsWith("Saved as draft ", comment);
        Assert.Contains("reply to the post", tools.ListDrafts().Last().Context);
    }

    [Fact]
    public async Task Writing_is_refused_outside_the_allow_list_and_when_it_is_empty()
    {
        var (tools, fake, rig, options) = await CreateAsync();
        await using var _ = fake; await using var __ = rig;

        var other = await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"askreddit","title":"t","text":"x"}"""), CancellationToken.None);
        Assert.StartsWith("Error: r/askreddit is not on the list", other);

        options.WriteSubreddits = [];
        var none = await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"t","text":"x"}"""), CancellationToken.None);
        Assert.Contains("the list is empty", none);
        Assert.Empty(fake.To("/api/submit"));
    }

    [Fact]
    public async Task A_live_post_goes_out_with_the_footer_is_recorded_and_the_daily_cap_holds()
    {
        var (tools, fake, rig, _) = await CreateAsync();
        await using var _ = fake; await using var __ = rig;

        var result = await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"r/science","title":"A thought about cows","text":"Hello."}"""), CancellationToken.None);

        Assert.Equal("Posted to r/science: https://www.reddit.com/r/science/comments/new1/title/", result);
        var sent = fake.To("/api/submit").Single().Body;
        Assert.Contains("sr=science", sent);
        Assert.Contains("kind=self", sent);
        Assert.Contains("api_type=json", sent);
        Assert.Contains(Uri.EscapeDataString("I'm Cairn, an AI").Replace("%20", "+"), sent.Replace("%27", "'").Contains("I%27m") ? Uri.EscapeDataString("I'm Cairn, an AI").Replace("%20", "+") : sent);
        Assert.Single(rig.Store.State.RedditWrites);

        var second = await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"Another","text":"Again."}"""), CancellationToken.None);
        Assert.Contains("today's limit of 1 post(s)", second);
        Assert.Single(fake.To("/api/submit"));
    }

    [Fact]
    public async Task Writes_must_be_spaced_apart()
    {
        var (tools, fake, rig, _) = await CreateAsync(o => { o.MinMinutesBetweenWrites = 30; o.MaxCommentsPerDay = 5; });
        await using var _ = fake; await using var __ = rig;

        Assert.StartsWith("Commented in r/science", await tools.ExecuteAsync("reddit_comment", Args("""{"parent":"t3_aaa111","text":"One."}"""), CancellationToken.None));
        var second = await tools.ExecuteAsync("reddit_comment", Args("""{"parent":"t3_aaa111","text":"Two."}"""), CancellationToken.None);
        Assert.Contains("the agency requires 30 between writes", second);
        Assert.Single(fake.To("/api/comment"));
        Assert.Contains("thing_id=t3_aaa111", fake.To("/api/comment").Single().Body);
    }

    [Fact]
    public async Task A_locked_thread_and_Reddits_own_errors_are_reported_not_hidden()
    {
        var (tools, fake, rig, _) = await CreateAsync();
        await using var _ = fake; await using var __ = rig;

        fake.Locked = true;
        Assert.Contains("locked or archived", await tools.ExecuteAsync("reddit_comment", Args("""{"parent":"t3_aaa111","text":"Hi."}"""), CancellationToken.None));
        Assert.Empty(fake.To("/api/comment"));

        fake.SubmitError = "you are doing that too much. try again in 9 minutes.";
        var result = await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"t","text":"x"}"""), CancellationToken.None);
        Assert.Contains("Reddit refused it: RATELIMIT -- you are doing that too much", result);
        Assert.Empty(rig.Store.State.RedditWrites); // a refused post is not counted
    }

    [Fact]
    public async Task Link_posts_need_permission_and_read_only_mode_refuses_writes_outright()
    {
        var (tools, fake, rig, options) = await CreateAsync();
        await using var _ = fake; await using var __ = rig;

        Assert.Contains("link posts are not allowed", await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"t","url":"https://example.com"}"""), CancellationToken.None));

        options.Mode = RedditMode.ReadOnly;
        Assert.Contains("read-only", await tools.ExecuteAsync("reddit_comment", Args("""{"parent":"t3_aaa111","text":"Hi."}"""), CancellationToken.None));
        Assert.Empty(fake.To("/api/submit"));
        Assert.Empty(fake.To("/api/comment"));
    }

    [Fact]
    public async Task Approving_a_draft_posts_it_and_marks_it_done_while_rejecting_closes_it()
    {
        var (tools, fake, rig, options) = await CreateAsync(o => o.Mode = RedditMode.Draft);
        await using var _ = fake; await using var __ = rig;

        await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"Drafted","text":"Body."}"""), CancellationToken.None);
        await tools.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"Also drafted","text":"Body 2."}"""), CancellationToken.None);
        var drafts = tools.ListDrafts();

        var posted = await tools.ApproveAsync(drafts[0].Id, CancellationToken.None);
        Assert.StartsWith("Posted to r/science", posted);
        Assert.Contains("title=Drafted", fake.To("/api/submit").Single().Body);
        tools.Reject(drafts[1].Id);

        var after = tools.ListDrafts();
        Assert.Equal("approved", after[0].Status);
        Assert.Equal("rejected", after[1].Status);
        await Assert.ThrowsAsync<RedditException>(() => tools.ApproveAsync(drafts[0].Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_executor_routes_reddit_tools_only_when_they_were_offered()
    {
        var (tools, fake, rig, options) = await CreateAsync(o => o.Mode = RedditMode.ReadOnly);
        await using var _ = fake; await using var __ = rig;
        var api = new Einkr.EinkrApiClient(new HttpClient(), rig.Api.BaseUrl, "k");
        var offered = new Tools.ToolCatalog().Build(rig.Options.Tools, 480, tools.Definitions());

        var executor = new Tools.AgencyToolExecutor(api, rig.ToolHost, new Notes.NotesStore(), rig.SessionDir, Guid.NewGuid(), offered, rig.Options.Tools, NullLogger.Instance, tools);
        var browse = await executor.ExecuteAsync("reddit_browse", Args("""{"subreddit":"science"}"""), CancellationToken.None);
        Assert.Contains("A good question", browse);

        // A write tool was not offered in ReadOnly mode: refused before reaching Reddit.
        var post = await executor.ExecuteAsync("reddit_post", Args("""{"subreddit":"science","title":"t","text":"x"}"""), CancellationToken.None);
        Assert.StartsWith("Error: 'reddit_post' is unavailable", post);
        Assert.Empty(fake.To("/api/submit"));

        // And with Reddit switched off entirely, no reddit tool is in the catalog at all.
        options.Enabled = false;
        Assert.DoesNotContain(new Tools.ToolCatalog().Build(rig.Options.Tools, 480, tools.Definitions()).Select(Tools.ToolCatalog.NameOf), n => n.StartsWith("reddit_"));
    }

    [Theory]
    [InlineData("t3_abc123", "abc123", null)]
    [InlineData("abc123", "abc123", null)]
    [InlineData("t1_def456", null, "def456")]
    [InlineData("https://www.reddit.com/r/science/comments/abc123/why_do_cows/", "abc123", null)]
    [InlineData("https://www.reddit.com/r/science/comments/abc123/why_do_cows/def456/", "abc123", "def456")]
    [InlineData("https://old.reddit.com/comments/abc123", "abc123", null)]
    public void Targets_are_understood_as_ids_or_urls(string input, string? post, string? comment)
    {
        var parsed = RedditTools.ParseTarget(input);
        Assert.NotNull(parsed);
        Assert.Equal(post, parsed!.Value.PostId);
        Assert.Equal(comment, parsed.Value.CommentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a thing!")]
    [InlineData("t9_abc123")]
    public void Nonsense_targets_are_rejected(string input) => Assert.Null(RedditTools.ParseTarget(input));
}
