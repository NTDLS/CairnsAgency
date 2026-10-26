using Microsoft.Extensions.DependencyInjection;
using VPT.Einkr.CairnsAgency.Agency;
using VPT.Einkr.CairnsAgency.Einkr;
using VPT.Einkr.CairnsAgency.Tools;

namespace VPT.Einkr.CairnsAgency;

/// <summary>The hands-on commands: look, check, rehearse, and run one wake on purpose. None of them (except `once --live`) sends
/// anything to the model or writes to the session.</summary>
public static class Cli
{
    public static async Task<int> RunAsync(string command, List<string> argv, IServiceProvider services, AgencyOptions options)
    {
        var store = services.GetRequiredService<StateStore>();
        var state = store.State;
        var runner = services.GetRequiredService<WakeRunner>();
        var catalog = services.GetRequiredService<ToolCatalog>();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        switch (command)
        {
            case "status":
                PrintStatus(options, state, store);
                return 0;

            case "pause":
                state.Paused = true;
                state.PausedBy = "manual";
                state.PauseReason = "paused from the command line";
                state.PausedAtUtc = DateTime.UtcNow;
                store.Save();
                Console.WriteLine("Paused (manual). Josh writing to the session will NOT resume it; run `resume`.");
                return 0;

            case "resume":
                state.Paused = false;
                state.PausedBy = null;
                state.PauseReason = null;
                state.ConsecutiveFailures = 0;
                state.NextWakeUtc = null; // rescheduled from now on the next tick
                store.Save();
                Console.WriteLine("Resumed. The next wake will be scheduled one interval from the next tick.");
                return 0;

            case "prompt":
            {
                var context = runner.BuildContext(state.WakeCount + 1, state.JustResumedAfterPause);
                Console.WriteLine(NudgePromptBuilder.Build(context));
                Console.WriteLine();
                Console.WriteLine("---- tools that would be offered ----");
                Console.WriteLine(string.Join(", ", catalog.Build(options.Tools, options.Schedule.MinIntervalMinutes, services.GetRequiredService<VPT.Einkr.CairnsAgency.Reddit.RedditTools>().Definitions()).Select(ToolCatalog.NameOf)));
                return 0;
            }

            case "check":
                return await CheckAsync(options, services, runner, cts.Token);

            case "dry-run":
            {
                var probe = services.GetRequiredService<ActivityProbe>();
                try
                {
                    var status = await probe.CheckAsync(cts.Token);
                    Console.WriteLine($"Josh: here={status.Here}, returned-since-last-seen={status.Returned}, last active {(status.LastActiveUtc?.ToLocalTime().ToString("g") ?? "unknown")} (via {status.Source}).");
                    Console.WriteLine(status.Here ? "=> a due wake would WAIT." : "=> a due wake would run.");
                }
                catch (Exception ex) { Console.WriteLine($"(could not reach the API to check on Josh: {ex.Message})"); }

                var outcome = await runner.RunAsync(dryRun: true, cts.Token);
                Console.WriteLine();
                Console.WriteLine(outcome.Prompt);
                return 0;
            }

            case "once":
            {
                var live = argv.Contains("--live");
                var force = argv.Contains("--force");
                if (live && string.IsNullOrWhiteSpace(options.Einkr.ApiKey)) { Console.Error.WriteLine("No API key (EINKR_API_KEY)."); return 2; }

                if (live && !force)
                {
                    var probe = services.GetRequiredService<ActivityProbe>();
                    var status = await probe.CheckAsync(cts.Token);
                    if (status.Here) { Console.Error.WriteLine("Josh is in the session right now. Not waking Cairn. (--force overrides.)"); return 3; }
                }

                var worker = services.GetRequiredService<AgencyWorker>();
                var outcome = await worker.RunWakeAsync(dryRun: !live, cts.Token);
                Console.WriteLine();
                Console.WriteLine($"Result: {(outcome.Success ? "ok" : "FAILED")} ({outcome.EndReason}); {outcome.Rounds} rounds, {outcome.ToolCalls} tool calls, {outcome.Elapsed.TotalMinutes:0.0} min.");
                if (outcome.Error != null) Console.WriteLine($"Error: {outcome.Error}");
                if (!outcome.DryRun) Console.WriteLine($"\nCairn's final message:\n{outcome.FinalText}");
                return outcome.Success ? 0 : 1;
            }

            case "reddit-check":
            {
                var o = options.Reddit;
                Console.WriteLine($"Enabled={o.Enabled}  Mode={o.Mode}  Configured={o.Configured}  AllowNsfw={o.AllowNsfw}  AllowLinkPosts={o.AllowLinkPosts}");
                Console.WriteLine($"Write allow-list: {(o.WriteSubreddits.Count == 0 ? "(empty: no writing anywhere)" : string.Join(", ", o.WriteSubreddits.Select(s => "r/" + s)))}");
                Console.WriteLine($"Limits: {o.MaxPostsPerDay} posts/day, {o.MaxCommentsPerDay} comments/day, {o.MinMinutesBetweenWrites} min apart; disclosure footer: {(string.IsNullOrWhiteSpace(o.DisclosureFooter) ? "NONE" : "yes")}");
                if (!o.Configured) { Console.WriteLine("Not configured: set Agency:Reddit:Enabled=true and the ClientId/ClientSecret/Username/Password (appsettings.Local.json)."); return 1; }
                try
                {
                    var tools = services.GetRequiredService<VPT.Einkr.CairnsAgency.Reddit.RedditTools>();
                    using var doc = System.Text.Json.JsonDocument.Parse("{\"subreddit\":\"announcements\"}");
                    var info = await tools.ExecuteAsync("reddit_subreddit_info", doc.RootElement.Clone(), cts.Token);
                    Console.WriteLine(info.StartsWith("Error") ? $"[FAIL] {info}" : "[ ok ] signed in and read r/announcements");
                    return info.StartsWith("Error") ? 1 : 0;
                }
                catch (Exception ex) { Console.WriteLine($"[FAIL] {ex.Message}"); return 1; }
            }

            case "reddit-tool":
            {
                // Runs one Reddit tool exactly as the model would (same mode, allow-list, caps, footer) -- a hands-on way to try them.
                var rest = argv.Skip(1).ToList();
                var name = rest.FirstOrDefault();
                if (name is null || !name.StartsWith("reddit_"))
                {
                    Console.Error.WriteLine("Usage: reddit-tool <reddit_browse|reddit_search|reddit_read|reddit_subreddit_info|reddit_post|reddit_comment> '<json arguments>'");
                    return 2;
                }
                var json = string.Join(" ", rest.Skip(1)).Trim();
                if (json.Length == 0) json = "{}";
                System.Text.Json.JsonElement args;
                try { args = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(); }
                catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"Those arguments are not valid JSON: {ex.Message}"); return 2; }
                var tools = services.GetRequiredService<VPT.Einkr.CairnsAgency.Reddit.RedditTools>();
                Console.WriteLine(await tools.ExecuteAsync(name, args, cts.Token));
                return 0;
            }

