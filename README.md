# RecordingCopyNet

ASP.NET Core 8 port of [RecordingCopy](https://github.com/) automates transferring
Zoom cloud recordings to Google Drive, either automatically (via a Zoom WebSocket
event subscription) or on demand (manual browse, or a public "request a transfer"
form).

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

The dashboard is available at `http://localhost:3901` by default. Configure Zoom
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

## Version

0.1.0
