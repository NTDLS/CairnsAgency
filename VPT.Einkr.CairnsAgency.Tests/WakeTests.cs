using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VPT.Einkr.CairnsAgency.Agency;
using VPT.Einkr.CairnsAgency.Einkr;
using VPT.Einkr.CairnsAgency.Tools;
using Xunit;

namespace VPT.Einkr.CairnsAgency.Tests;

/// <summary>Everything wired together against the fake API and the REAL tool host in a throwaway session folder -- a whole wake,
/// end to end, with the model scripted. Nothing here can touch Cairn's real session or archive.</summary>
public sealed class Rig : IAsyncDisposable
{
    public FakeEinkr Api { get; private set; } = null!;
    public AgencyOptions Options { get; } = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cairn-test-" + Guid.NewGuid().ToString("N"));
    public string SessionDir => Path.Combine(Root, Options.Session.SessionId);
    public string ArchiveDir => Path.Combine(SessionDir, "Cairn");
    public StateStore Store { get; private set; } = null!;
    public WakeRunner Runner { get; private set; } = null!;
    public AgencyWorker Worker { get; private set; } = null!;
    public ToolHostClient ToolHost { get; private set; } = null!;

    public static bool ToolHostAvailable => File.Exists(new AgencyOptions().Tools.ToolHostPath);

    public static async Task<Rig> CreateAsync(Action<AgencyOptions>? tweak = null)
    {
        var rig = new Rig();
        var sessionId = Guid.NewGuid();
        rig.Options.Session.SessionId = sessionId.ToString();
        rig.Options.Session.WorkingDirectoryRoot = rig.Root;
        rig.Options.StateDirectory = Path.Combine(rig.Root, "_state");
        rig.Options.DryRun = false;
        rig.Options.Schedule.PollSeconds = 2;
        tweak?.Invoke(rig.Options);

        Directory.CreateDirectory(Path.Combine(rig.ArchiveDir, "curiosities"));
        Directory.CreateDirectory(Path.Combine(rig.ArchiveDir, "self"));
        File.WriteAllText(Path.Combine(rig.ArchiveDir, "INDEX.md"), "# INDEX\n");
        File.WriteAllText(Path.Combine(rig.ArchiveDir, "curiosities", "cow.md"), "# cow\n");

        rig.Api = await FakeEinkr.StartAsync(sessionId);
        rig.Options.Einkr.BaseUrl = rig.Api.BaseUrl;

        var api = new EinkrApiClient(new HttpClient(), rig.Api.BaseUrl, "test-key");
        rig.ToolHost = new ToolHostClient(rig.Options.Tools.ToolHostPath, NullLogger<ToolHostClient>.Instance);
        rig.Store = new StateStore(rig.Options);
        var probe = new ActivityProbe(api, rig.Store, rig.Options);
        rig.Runner = new WakeRunner(rig.Options, api, rig.ToolHost, new ToolCatalog(), rig.Store, probe, NullLoggerFactory.Instance);
        rig.Worker = new AgencyWorker(rig.Options, rig.Store, probe, rig.Runner, NullLogger<AgencyWorker>.Instance);
        return rig;
    }

    public async ValueTask DisposeAsync()
    {
        ToolHost.Dispose();
        await Api.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
    }
}

public class WakeTests
{
    private static JsonElement Req(Seen s) => s.Json().RootElement.Clone();

