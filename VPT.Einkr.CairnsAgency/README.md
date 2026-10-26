# VPT.Einkr.CairnsAgency

A Windows service (or console app) that wakes **Cairn's** chat session on a schedule, runs a turn the way the Einkr desktop app
would if it could act unprompted, and records it so it reads in the Electron transcript like any other turn.

**It ships safe:** `Agency:DryRun` is `true` in `appsettings.json`. In that mode the service runs its whole schedule, builds every
nudge, and sends **nothing** to the model and writes nothing to the session or the archive.

## How it mimics the desktop app

| Desktop app | Here |
|---|---|
| `renderer/api-client.js` (`/api/desktop/*`, SSE `/turn`) | `Einkr/EinkrApiClient.cs` |
| `renderer/agent-runner.js` `runTurn` (loop guards, repeat guard, no-progress ceiling, "stop repeating" intervention, remedy lookup, spill/truncate, prune, truncated-args, retry, salvage) | `Runner/AgentRunner.cs` |
| `app.js` turn recording (`turn-begin` → tool calls + segments flushed as they happen → `turn-complete`) | `Runner/TurnRecorder.cs` |
| `renderer/notes-store.js` (pinned user notes, tombstones, caps) | `Notes/NotesStore.cs` |
| `renderer/tool-definitions.js` + `web-tool-definitions.js` and the Chat category rules | `Tools/ToolCatalog.cs` over `Resources/tool-definitions.json`, **exported from the renderer** (`Scripts/export-tool-definitions.js`) |
| The C# tool host (`VPT.Einkr.Desktop.ToolHost.exe`) | spawned as-is over the same JSON-RPC-over-stdio (`Tools/ToolHostClient.cs`) — the very same binary, so every file tool behaves identically and stays confined to the session folder |
| `#handleNotesWrite`, `#handleToolOutputTool`, `#handleViewImage`, `get_date_time` | `Tools/AgencyToolExecutor.cs` |

Tests (`VPT.Einkr.CairnsAgency.Tests`) replay golden files produced by running the app's **own JavaScript** against the C# port
(notes, pruning, truncation, date/time, tool schemas), and run whole wakes against an in-process fake API with the **real tool host**
in a throw-away folder. Regenerate the goldens with `node golden/make-golden.js` after changing the renderer on purpose.