            case "reddit-drafts":
            {
                var tools = services.GetRequiredService<VPT.Einkr.CairnsAgency.Reddit.RedditTools>();
                var drafts = tools.ListDrafts();
                if (drafts.Count == 0) { Console.WriteLine("No drafts."); return 0; }
                foreach (var d in drafts)
                {
                    Console.WriteLine($"[{d.Id}] {d.Status.ToUpperInvariant()}  {d.Kind} in r/{d.Subreddit}  ({d.CreatedUtc.ToLocalTime():g})");
                    if (d.Title != null) Console.WriteLine($"   title: {d.Title}");
                    if (d.Context != null) Console.WriteLine($"   {d.Context}");
                    Console.WriteLine($"   {(d.Url ?? d.Text ?? "").Replace("\n", "\n   ")}");
                    if (d.ResultUrl != null) Console.WriteLine($"   -> {d.ResultUrl}");
                    Console.WriteLine();
                }
                return 0;
            }

            case "reddit-approve":
            case "reddit-reject":
            {
                var id = argv.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                if (id is null) { Console.Error.WriteLine($"Usage: {command} <draft id>"); return 2; }
                var tools = services.GetRequiredService<VPT.Einkr.CairnsAgency.Reddit.RedditTools>();
                try
                {
                    if (command == "reddit-reject") { tools.Reject(id); Console.WriteLine($"Draft {id} rejected."); }
                    else Console.WriteLine(await tools.ApproveAsync(id, cts.Token));
                    return 0;
                }
                catch (VPT.Einkr.CairnsAgency.Reddit.RedditException ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }

            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Commands: run, status, check, prompt, dry-run, once [--live] [--force], pause, resume, reddit-check, reddit-tool <name> <json>, reddit-drafts, reddit-approve <id>, reddit-reject <id>.");
                return 2;
        }
    }

    private static void PrintStatus(AgencyOptions options, AgencyState state, StateStore store)
    {
        Console.WriteLine($"DryRun            : {options.DryRun}");
        Console.WriteLine($"Session           : {options.Session.SessionId}");
        Console.WriteLine($"Interval          : {state.IntervalMinutes} min ({NudgePromptBuilder.Human(TimeSpan.FromMinutes(state.IntervalMinutes))})");
        Console.WriteLine($"Paused            : {state.Paused}{(state.Paused ? $" by {state.PausedBy} -- {state.PauseReason}" : "")}");
        Console.WriteLine($"Next wake         : {(state.NextWakeUtc?.ToLocalTime().ToString("g") ?? "(not scheduled)")}");
        Console.WriteLine($"Wakes so far      : {state.WakeCount} (consecutive failures: {state.ConsecutiveFailures})");
        Console.WriteLine($"Josh last seen    : {(state.LastJoshActivityUtc?.ToLocalTime().ToString("g") ?? "never")}");
        Console.WriteLine($"State folder      : {store.StateDirectory}");
        Console.WriteLine($"Disabled file     : {(File.Exists(Path.Combine(store.StateDirectory, "DISABLED")) ? "PRESENT (service idles)" : "no")}");
        foreach (var w in state.Recent.TakeLast(5))
            Console.WriteLine($"  wake {w.Number,3} {w.StartedUtc.ToLocalTime():g}  {(w.Success ? "ok    " : "FAILED")} {w.EndReason,-10} {w.Rounds} rounds  {w.Summary.Replace('\n', ' ')[..Math.Min(80, w.Summary.Replace('\n', ' ').Length)]}");
    }

    private static async Task<int> CheckAsync(AgencyOptions options, IServiceProvider services, WakeRunner runner, CancellationToken ct)
    {
        var failures = 0;
        void Line(bool ok, string text) { Console.WriteLine($"[{(ok ? " ok " : "FAIL")}] {text}"); if (!ok) failures++; }

        Line(!string.IsNullOrWhiteSpace(options.Einkr.ApiKey), "API key present (EINKR_API_KEY / appsettings.Local.json)");
        var toolHost = services.GetRequiredService<ToolHostClient>();
        Line(toolHost.ExecutableExists, $"Tool host exists: {options.Tools.ToolHostPath}");
        Line(Directory.Exists(runner.SessionDirectory), $"Session folder exists: {runner.SessionDirectory}");
        Line(Directory.Exists(runner.ArchiveRoot), $"Archive exists: {runner.ArchiveRoot}");
        Console.WriteLine(File.Exists(Path.Combine(runner.ArchiveRoot, "AGENCY.md"))
            ? "[ ok ] Charter AGENCY.md is in the archive"
            : "[note] Charter AGENCY.md is not in the archive yet -- the first live wake writes it (only if missing).");

        if (!string.IsNullOrWhiteSpace(options.Einkr.ApiKey))
        {
            var api = services.GetRequiredService<EinkrApiClient>();
            try
            {
                var models = await api.GetModelsAsync(ct);
                var model = models.FirstOrDefault(m => m.Name == options.Session.ModelName);
                Line(model != null, $"Model '{options.Session.ModelName}' known (context window {model?.ContextWindowSize})");
            }
            catch (Exception ex) { Line(false, $"API reachable / key accepted: {ex.Message}"); }

            try
            {
                var sessions = await api.ListSessionsAsync("Chat", ct);
                var mine = sessions.FirstOrDefault(s => s.Id == Guid.Parse(options.Session.SessionId));
                Line(mine != null, $"Session found among Chat sessions{(mine != null ? $": \"{mine.Name}\", last used {mine.LastUsedUTC.ToLocalTime():g}" : "")}");
            }
            catch (Exception ex) { Line(false, $"Listing sessions: {ex.Message}"); }

            try
            {
                var activity = await api.GetActivityAsync(Guid.Parse(options.Session.SessionId), ct);
                Console.WriteLine(activity != null
                    ? $"[ ok ] /activity endpoint available (newest turn #{activity.LastChatLogId}, open={activity.LastTurnOpen})"
                    : "[warn] /activity endpoint not found -- the API has not been deployed with it; the agency falls back to the coarser session stamp.");
            }
            catch (Exception ex) { Line(false, $"Activity probe: {ex.Message}"); }
        }

        Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} problem(s).");
        return failures == 0 ? 0 : 1;
    }
}