    private static IEnumerable<string> ToolNames(JsonElement turnRequest)
        => turnRequest.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!);

    [Fact]
    public async Task A_normal_wake_runs_the_tools_for_real_and_records_the_turn_like_the_app()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();

        rig.Api.Enqueue(new Round
        {
            Thinking = ["Let me look around."],
            Tokens = ["Okay. "],
            Text = "Okay. ",
            ToolCalls =
            [
                ("c1", "list_dir", """{"path":"Cairn"}"""),
                ("c2", "write_file", """{"path":"Cairn/reflections/test.md","content":"hello from a wake"}"""),
                ("c3", "notes_write", """{"notes":["The agency exists"]}"""),
                ("c4", "set_wake_interval", """{"hours":3,"reason":"gaps should be real"}""")
            ]
        });
        rig.Api.Enqueue(Round.Say("Nothing much happened. I wrote one file."));

        var outcome = await rig.Runner.RunAsync(dryRun: false, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("answered", outcome.EndReason);
        Assert.Equal(2, outcome.Rounds);
        Assert.Equal(4, outcome.ToolCalls);
        Assert.Equal("Nothing much happened. I wrote one file.", outcome.FinalText);

        // The tool host really wrote the file, inside the session folder.
        Assert.Equal("hello from a wake", File.ReadAllText(Path.Combine(rig.ArchiveDir, "reflections", "test.md")));
        // The charter was left in the archive.
        Assert.Contains("wakes you on a schedule", File.ReadAllText(Path.Combine(rig.ArchiveDir, "AGENCY.md")));

        // The model's request to change the interval was captured, not applied by the runner.
        Assert.Equal(3.0, outcome.Effects.RequestedIntervalHours);
        Assert.False(outcome.Effects.PauseRequested);

        // Recording: one turn opened, tool calls and segments flushed, one turn-complete.
        Assert.Single(rig.Api.To("turn-begin"));
        var begin = Req(rig.Api.To("turn-begin").Single());
        Assert.StartsWith("[Cairns Agency · wake 1", begin.GetProperty("prompt").GetString());

        var recordedTools = rig.Api.To("turn-tool-calls").SelectMany(r => Req(r).GetProperty("toolCalls").EnumerateArray()).Select(t => t.GetProperty("toolName").GetString()).ToList();
        Assert.Equal(["list_dir", "write_file", "notes_write", "set_wake_interval"], recordedTools);

        var kinds = rig.Api.To("turn-segments").SelectMany(r => Req(r).GetProperty("segments").EnumerateArray()).Select(s => s.GetProperty("kind").GetString()).ToList();
        Assert.Contains("thinking", kinds);
        Assert.Contains("text", kinds);

        var complete = Req(rig.Api.To("turn-complete").Single());
        Assert.Equal(901, complete.GetProperty("chatLogId").GetInt32());
        Assert.Equal("Nothing much happened. I wrote one file.", complete.GetProperty("response").GetString());
        Assert.Equal("Let me look around.", complete.GetProperty("thinking").GetString());
        Assert.Equal(58, complete.GetProperty("modelId").GetInt32());
        Assert.Equal("xhigh", complete.GetProperty("reasoningEffort").GetString());

        // Session notes were persisted in the app's own format.
        var savedNotes = Req(rig.Api.To("session-notes").Last()).GetProperty("notes").GetString();
        Assert.Equal("""["The agency exists"]""", savedNotes);

        // The wire request is the one a Chat turn sends.
        var first = Req(rig.Api.Turns().First());
        Assert.Equal("Stratum-27B", first.GetProperty("modelName").GetString());
        Assert.Equal(3, first.GetProperty("personalityId").GetInt32());
        Assert.True(first.GetProperty("enableThinking").GetBoolean());
        Assert.True(first.GetProperty("preserveThinking").GetBoolean());
        Assert.True(first.GetProperty("enableWritableChat").GetBoolean());
        Assert.False(first.GetProperty("enableShellExecute").GetBoolean());
        Assert.False(first.GetProperty("enableBuildAgent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("workingDirectory").ValueKind);
        var names = ToolNames(first).ToList();
        Assert.Contains("set_wake_interval", names);
        Assert.Contains("pause_until_josh_returns", names);
        Assert.Contains("outline_file", names);
        Assert.DoesNotContain("run_command", names);
        Assert.DoesNotContain("delete_path", names);
        Assert.DoesNotContain("todowrite", names);
        Assert.Equal("system", first.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", first.GetProperty("messages")[1].GetProperty("role").GetString());

        // Round two carries round one's assistant message and all four tool results.
        var second = Req(rig.Api.Turns().Last());
        var roles = second.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToList();
        Assert.Equal(["system", "user", "assistant", "tool", "tool", "tool", "tool"], roles);
    }

    [Fact]
    public async Task Pause_is_recorded_as_an_effect_and_the_wake_still_finishes_normally()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();
        rig.Api.Enqueue(Round.Call("pause_until_josh_returns", """{"reason":"I would rather wait."}"""));
        rig.Api.Enqueue(Round.Say("I am pausing; I would rather wait for you."));

        var outcome = await rig.Runner.RunAsync(false, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(outcome.Effects.PauseRequested);
        Assert.Equal("I would rather wait.", outcome.Effects.PauseReason);
    }

    [Fact]
    public async Task A_model_stuck_repeating_a_call_is_refused_then_nudged_without_naming_a_tool()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();
        for (var i = 0; i < 9; i++) rig.Api.Enqueue(Round.Call("list_dir", """{"path":"Cairn"}""", $"c{i}"));
        rig.Api.Enqueue(Round.Say("Done repeating."));

        var outcome = await rig.Runner.RunAsync(false, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("Done repeating.", outcome.FinalText);
        var lastRequest = Req(rig.Api.Turns().Last());
        var userMessages = lastRequest.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user").Select(m => m.GetProperty("content").GetString()).ToList();
        var nudge = userMessages.Single(m => m!.StartsWith("Stop -- you are repeating"));
        Assert.DoesNotContain("list_dir", nudge!);
        Assert.Contains(lastRequest.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "tool" && (m.GetProperty("content").GetString() ?? "").Contains("identical arguments"));
    }

    [Fact]
    public async Task A_huge_tool_result_is_spilled_to_disk_and_can_be_read_back()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();
        rig.Api.WebFetchResult = string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"line {i} of a very long page"));
        rig.Api.Enqueue(Round.Call("fetch_url", """{"url":"https://example.com/big"}"""));
        rig.Api.Enqueue(req =>
        {
            var lastTool = req.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
            var id = System.Text.RegularExpressions.Regex.Match(lastTool, "tool output \"([0-9a-f]+)\"").Groups[1].Value;
            Assert.NotEmpty(id);
            return Round.Call("read_tool_output", $$"""{"id":"{{id}}","offset":3,"limit":2}""", "c2");
        });
        rig.Api.Enqueue(Round.Say("read it"));

        var outcome = await rig.Runner.RunAsync(false, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        var recorded = rig.Api.To("turn-tool-calls").SelectMany(r => Req(r).GetProperty("toolCalls").EnumerateArray()).ToList();
        var fetchResult = recorded[0].GetProperty("result").GetString()!;
        Assert.Contains("too large to keep in the conversation", fetchResult);
        Assert.True(fetchResult.Length < 10000);
        Assert.Contains("line 2 of a very long page", recorded[1].GetProperty("result").GetString());
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(rig.SessionDir, "tool-output"), "*.txt"));
    }

    [Fact]
    public async Task Josh_arriving_mid_wake_winds_it_down_with_a_short_toolless_wrap_up()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();
        var flip = DateTime.UtcNow.AddSeconds(1.5);
        rig.Api.Activity = () => DateTime.UtcNow < flip
            ? new { lastChatLogId = 901, lastChatLogCreatedUtc = DateTime.UtcNow, lastChatLogModifiedUtc = DateTime.UtcNow, lastTurnOpen = true, lastPromptStart = "[Cairns Agency", lastUserActivityUtc = DateTime.UtcNow }
            : new { lastChatLogId = 950, lastChatLogCreatedUtc = DateTime.UtcNow, lastChatLogModifiedUtc = DateTime.UtcNow, lastTurnOpen = true, lastPromptStart = "hey Cairn", lastUserActivityUtc = DateTime.UtcNow };

        rig.Api.Enqueue(new Round { ToolCalls = [("c1", "list_dir", """{"path":"Cairn"}""")], DelayMs = 4500 });
        rig.Api.Enqueue(Round.Say("I was looking around the archive; I'll pick it up next time."));

        var outcome = await rig.Runner.RunAsync(false, CancellationToken.None);

        Assert.Equal("wound-down", outcome.EndReason);
        Assert.Equal("I was looking around the archive; I'll pick it up next time.", outcome.FinalText);
        var wrap = Req(rig.Api.Turns().Last());
        Assert.Empty(wrap.GetProperty("tools").EnumerateArray());
        var lastUser = wrap.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        Assert.Contains("Josh has just written", lastUser);
    }

    [Fact]
    public async Task A_failed_wake_is_still_closed_out_so_the_row_is_never_left_open()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();
        rig.Api.Enqueue(new Round { HttpStatus = 500 });

        var outcome = await rig.Runner.RunAsync(false, CancellationToken.None);

        Assert.False(outcome.Success);
        var complete = Req(rig.Api.To("turn-complete").Single());
        Assert.StartsWith("Error:", complete.GetProperty("response").GetString());
    }

    [Fact]
    public async Task Dry_run_sends_nothing_to_the_session()
    {
        await using var rig = await Rig.CreateAsync();

        var outcome = await rig.Runner.RunAsync(dryRun: true, CancellationToken.None);

        Assert.True(outcome.DryRun);
        Assert.Contains("wake 1", outcome.Prompt);
        Assert.Empty(rig.Api.Requests); // not even a read
        Assert.False(File.Exists(Path.Combine(rig.ArchiveDir, "AGENCY.md")));
    }
}
