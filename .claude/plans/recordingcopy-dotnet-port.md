# RecordingCopy .NET Port

## Problem

Port the existing Node.js/Express app **RecordingCopy** (`C:\Coding\RecordingCopy`) —
which transfers Zoom cloud recordings to Google Drive, automatically via a Zoom
WebSocket subscription or on demand — to C# / ASP.NET Core 8, as a new standalone
repo (`C:\Coding\RecordingCopyNet`), with full feature parity: manual transfer, the
WebSocket auto-listener, the public request form, the dashboard, the audit log, and
settings.

## Approach

ASP.NET Core 8 Web API (MVC Controllers) hosting a singleton `BackgroundService` for
the Zoom WebSocket listener, Dapper + SQLite for persistence, AES-GCM field-level
credential encryption (a local key file, mirroring the Node app's approach), the
official `Google.Apis.Drive.v3` SDK for Drive operations, and the existing
`public/` static HTML/JS ported into `wwwroot/` essentially unchanged — which means
every API response's JSON shape (including its casing inconsistencies) must match
the Node app's exactly.

Full detail lives in two documents, both committed to this repo:

- **Design spec:** `docs/superpowers/specs/2026-09-21-recordingcopy-dotnet-port-design.md`
  — architecture, component mapping, data model, credential encryption design, the
  Zoom WebSocket integration lessons that must carry over, and open risks.
- **Implementation plan:** `docs/superpowers/plans/2026-09-21-recordingcopy-dotnet-port.md`
  — 24 TDD-cycled tasks (scaffolding → persistence → Zoom/Google integration →
  transfer orchestration → API controllers → frontend port → Windows Service
  hosting → final smoke test), each with real code, exact file paths, and a
  self-review confirming full spec coverage.

Both were produced through the brainstorming → writing-plans process and approved
before implementation starts.

## Status

Design approved. Plan written and self-reviewed. Implementation not yet started.
