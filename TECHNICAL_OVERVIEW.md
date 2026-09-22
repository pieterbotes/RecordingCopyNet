# RecordingCopyNet — Technical Overview

ASP.NET Core 8 port of [RecordingCopy](https://github.com/) (a Node.js/Express app):
automates transferring Zoom cloud recordings to Google Drive, either automatically
via a Zoom WebSocket event subscription or on demand (manual browse, or a public
"request a transfer" form).

This document is for a developer picking up the codebase — it explains the
architecture, where things live, the non-obvious design decisions, and what's known
to still need attention. For setup/run instructions see `README.md`.

## Origin

This is a from-scratch C# rewrite of an existing Node app, built to match its
behavior exactly — same routes, same JSON response shapes (including the original's
inconsistent casing per endpoint), same business logic, same WebSocket integration
quirks. It was built via a 24-task implementation plan plus a final whole-branch
review/fix pass; the full design rationale lives in:

- `docs/superpowers/specs/2026-09-21-recordingcopy-dotnet-port-design.md` — architecture decisions
- `docs/superpowers/plans/2026-09-21-recordingcopy-dotnet-port.md` — the task-by-task build plan
- `todo.md` — task checklist + a "Review" section summarizing what the final review caught and fixed

## Tech stack

- ASP.NET Core 8, MVC Controllers (not Minimal APIs)
- SQLite via `Microsoft.Data.Sqlite` + Dapper (no EF Core)
- AES-GCM field-level credential encryption (custom, not DPAPI/ASP.NET Data Protection)
- `Google.Apis.Drive.v3` / `Google.Apis.Auth` for Drive integration
- `System.Net.WebSockets.ClientWebSocket` for the Zoom event subscription
- xUnit, 121 tests

## Project layout

```
src/RecordingCopyNet/
  Program.cs                    — host setup, DI wiring, Kestrel port binding, Windows Service support
  Config/                       — AppConfig (bound from appsettings.json), AppVersion
  Controllers/                  — 7 MVC controllers, one per resource (see "HTTP API" below)
  Data/                         — SQLite access: Db (schema/connections), CredentialStore,
                                   EventsRepository, RequestsRepository — all Dapper + raw SQL
  Security/                     — AesGcmFieldCipher, FileEncryptionKeyProvider
  Services/
    TransferService.cs          — the core orchestration: Zoom → temp dir → Google Drive
    SseWriter.cs / SseBroadcastHub.cs — Server-Sent Events plumbing shared by 3 endpoints
    Google/                     — GoogleAuthService (JWT/service-account), GoogleDriveService
    Zoom/
      ZoomAuthService.cs        — OAuth token cache (Server-to-Server grant)
      ZoomRecordingsService.cs  — REST calls (list/get recordings, resolve user email)
      ZoomDownloadService.cs    — streamed file download
      ZoomWebSocketListener.cs  — the big one: BackgroundService managing the live
                                   WebSocket connection (see below)
      ZoomWsMessageRouter.cs    — pure message classifier (no I/O), used by the listener
      EventDedupTracker.cs      — TTL-based dedup for recording.completed events
      TransferQueue.cs          — bounded-concurrency queue (max 3 concurrent transfers)
  Models/                       — DTOs and API response records
  wwwroot/                      — static frontend (HTML/CSS/JS), copied byte-for-byte
                                   from the original Node app's public/ directory

tests/RecordingCopyNet.Tests/   — mirrors the src/ structure 1:1
```

## HTTP API

| Controller | Routes |
|---|---|
| `VersionController` | `GET /api/version` |
| `CredentialsController` | `GET/POST/DELETE /api/credentials/{type}` (type = zoom/google/settings) |
| `SettingsController` | `GET /api/settings` |
| `ZoomController` | `POST /api/zoom/test`, `GET /api/zoom/recordings`, `GET /api/zoom/websocket/status`, `GET /api/zoom/websocket/stream` (SSE), `GET /api/zoom/websocket/subscribers`, `POST /api/zoom/websocket/{start,stop,restart}` |
| `GoogleController` | `POST /api/google/test`, `GET /api/google/drives` |
| `TransferController` | `POST /api/transfer/{meetingId}` (SSE progress stream) |
| `EventsController` | `GET /api/events`, `GET /api/events/{id}`, `GET /api/events/stats` |
| `RequestsController` | `POST /api/requests`, `GET /api/requests`, `POST /api/requests/{id}/transfer` (SSE) |

**JSON casing is intentionally inconsistent** — this mirrors the original Node app,
which the frontend JS (`wwwroot/js/*.js`) was built against and still expects exactly:
DB-row responses (events, requests) are snake_case; service-result responses
(`TransferResult`, `GoogleTestResponse`, WebSocket debug info) are camelCase. The
global `PropertyNamingPolicy` is disabled in `Program.cs`, so **every** model that
crosses an HTTP boundary carries explicit `[JsonPropertyName]` attributes — don't
add a new response type without checking what casing it needs.

There's also a deliberately-preserved asymmetry: `POST /api/zoom/websocket/stop` and
`.../start` return a bare JSON string (`"connected"`), while `.../restart` returns
`{"status":"connected"}`. Not a bug — the original app does this too, and the
frontend depends on it.

## The Zoom WebSocket listener

`Services/Zoom/ZoomWebSocketListener.cs` is the most complex file in the codebase — a
singleton `BackgroundService` that:

1. Connects to Zoom's WebSocket event subscription on host startup (if a URL is configured).
2. Sends a heartbeat every 30s, reconnects with a 5s backoff on disconnect.
3. On `recording.completed`: dedupes by UUID (1hr TTL), filters by configured user
   (unless "all users" mode), then enqueues a transfer via `TransferQueue` (max 3 concurrent).
4. Exposes `IZoomWebSocketController` (Start/Stop/RestartConnectionAsync, status, debug
   info) for the Settings page's connection controls and live debug log (SSE).

It's registered three ways in `Program.cs` — as a singleton class, as the hosted
service, and as `IZoomWebSocketController` — all resolving to the **same instance**,
so the host lifecycle and the manual start/stop controls share state correctly.

This file went through the most review scrutiny in the whole build (3 fix rounds
just within its own task, plus more in the final whole-branch pass) because it's
genuinely concurrent: a `SemaphoreSlim`-based lifecycle lock (`_lifecycleLock`)
serializes `StartConnectionAsync`/`StopConnectionAsync`, and a generation counter
(`_generation`) invalidates stale reconnect attempts after an intentional stop. If
you're modifying connection lifecycle logic here, read that locking carefully before
changing it — it's correct but not obvious, and it's already been wrong twice during
development (see `todo.md`'s Review section for specifics).

The message-handling logic (`internal Task HandleRawMessageAsync(...)`) is
deliberately `internal` + `InternalsVisibleTo` so it's unit-testable without a real
socket — the actual socket I/O (connect/receive/heartbeat loops) isn't unit tested,
only exercised via manual smoke testing.

## Credential storage

`Data/CredentialStore.cs` stores Zoom/Google credentials and app settings as
single-row tables (`credentials_zoom`, `credentials_google`, `credentials_settings`),
with sensitive fields AES-GCM encrypted using a key generated on first run
(`data/.key`, restricted to the current user via Windows ACLs). The schema
(field names, which are encrypted, which are required) is declared once in
`Data/CredentialSchema.cs` and driven off that everywhere — adding a new credential
field means editing that file, not hand-writing SQL elsewhere.

`Save()` distinguishes three states for each field: **absent** from the request body
(leave the stored value untouched — needed for partial credential rotation), versus
**present with an explicit `null`** (clear that field), versus **present with a
value** (update it). Getting this distinction wrong was a real bug caught in the
final review — the Settings page always sends a full record and relies on explicit
`null` to mean "clear this," so conflating absent-vs-null breaks the UI silently.

## Transfer orchestration

`Services/TransferService.cs` is the actual business logic, independent of whether
the transfer was triggered by the WebSocket listener, a manual dashboard click, or a
processed request: fetch meeting metadata from Zoom → verify the target Drive folder
has real storage quota (must be on a Shared Drive, or a domain-wide-delegation
impersonation email must be configured — a bare service account has zero quota) →
download recording files to a temp dir → create a dated Drive subfolder → upload
`metadata.json` + each file with the right MIME type → clean up.

It reports progress via a synchronous `Action<string>?` callback, which the three SSE
controllers (`TransferController`, `RequestsController`, and the WebSocket listener)
each turn into either an SSE frame or a DB log entry.

## Testing approach

- Everything except two things has real unit test coverage: `GoogleAuthService` /
  `GoogleDriveService` are thin adapters over the Google SDK with no automated tests
  (a deliberate call — faking that SDK's HTTP transport safely was judged not worth
  the risk vs. reward; see the design spec). Correctness there depends on manual
  testing against real Google credentials.
- `ZoomController.cs` has one real HTTP integration test
  (`ZoomControllerIntegrationTests.cs`, using `WebApplicationFactory<Program>`) that
  asserts actual `Content-Type` and response body — added after a bug slipped through
  because every other controller test only asserted at the `ActionResult` level,
  which can't catch a content-negotiation issue. Worth following this pattern for any
  new endpoint where the exact wire format matters.

## Known gaps / follow-ups (not blocking, but real)

From the final whole-branch review — nothing here is an active bug in normal use,
but worth knowing about:

- **Not tested against real Zoom/Google APIs.** No live credentials were available
  during development. Core auth/error-handling paths were exercised against Zoom's
  real OAuth endpoint (with fake credentials, correctly rejected), but a full
  end-to-end transfer has never actually run.
- `ZoomRecordingsService`/`ZoomDownloadService`'s `HttpClient` never rotates —
  fine for now, but over a months-long Windows Service run this won't pick up DNS
  changes for `api.zoom.us`. Would need an `IHttpClientFactory`-based redesign.
- `GoogleDriveService` and the Zoom meeting-UUID double-encoding path
  (`/`-prefixed UUIDs) have no automated test coverage.
- A non-numeric `AppConfig__Port` environment override still crashes startup via the
  Options-pattern binding in `Db`/`ZoomAuthService`, even though the Kestrel binding
  itself was hardened against it. Cheap fix if it ever matters (~2 lines in `Program.cs`).

Full detail on all of these, plus a handful of lower-priority items, is in
`todo.md`'s "Review" section.

## Where to start reading

1. `Program.cs` — see the whole DI graph in one place.
2. `Services/TransferService.cs` — the actual thing this app does.
3. `Services/Zoom/ZoomWebSocketListener.cs` — the automatic-transfer trigger, and the
   most carefully-built file in the codebase.
4. Any `Controllers/*.cs` alongside its matching `Data/*Repository.cs` or
   `Services/*.cs` — the pattern is consistent across all 7 controllers.