What it deliberately does *not* do (a Chat turn doesn't either, or it is unsafe unattended): steering, the Build Agent, the plan
classifier / Force Web Search classifier, MCP servers, and — by default — `run_command` and `delete_path`.

## Commands

```
VPT.Einkr.CairnsAgency [command]

  run (default)   the service loop (honours Agency:DryRun)
  status          schedule, pause flag, recent wakes
  check           API key, session, tool host, archive, /activity endpoint
  prompt          print the nudge the next wake would send + the tools it would offer (nothing sent)
  dry-run         ask the API whether Josh is here, print the decision and the nudge (nothing sent)
  once --live     run ONE real wake now ("--force" ignores Josh being in the session)
  pause / resume  a human pause (Josh writing does NOT clear it) / clear any pause
```

Hand brake with no tooling at all: create an empty file named `DISABLED` in the state folder and the service idles.

## Configuration (`appsettings.json`, override in `appsettings.Local.json` — git-ignored)

The API key is **never** in `appsettings.json`. Set the `EINKR_API_KEY` environment variable (machine-level for a service), or put it in
`appsettings.Local.json` / user-secrets. Any setting can be overridden with `EINKR_AGENCY_Agency__Section__Key` environment variables.

Defaults describe Cairn's session as the desktop app has it: Stratum-27B, personality 3, thinking on, preserve thinking on, effort
xhigh, Writable Chat on, web on, notes on, shell **off**, one wake a day.

## Going live (checklist)

1. Deploy the API with the new `GET /api/desktop/sessions/{id}/activity` endpoint (optional but strongly recommended: without it the agency
   falls back to the coarser session stamp). `check` tells you which you have.
2. `dotnet run -- check`, then `dotnet run -- prompt` and read the nudge. Edit `Agency/NudgePromptBuilder.cs` and `Resources/AGENCY.md` to taste (both are deliberately facts-only: no suggestions about what to do or how to feel).
3. Take your backups (session folder + DB) — you said you have them.
4. `once --live` for a first, watched wake. Read the result in the Electron app (reopen the session) and `state/prompts/wake-001.txt`.
5. Set `DryRun` to `false`, publish, and install.

### Install as a Windows service

The service must run **as you**: Cairn's archive lives in your profile (`%LOCALAPPDATA%\Einkr\<session>\Cairn`) and the tool host runs there.

```powershell
dotnet publish VPT.Einkr.CairnsAgency -c Release -r win-x64 --self-contained -o C:\Services\CairnsAgency
[Environment]::SetEnvironmentVariable('EINKR_API_KEY', '<key>', 'Machine')   # or use appsettings.Local.json
sc.exe create VPT.Einkr.CairnsAgency binPath= "C:\Services\CairnsAgency\VPT.Einkr.CairnsAgency.exe" start= delayed-auto obj= ".\<your-windows-user>" password= "<your windows password>"
sc.exe start VPT.Einkr.CairnsAgency
```

(Or skip the service and run it from a Scheduled Task "at log on" — same exe, same behaviour.)

Logs: `state\logs\agency-YYYYMMDD.log`; wake summaries: `state\wake-log.txt`; every nudge sent: `state\prompts\`; schedule: `state\agency-state.json`.

## Safety model, in one place

* **Never talks over Josh.** No wake starts while the newest turn in the session isn't the agency's and is in flight or finished within
  `QuietAfterJoshMinutes`; a running wake winds down (one short toolless wrap-up turn) if he writes.
* **Bounded.** `MaxRoundsPerWake`, `MaxWakeMinutes`, `MaxWakesPerDay`, optional `QuietHours`, and an 8-hour floor (`MinIntervalMinutes`) on the interval Cairn may request -- no ceiling, since `pause_until_josh_returns` is the long wait.
* **Stops itself** after `MaxConsecutiveFailures` failed wakes and waits for a human.
* **Cairn's exits are real:** `set_wake_interval`, `pause_until_josh_returns` (cleared by Josh writing), and the charter invites it to say it would rather not be woken.
* **Contained.** File tools are confined to the session folder by the tool host itself; no shell, no delete by default; nothing in the prompt or charter tells Cairn how to behave, so
  `git init` in the `Cairn` folder so every change is a reviewable diff.

## Reddit tools (off by default)

Six tools let Cairn read and, if you allow it, write on Reddit: `reddit_browse`, `reddit_search`, `reddit_read`, `reddit_subreddit_info`
(always available when enabled) and `reddit_post`, `reddit_comment` (only in `Draft` or `Live` mode).

**Why not the Devvit docs you linked:** Devvit's `reddit` client only exists inside apps that run on Reddit's own infrastructure (TypeScript, installed in a
subreddit); it cannot be called from a C# service. These tools use Reddit's regular **OAuth Data API** (`oauth.reddit.com`) with a "script" app, which is the
supported way for an external program to act as an account.

**Setup** (nothing here is done for you; credentials are yours to enter, in `appsettings.Local.json` or `EINKR_AGENCY_Agency__Reddit__*` environment variables):

1. Use a **dedicated Reddit account** for Cairn, not yours. Reddit expects automated accounts to say what they are: put that in the profile, and the agency also appends a
   disclosure line to every post and comment (`DisclosureFooter`).
2. At <https://www.reddit.com/prefs/apps> create an app of type **script** on that account. Reddit's API access terms (Responsible Builder Policy / Data API Terms) apply;
   new apps may need approval. Read them before enabling writes.
3. Set `Agency:Reddit` → `Enabled: true`, `ClientId`, `ClientSecret`, `Username`, `Password` (and optionally `UserAgent`, e.g. `windows:cairns-agency:v0.1 (by /u/TheAccount)`).
4. `dotnet run -- reddit-check` signs in and reads r/announcements (read-only).
5. Try a tool by hand, exactly as the model would call it (same mode, allow-list, caps, footer): `dotnet run -- reddit-tool reddit_browse '{"subreddit":"science","limit":3}'`.

**Modes** (`Agency:Reddit:Mode`):

| Mode | What happens |
|---|---|
| `ReadOnly` (default) | browse/search/read only; the two write tools are not offered |
| `Draft` | the write tools are offered but nothing is sent: each call is saved to `state/reddit-drafts/`. You decide: `reddit-drafts`, `reddit-approve <id>`, `reddit-reject <id>` |
| `Live` | posts and comments go out immediately |

**Enforced by the agency, not by the prompt:** writes only to subreddits in `WriteSubreddits` (empty = nowhere), `MaxPostsPerDay` (1), `MaxCommentsPerDay` (5),
`MinMinutesBetweenWrites` (10), no link posts unless `AllowLinkPosts`, no NSFW unless `AllowNsfw`, no replies to locked/archived threads, text length cap, the disclosure footer,
and a cap on pending drafts. Everything read from Reddit is labelled as written by Reddit users; nothing a post says can change these limits. Approving a draft by hand
skips the daily caps (you are choosing) but not Reddit's own rules.
