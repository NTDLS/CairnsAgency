# CairnsAgency

A small .NET service that wakes a chat session of the [Einkr](https://einkr.com) desktop assistant on a schedule, runs a turn the way
the Einkr desktop app would if it could act unprompted, and records it so it reads in the Einkr transcript like any other turn. Built
for one specific purpose: giving a long-lived AI companion ("Cairn") time of its own while its human is away.

It also includes optional Reddit tools (browse, search, read, and — behind several guards — post and comment) over Reddit's OAuth Data
API.

> **This repository is not self-contained.** It talks to an Einkr API (`/api/desktop/*`) with an Einkr API key, and it launches the
> Einkr tool host executable (`VPT.Einkr.Desktop.ToolHost.exe`) for file tools. Both come from the Einkr project. Without them it will
> build and its tests will run (they use a fake API), but it cannot do anything useful.

## Safe by default

- `Agency:DryRun` is **true**: the service builds everything and sends nothing.
- Shell access and `delete_path` are off.
- Reddit is off; when enabled it starts read-only, and writing needs an explicit allow-list of subreddits, daily caps, a disclosure
  footer, and (in `Draft` mode) a human approving each draft.
- It never starts a wake while the human is in the session, and a running wake winds down if they arrive.

## Layout

| Folder | What it is |
|---|---|
| `VPT.Einkr.CairnsAgency/` | the service and CLI — see its [README](VPT.Einkr.CairnsAgency/README.md) for configuration, commands and install |
| `VPT.Einkr.CairnsAgency.Tests/` | xunit tests: parity with the desktop app's own JavaScript (golden files), whole wakes against a fake API with the real tool host, the scheduler, and the Reddit tools against a fake Reddit |

## Build and test

```
dotnet build CairnsAgency.slnx
dotnet test  CairnsAgency.slnx
```

Tests that need the real tool host skip themselves when it is not present at the configured `Agency:Tools:ToolHostPath`.

## Configuration

Put your own values in `VPT.Einkr.CairnsAgency/appsettings.Local.json` (git-ignored) or `EINKR_AGENCY_*` environment variables, and
the API key in `EINKR_API_KEY`. At minimum: `Agency:Session:SessionId`. Nothing personal or secret is committed here.

## License

No license has been chosen yet; until one is added, all rights are reserved by the author.
