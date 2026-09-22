# RecordingCopy .NET Port — Design

Date: 2026-09-21
Status: Approved by user, pending implementation plan

## 1. Purpose

Port the existing Node.js/Express app **RecordingCopy** (`C:\Coding\RecordingCopy`) to
C# / ASP.NET Core, as a new, separate repository at `C:\Coding\RecordingCopyNet`. The
original app automates transferring Zoom cloud recordings to Google Drive, either
automatically (via a Zoom WebSocket event subscription) or on demand (manual browse,
or a public "request a transfer" form). Full feature parity is the goal — this is a
1:1 rebuild in a different stack, not a redesign.

Source of truth for current behavior: the original repo's `server.js`, `config.js`,
`lib/`, `routes/api.js`, `public/`, and `docs/zoom-websocket-integration.md` (a
hard-won runbook of WebSocket integration pitfalls that must be preserved, not
rediscovered).

## 2. Decisions

These were confirmed with the user during brainstorming; each is a closed decision
for this design, not open for re-litigation during planning:

| Area | Decision |
|---|---|
| Scope | Full feature parity: manual transfer, WebSocket auto-listener, request system, dashboard, audit log, settings |
| API style | ASP.NET Core MVC Controllers (not Minimal APIs) |
| .NET version | .NET 8 LTS |
| Frontend | Reuse existing static HTML/JS/CSS from `public/` almost unchanged, served from `wwwroot/` |
| Data access | Dapper + raw SQL over `Microsoft.Data.Sqlite` (mirrors the current hand-written SQL, no EF Core) |
| Credential encryption | Custom AES-GCM service with a locally-generated key file, replicating the current `data/.key` approach (not DPAPI, not ASP.NET Core Data Protection) |
| Hosting | Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`), same executable also runnable as a plain console app for local dev |
| New repo location | `C:\Coding\RecordingCopyNet`, initialized as its own git repository |

## 3. Non-goals

- No new features beyond what the Node app has today.
- No change to the credential *schema* (same fields per type: zoom, google, settings).
- No change to the SQLite database file format/tables beyond what's needed to express
  the same schema in Dapper (columns, types, and semantics stay identical).
- No Blazor / Razor rewrite of the frontend — HTML/JS is ported, not reimagined.
- No multi-tenant or multi-account support — single Zoom account / single Drive
  target, same as today.

## 4. Architecture

ASP.NET Core 8 Web API using `IHost` (`Host.CreateDefaultBuilder` /
`WebApplication` builder configured to also support Windows Service mode via
`.UseWindowsService()`). Kestrel serves both the static frontend (`wwwroot/`) and the
`/api/*` controller routes, matching the current single-process Express app where one
server does both.

The Zoom WebSocket client becomes a singleton `BackgroundService`
(`ZoomWebSocketListener`), registered in DI and started automatically by the host —
the direct equivalent of `startWebSocket()` being called at the bottom of `server.js`
today. It owns its own reconnect loop, heartbeat timer, and in-memory dedup/queue
state for the lifetime of the process, exactly as the Node module-level singleton
does.

```
Browser (wwwroot static pages)
        │  fetch() / EventSource
        ▼
ASP.NET Core Kestrel
 ├─ Controllers (routes/api.js equivalent)
 ├─ ZoomWebSocketListener (BackgroundService, singleton)
 ├─ TransferService (orchestration)
 ├─ CredentialStore (AES-GCM over SQLite)
 └─ EventsRepository / RequestsRepository (Dapper)
        │
        ├──▶ Zoom REST + WebSocket (api.zoom.us / ws.zoom.us)
        └──▶ Google Drive API (Google.Apis.Drive.v3)
```

## 5. Project structure

```
RecordingCopyNet/
  RecordingCopyNet.sln
  src/RecordingCopyNet/
    Program.cs                    -- host builder, Windows Service registration, Kestrel port
    appsettings.json              -- port, Zoom API base URL, default date range, paths
    Controllers/
      CredentialsController.cs    -- GET/POST/DELETE /api/credentials/:type
      ZoomController.cs           -- /api/zoom/test, /api/zoom/recordings, /api/zoom/websocket/*
      GoogleController.cs         -- /api/google/test, /api/google/drives
      TransferController.cs       -- POST /api/transfer/:meetingId (SSE)
      EventsController.cs         -- /api/events, /api/events/:id, /api/events/stats
      RequestsController.cs       -- /api/requests, /api/requests/:id/transfer (SSE)
      SettingsController.cs       -- GET /api/settings
      VersionController.cs        -- GET /api/version
    Services/
      Zoom/
        ZoomAuthService.cs        -- OAuth token acquisition + in-memory cache (mirrors lib/zoom/auth.js)
        ZoomRecordingsService.cs  -- REST: list recordings, get meeting recordings, resolve user email
        ZoomDownloadService.cs    -- streamed file download to temp dir
        ZoomWebSocketListener.cs  -- BackgroundService: connect, heartbeat, reconnect, dedup, transfer queue
      Google/
        GoogleAuthService.cs      -- JWT service-account credential, optional domain-wide-delegation impersonation
        GoogleDriveService.cs     -- create folder, upload file, list shared drives, verify shared-drive/quota
      TransferService.cs          -- orchestration: fetch → verify target → download → create folder → upload → cleanup
      CredentialStore.cs          -- AES-GCM encrypt/decrypt, schema-driven like CREDENTIAL_SCHEMA in store.js
      SseWriter.cs                -- shared helper for writing text/event-stream frames + detecting disconnect
    Data/
      Db.cs                       -- connection factory, schema creation/migration (events, requests, credentials_* tables)
      EventsRepository.cs         -- mirrors lib/events.js
      RequestsRepository.cs       -- mirrors lib/requests.js
    Models/
      ZoomCredentials.cs, GoogleCredentials.cs, AppSettings.cs
      EventRecord.cs, RequestRecord.cs, TransferResult.cs
    wwwroot/
      index.html, dashboard.html, settings.html, request.html, audit.html
      js/ (api.js, app.js, dashboard.js, settings.js, request.js, audit.js — ported ~1:1)
      css/
  data/                            -- gitignored: app.db, .key, temp/
  docs/superpowers/specs/          -- this file
  .gitignore
  README.md
```

## 6. Component mapping (Node → C#)

| Node file | Responsibility | C# equivalent |
|---|---|---|
| `server.js` | boot Express, mount routes, start WS | `Program.cs` |
| `config.js` | constants | `appsettings.json` + strongly-typed `AppConfig` via `IOptions<AppConfig>` |
| `lib/store.js` | encrypted credential CRUD + schema | `Services/CredentialStore.cs` + `Data/Db.cs` |
| `lib/events.js` | events table CRUD | `Data/EventsRepository.cs` |
| `lib/requests.js` | requests table CRUD | `Data/RequestsRepository.cs` |
| `lib/transfer.js` | transfer orchestration | `Services/TransferService.cs` |
| `lib/zoom/auth.js` | OAuth token cache | `Services/Zoom/ZoomAuthService.cs` |
| `lib/zoom/recordings.js` | Zoom REST calls | `Services/Zoom/ZoomRecordingsService.cs` |
| `lib/zoom/download.js` | streamed file download | `Services/Zoom/ZoomDownloadService.cs` |
| `lib/zoom/websocket.js` | WS listener, heartbeat, reconnect, dedup, queue, SSE debug log | `Services/Zoom/ZoomWebSocketListener.cs` |
| `lib/google/auth.js` | JWT service-account client | `Services/Google/GoogleAuthService.cs` |
| `lib/google/drive.js` | Drive operations | `Services/Google/GoogleDriveService.cs` |
| `routes/api.js` | all HTTP endpoints | `Controllers/*.cs` (split by resource, same routes/behavior) |
| `public/*.html`, `public/js/*.js` | frontend | `wwwroot/*.html`, `wwwroot/js/*.js` (ported, `api.js` updated only if any route path changes) |

## 7. Data model

Same four logical tables as today, created/migrated on startup (mirrors
`migrateSchema()` in `store.js` — add-column-if-missing, no destructive migrations):

- `credentials_zoom` (account_id_encrypted, client_id_encrypted, client_secret_encrypted)
- `credentials_google` (client_email_encrypted, private_key_encrypted, project_id, client_id)
- `credentials_settings` (google_folder_id, google_impersonate_email, default_zoom_user, transfer_all_users, zoom_websocket_url)
- `events` (id, event_type, meeting_uuid, meeting_topic, host_email, received_at, raw_payload, status, skip_reason, transfer_started_at, transfer_completed_at, transfer_folder_name, transfer_folder_id, transfer_files_uploaded, transfer_error, transfer_logs)
- `requests` (id, name, surname, email, meeting_id, meeting_date, reason, status, created_at, transfer_error, transfer_folder_name, transfer_folder_id, transfer_files_uploaded)

Column-level encryption stays field-driven by a schema definition (a C# equivalent of
`CREDENTIAL_SCHEMA`), so which fields get AES-GCM applied is declarative, not
hardcoded per table.

## 8. Credential encryption

- On first run, `CredentialStore` generates a random 256-bit key, written to
  `data/.key` with restrictive ACLs (closest Windows equivalent of the current
  `mode: 0o600`).
- Each encrypted field is stored as `nonce (12 bytes) || ciphertext || tag`,
  base64-encoded in its `_encrypted` column, using `AesGcm` from
  `System.Security.Cryptography`.
- A fresh random nonce is generated per encryption call (never reused for a given
  key), matching standard AES-GCM guidance.
- Decrypt failures (corrupted data, wrong key) throw, surfaced as a 400 from the
  relevant controller, same as the Node app's behavior on a broken store.

## 9. Zoom integration

- `ZoomAuthService`: Server-to-Server OAuth (`account_credentials` grant), caches
  the token in memory with a 60-second expiry safety margin, cleared when
  credentials are updated — direct port of `lib/zoom/auth.js`.
- `ZoomRecordingsService` / `ZoomDownloadService`: same three REST endpoints
  (`GET /users/{userId}/recordings`, `GET /meetings/{meetingId}/recordings`,
  `GET /users/{userId}`), streamed download via `HttpClient` + `Stream.CopyToAsync`
  instead of Node's `pipeline(Readable.fromWeb(...))`.
- `ZoomWebSocketListener` (`BackgroundService`) must preserve every lesson from
  `docs/zoom-websocket-integration.md` in the original repo, translated to
  `ClientWebSocket`:
  - Token appended to the WS URL as a query parameter, not a header.
  - 30-second heartbeat (`{"module":"heartbeat"}`), started on connect, stopped on
    disconnect.
  - Double-encoded event envelope: `{"module":"message","content":"<json string>"}`
    must be unwrapped by parsing `content` as JSON.
  - `build_connection` replies must be branched on `success: true|false`, not on
    presence of `content` — a `success:false` reply can still arrive over an
    apparently-open socket.
  - Meeting UUIDs containing `/` or `//` must be double-`Uri.EscapeDataString`'d
    before use in a path segment.
  - TTL-based in-memory dedup map (`ConcurrentDictionary<string, DateTimeOffset>`,
    1-hour TTL), removing an entry on transfer failure so it can retry.
  - Bounded transfer concurrency (`SemaphoreSlim(3, 3)`), with "started at" stamped
    when a transfer actually begins, not at enqueue time, and status split into
    `queued` vs `transferring` so a backlog is visible rather than looking stalled.
  - Re-read the configured WebSocket URL from `CredentialStore` on every reconnect
    attempt (it may have changed or been cleared) rather than caching it.
  - On intentional stop: unsubscribe event handlers *before* closing, and treat a
    still-connecting socket differently from an open one — `ClientWebSocket` doesn't
    share Node's fatal-uncaught-exception failure mode, but the *sequencing* lesson
    (don't let a stale reconnect fire after a deliberate stop) still applies and
    must be preserved via a cancellation token passed through the connect loop.
  - In-memory ring buffer (last 100 entries) of debug log lines, broadcast to
    subscribed SSE clients exactly like the Node version's `_sseClients` set —
    implemented with a `ConcurrentDictionary<Guid, HttpResponse>`-style registry and
    the same snapshot-then-append protocol on the wire.

## 10. Google Drive integration

- `GoogleAuthService`: `ServiceAccountCredential` built from the stored
  `client_email` / `private_key`, scoped to `https://www.googleapis.com/auth/drive`,
  with `Subject` set to `google_impersonate_email` when configured (domain-wide
  delegation), using the official `Google.Apis.Drive.v3` + `Google.Apis.Auth`
  NuGet packages.
- `GoogleDriveService`: `Files.Create` (folder + file upload with
  `SupportsAllDrives = true`), `Drives.List`, `Files.List` for folder contents,
  `Files.Get` for the shared-drive verification check — same four operations as
  `lib/google/drive.js`.

## 11. Transfer orchestration

`TransferService.TransferMeetingAsync(meetingId, IProgress<string> onProgress)`
follows the exact sequence of `lib/transfer.js`:

1. Load settings; require `google_folder_id`.
2. Fetch meeting recording metadata from Zoom; require at least one file.
3. Verify the target folder has usable storage quota: either it's on a Shared
   Drive, or an impersonation email is configured — otherwise throw with the same
   explanatory message (service-account-has-zero-quota is a real Google Drive API
   trap and the error text is what makes it debuggable).
4. Build the dated folder name (`sanitizeFolderName($"{startDate} - {topic}")`),
   same character-stripping rules (`<>:"/\|?*`, max 200 chars).
5. Create a temp working directory, write `metadata.json` (same shape as today).
6. Download each recording file, streaming to disk.
7. Create the Drive subfolder, upload `metadata.json` then each recording file with
   the same MIME-type map (`MP4→video/mp4`, `M4A→audio/mp4`, `CHAT→text/plain`,
   `TRANSCRIPT→text/vtt`, `TIMELINE→application/json`, `CC→text/vtt`,
   `CSV→text/csv`, default `application/octet-stream`).
8. Delete the temp directory.
9. Return `{ FolderName, FolderId, FilesUploaded }`.

Progress messages are reported via the same `IProgress<string>` callback pattern
Node uses with its `onProgress` closure, consumed identically by both the manual
SSE transfer endpoint and the WebSocket auto-transfer path.

## 12. HTTP API (must match routes/api.js 1:1)

`GET /api/version` · `GET/POST/DELETE /api/credentials/:type` ·
`POST /api/zoom/test` · `GET /api/zoom/recordings` ·
`POST /api/google/test` · `GET /api/google/drives` ·
`GET /api/zoom/websocket/status` · `GET /api/zoom/websocket/stream` (SSE) ·
`GET /api/zoom/websocket/subscribers` · `POST /api/zoom/websocket/{start,stop,restart}` ·
`GET /api/settings` · `POST /api/transfer/:meetingId` (SSE) ·
`GET /api/events`, `GET /api/events/:id`, `GET /api/events/stats` ·
`POST /api/requests`, `GET /api/requests`, `POST /api/requests/:id/transfer` (SSE)

Same status codes and JSON error shapes (`{ error: message }` /
`{ ok: false, error: message }`) as the Node responses, so the ported frontend JS
needs no changes to its fetch/error-handling logic.

## 13. Frontend porting notes

`public/` → `wwwroot/`, files copied with minimal changes:

- `api.js` — only touched if any endpoint path or response shape differs (none are
  expected to).
- SSE consumption (`EventSource`) is unchanged — ASP.NET Core will emit the same
  `event: <name>\ndata: <json>\n\n` wire format.
- Existing XSS-safety note from the original doc — render log lines via
  `textContent`, never `innerHTML` — carries over unchanged since it's the same
  HTML/JS.

## 14. Error handling

- Controllers return the same status codes/shapes as the Express routes (400 for
  validation/config errors, 404 for missing events/requests, 500 for unexpected
  failures), so the frontend's existing error handling needs no changes.
- The WebSocket listener logs and reconnects on any connection-level failure; it
  never lets an exception escape the `BackgroundService.ExecuteAsync` loop
  unhandled (an unobserved exception there stops the service silently, which is
  its own failure mode to guard against — wrap the loop body in try/catch per
  reconnect attempt, matching the Node version's per-callback error handling).
- Transfer failures update the `events`/`requests` row with `status = 'failed'`
  and the error message, and (for the WebSocket path) remove the UUID from the
  dedup map so a retry is possible — same as today.

## 15. Testing strategy

No test suite exists in the Node app; parity doesn't require one, but the port adds:

- xUnit unit tests for `TransferService`, `ZoomAuthService`, `GoogleDriveService`
  against a mocked `HttpMessageHandler` (no live credentials needed) — covers the
  shared-drive/impersonation-quota guard, MIME mapping, folder-name sanitization,
  and UUID double-encoding logic.
- xUnit tests for `CredentialStore` round-tripping encrypt/decrypt.
- Manual end-to-end smoke test against real Zoom/Google sandbox credentials via the
  Settings UI before any phase is marked complete, per the project's verification
  rule.

## 16. Deployment

Single executable, runnable two ways from the same `Program.cs`:

- `dotnet run` / a built exe for local development (Kestrel binds the configured
  port directly, same as `npm start` today).
- Installed as a Windows Service (`sc.exe create` or `New-Service`, using
  `.UseWindowsService()`) for always-on production use.

## 17. Open risks / follow-ups for planning

- `Google.Apis.Drive.v3` and `Google.Apis.Auth` version compatibility with .NET 8
  should be pinned during implementation, not assumed.
- Windows ACL-based file protection for `data/.key` needs a concrete
  implementation (`FileSystemAccessRule`) since there's no direct .NET equivalent
  of POSIX `mode: 0o600` — functionally equivalent restriction (owner-only) is the
  goal.
- Exact SSE keep-alive/comment-frame timing (30s `: ping`) and Kestrel response
  buffering settings need verification under ASP.NET Core (equivalent of Node's
  `X-Accel-Buffering: no` header) to avoid proxy buffering surprises if ever
  deployed behind IIS/reverse proxy.
