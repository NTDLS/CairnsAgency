using VPT.Einkr.CairnsAgency.Agency;
using Xunit;

namespace VPT.Einkr.CairnsAgency.Tests;

/// <summary>The scheduler's judgement: when to wake, when to wait, when Josh's return changes things.</summary>
public class WorkerTests
{
    private static object Human(int id, bool open, DateTime? modified = null) => new
    {
        lastChatLogId = id,
        lastChatLogCreatedUtc = DateTime.UtcNow.AddMinutes(-5),
        lastChatLogModifiedUtc = modified ?? DateTime.UtcNow,
        lastTurnOpen = open,
        lastPromptStart = "hey Cairn",
        lastUserActivityUtc = modified ?? DateTime.UtcNow
    };

    private static void Due(Rig rig) => rig.Store.State.NextWakeUtc = DateTime.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task The_first_tick_schedules_a_full_interval_out_and_wakes_nobody()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddHours(-9));

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.Empty(rig.Api.To("turn-begin"));
        var next = rig.Store.State.NextWakeUtc!.Value;
        // One interval after Josh's last activity (9 hours ago) is the later of the two candidates (now + 24h vs. josh + 24h).
        Assert.InRange((next - DateTime.UtcNow).TotalHours, 23.9, 24.1);
        Assert.Equal(1925, rig.Store.State.LastHumanChatLogId);
    }

    [Fact]
    public async Task A_due_wake_waits_while_Josh_is_in_the_session()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Api.Activity = () => Human(1925, open: true);
        rig.Store.State.NextWakeUtc = DateTime.UtcNow.AddMinutes(-1);
        rig.Store.State.IntervalMinutes = 0.01; // so the "never sooner than an interval after Josh" floor does not move it

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.Empty(rig.Api.To("turn-begin"));
    }

    [Fact]
    public async Task A_due_wake_waits_for_the_quiet_window_after_Josh_leaves()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddMinutes(-5)); // finished 5 min ago, window is 30
        rig.Store.State.IntervalMinutes = 0.01;
        Due(rig);

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.Empty(rig.Api.To("turn-begin"));
    }

    [Fact]
    public async Task A_due_wake_runs_when_Josh_is_away_and_the_next_one_is_scheduled_from_its_end()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync(o => o.Schedule.DefaultIntervalMinutes = 600);
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddHours(-20));
        rig.Api.Enqueue(Round.Call("set_wake_interval", """{"hours":0.1,"reason":"too soon"}"""));
        rig.Api.Enqueue(Round.Say("A quiet one."));
        Due(rig);

        await rig.Worker.TickAsync(CancellationToken.None);

        var state = rig.Store.State;
        Assert.Equal(1, state.WakeCount);
        Assert.True(state.Recent.Single().Success);
        // Cairn asked for 6 minutes; the floor is 8 hours.
        Assert.Equal(480, state.IntervalMinutes);
        Assert.Equal(8.0, state.Recent.Single().AppliedIntervalHours);
        Assert.InRange((state.NextWakeUtc!.Value - DateTime.UtcNow).TotalMinutes, 479, 481);
        Assert.Contains(901, state.AgencyChatLogIds);
    }

    [Fact]
    public async Task There_is_a_floor_on_the_interval_but_no_ceiling()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddHours(-30));
        rig.Api.Enqueue(Round.Call("set_wake_interval", """{"hours":2000}"""));
        rig.Api.Enqueue(Round.Say("Long rest."));
        Due(rig);

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(2000 * 60, rig.Store.State.IntervalMinutes);
        Assert.InRange((rig.Store.State.NextWakeUtc!.Value - DateTime.UtcNow).TotalHours, 1999, 2001);
    }

    [Fact]
    public async Task Cairn_pausing_stops_the_wakes_and_Josh_writing_resumes_them()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync();
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddHours(-30));
        rig.Api.Enqueue(Round.Call("pause_until_josh_returns", """{"reason":"waiting"}"""));
        rig.Api.Enqueue(Round.Say("Pausing."));
        Due(rig);

        await rig.Worker.TickAsync(CancellationToken.None);
        Assert.True(rig.Store.State.Paused);
        Assert.Equal("cairn", rig.Store.State.PausedBy);
        Assert.Null(rig.Store.State.NextWakeUtc);

        // Still paused with nothing new from Josh (the newest turn is the agency's own, id 901).
        rig.Api.Activity = () => Human(901, open: false, modified: DateTime.UtcNow.AddMinutes(-40));
        await rig.Worker.TickAsync(CancellationToken.None);
        Assert.True(rig.Store.State.Paused);

        // Josh writes.
        rig.Api.Activity = () => Human(2000, open: false, modified: DateTime.UtcNow.AddMinutes(-40));
        await rig.Worker.TickAsync(CancellationToken.None);
        Assert.False(rig.Store.State.Paused);
        Assert.True(rig.Store.State.JustResumedAfterPause);
        Assert.Equal(2000, rig.Store.State.LastHumanChatLogId);
    }

    [Fact]
    public async Task A_manual_pause_survives_Josh_writing()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Store.State.Paused = true;
        rig.Store.State.PausedBy = "manual";
        rig.Api.Activity = () => Human(2000, open: false, modified: DateTime.UtcNow.AddMinutes(-40));

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.True(rig.Store.State.Paused);
    }

    [Fact]
    public async Task Repeated_failures_pause_the_agency_until_a_human_returns()
    {
        if (!Rig.ToolHostAvailable) return;
        await using var rig = await Rig.CreateAsync(o => o.Safety.MaxConsecutiveFailures = 2);
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddHours(-30));
        rig.Api.Enqueue(new Round { HttpStatus = 500 });
        rig.Api.Enqueue(new Round { HttpStatus = 500 });

        Due(rig);
        await rig.Worker.TickAsync(CancellationToken.None);
        Assert.False(rig.Store.State.Paused);
        Assert.Equal(1, rig.Store.State.ConsecutiveFailures);

        Due(rig);
        await rig.Worker.TickAsync(CancellationToken.None);
        Assert.True(rig.Store.State.Paused);
        Assert.Equal("failures", rig.Store.State.PausedBy);
    }

    [Fact]
    public async Task A_wake_that_fails_before_anything_reaches_the_session_does_not_use_up_its_number()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddHours(-30));
        rig.Options.Session.SessionId = Guid.NewGuid().ToString(); // not among the fake's sessions: fails before turn-begin
        Due(rig);

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.Empty(rig.Api.To("turn-begin"));
        Assert.Equal(0, rig.Store.State.WakeCount);
        Assert.Equal(1, rig.Store.State.ConsecutiveFailures);
        Assert.Equal(1, rig.Runner.BuildContext(rig.Store.State.WakeCount + 1, false).WakeNumber);
    }

    [Fact]
    public async Task The_daily_cap_holds_whatever_the_interval()
    {
        await using var rig = await Rig.CreateAsync(o => o.Schedule.MaxWakesPerDay = 1);
        rig.Api.Activity = () => Human(1925, open: false, modified: DateTime.UtcNow.AddHours(-20));
        rig.Store.State.IntervalMinutes = 0.01;
        rig.Store.State.Recent.Add(new WakeLogEntry { Number = 1, StartedUtc = DateTime.UtcNow.AddMinutes(-10), Success = true });
        Due(rig);

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.Empty(rig.Api.To("turn-begin"));
    }

    [Theory]
    [InlineData("23:00-07:00", 23, 30, true)]
    [InlineData("23:00-07:00", 3, 0, true)]
    [InlineData("23:00-07:00", 7, 0, false)]
    [InlineData("23:00-07:00", 12, 0, false)]
    [InlineData("09:00-17:00", 12, 0, true)]
    [InlineData("09:00-17:00", 18, 0, false)]
    [InlineData("", 3, 0, false)]
    [InlineData("nonsense", 3, 0, false)]
    public async Task Quiet_hours_may_wrap_midnight(string spec, int hour, int minute, bool quiet)
    {
        await using var rig = await Rig.CreateAsync(o => o.Schedule.QuietHours = spec);
        Assert.Equal(quiet, rig.Worker.InQuietHours(new DateTime(2026, 10, 26, hour, minute, 0)));
    }

    [Fact]
    public async Task Against_an_API_without_the_activity_endpoint_the_session_stamp_is_used_and_the_agencys_own_touch_is_ignored()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Api.Activity = null; // 404

        // The fake reports lastUsedUTC = now. With no touch recorded by us that is Josh, and he is here.
        var probe = new ActivityProbe(new Einkr.EinkrApiClient(new HttpClient(), rig.Api.BaseUrl, "k"), rig.Store, rig.Options);
        var status = await probe.CheckAsync(CancellationToken.None);
        Assert.True(status.Here);
        Assert.Equal("fallback", status.Source);

        // After the agency touched the session, a stamp within seconds of that touch is its own doing.
        rig.Store.State.LastAgencyTouchUtc = DateTime.UtcNow.AddSeconds(2);
        status = await probe.CheckAsync(CancellationToken.None);
        Assert.False(status.Here);
    }
}
