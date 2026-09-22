# RecordingCopyNet

ASP.NET Core 8 port of [RecordingCopy](https://github.com/) — automates transferring
Zoom cloud recordings to Google Drive, either automatically (via a Zoom WebSocket
event subscription) or on demand (manual browse, or a public "request a transfer"
form).

Full design: `docs/superpowers/specs/2026-09-21-recordingcopy-dotnet-port-design.md`
Implementation plan: `docs/superpowers/plans/2026-09-21-recordingcopy-dotnet-port.md`

## Tech Stack

- ASP.NET Core 8 (MVC Controllers)
- Dapper + SQLite
- Google.Apis.Drive.v3 / Google.Apis.Auth
- System.Net.WebSockets.ClientWebSocket

## Getting Started

```bash
dotnet restore
dotnet test tests/RecordingCopyNet.Tests
dotnet run --project src/RecordingCopyNet
```

The dashboard is available at `http://localhost:3900` by default. Configure Zoom
OAuth2 and Google Drive API credentials on the Settings page before use.

To run on a different port (e.g. because 3900 is already in use), override the
config via environment variable instead of editing `appsettings.json`:

```bash
AppConfig__Port=3901 dotnet run --project src/RecordingCopyNet
```

## Running as a Windows Service

(requires an elevated PowerShell prompt)

    dotnet publish src/RecordingCopyNet -c Release -o publish
    sc.exe create RecordingCopyNet binPath= "C:\path\to\publish\RecordingCopyNet.exe"
    sc.exe start RecordingCopyNet

To remove it:

    sc.exe stop RecordingCopyNet
    sc.exe delete RecordingCopyNet

## Verification status

- **Automated tests:** `dotnet test` — 107/107 passing (full suite, all 23 prior
  implementation tasks combined). See
  `.superpowers/sdd/2026-09-21-recordingcopy-dotnet-port/task-24-report.md` for the
  exact run output.
- **Manual smoke test:** the app itself, its 5 static pages, settings persistence,
  and the WebSocket status endpoint (no URL configured) were verified directly in
  this environment. The steps that require live Zoom Server-to-Server OAuth
  credentials and a real Google service account with Shared Drive or
  Domain-Wide Delegation access (real "Test Connection" calls, a live WebSocket
  `Connected to Zoom` handshake, an actual recording transfer landing in Drive, and
  a real `recording.completed` webhook) were **not verified in this session** —
  those need real credentials in a real Zoom/Google environment and are called out
  explicitly, item by item, in the task-24 report rather than assumed to pass.

## Version

0.1.0
