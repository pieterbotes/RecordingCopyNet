using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/websocket.js — see spec §9. Connection I/O
// (ConnectLoopAsync, TryConnectOnceAsync, ReceiveLoopAsync, heartbeat) is
// glue that's only meaningfully verified by the manual smoke test in
// Task 25; the message-handling pipeline (HandleRawMessageAsync and
// everything it calls) is `internal` specifically so the tests above can
// exercise dedup/filtering/queueing logic without a real socket.
public class ZoomWebSocketListener : BackgroundService, IZoomWebSocketController
{
    private const int MaxDebugLog = 100;
    private const int HeartbeatIntervalMs = 30_000;
    private const int ReconnectDelayMs = 5_000;

    private readonly IZoomAuthService _zoomAuth;
    private readonly IZoomRecordingsService _zoomRecordings;
    private readonly ITransferService _transferService;
    private readonly ICredentialStore _credentialStore;
    private readonly IEventsRepository _eventsRepo;
    private readonly ZoomWsMessageRouter _router;
    private readonly EventDedupTracker _dedup;
    private readonly TransferQueue _transferQueue;
    private readonly SseBroadcastHub _sseHub;
    private readonly Microsoft.Extensions.Logging.ILogger<ZoomWebSocketListener> _logger;

    private readonly object _stateLock = new();
    private readonly List<DebugLogEntry> _debugLog = new();
    private string _status = "disconnected";
    private DateTimeOffset? _connectedAt;
    private int _messages, _heartbeats, _events, _errors;
    private long _generation;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _connectionCts;

    // Async-safe mutex serializing Start/Stop/Restart against each other. A plain `lock`
    // can't be held across an `await` (which both the socket-close in StopConnectionAsync
    // and awaiting the connect loop's completion need), so a SemaphoreSlim(1,1) stands in
    // for one. Without this, two truly concurrent Start calls (e.g. two overlapping
    // POST /api/zoom/websocket/start requests) could each pass a plain field
    // read/cancel/dispose of _connectionCts and race on installing the new one, silently
    // orphaning a live connection loop + socket.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Task? _connectionLoopTask;

    public ZoomWebSocketListener(
        IZoomAuthService zoomAuth, IZoomRecordingsService zoomRecordings, ITransferService transferService,
        ICredentialStore credentialStore, IEventsRepository eventsRepo, ZoomWsMessageRouter router,
        EventDedupTracker dedup, TransferQueue transferQueue, SseBroadcastHub sseHub,
        Microsoft.Extensions.Logging.ILogger<ZoomWebSocketListener> logger)
    {
        _zoomAuth = zoomAuth;
        _zoomRecordings = zoomRecordings;
        _transferService = transferService;
        _credentialStore = credentialStore;
        _eventsRepo = eventsRepo;
        _router = router;
        _dedup = dedup;
        _transferQueue = transferQueue;
        _sseHub = sseHub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartConnectionAsync();
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* host shutdown */ }
        await StopConnectionAsync();
    }

    // ---- IZoomWebSocketController ----

    public string GetStatus() { lock (_stateLock) return _status; }

    public ZoomWsDebugInfo GetDebugInfo()
    {
        lock (_stateLock)
        {
            var (active, queued, limit) = _transferQueue.GetQueueInfo();
            return new ZoomWsDebugInfo(
                _status, _connectedAt?.ToString("o"),
                new ZoomWsCounters(_messages, _heartbeats, _events, _errors),
                new ZoomWsQueueInfo(active, queued, limit),
                _debugLog.TakeLast(50).ToList());
        }
    }

    public IDisposable SubscribeDebugLog(Func<string, Task> writer) => _sseHub.Subscribe(writer);

    public int SseSubscriberCount => _sseHub.SubscriberCount;

    // Test seam only (see HandleRawMessageAsync's header comment for the pattern):
    // lets tests confirm StartConnectionAsync's idempotency guard actually swaps in a
    // fresh CancellationTokenSource and cancels+disposes the previous one, without
    // needing a real socket. Reads without the lock are fine for tests, which only ever
    // read this after their own Start/Stop calls have completed.
    internal CancellationTokenSource? ConnectionCtsForTests => _connectionCts;

    public async Task StartConnectionAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            // Idempotency guard: cancel any previous connection attempt/loop and wait for it
            // to actually finish before disposing its CTS and starting a new one — so
            // calling this twice, even from two truly concurrent callers, can never leave
            // two live ConnectLoopAsync chains running. Mirrors StopConnectionAsync's
            // cancel-await-dispose ordering: disposing immediately after Cancel() risks an
            // unobserved ObjectDisposedException inside the old loop's Task.Delay(ct). A
            // plain read (no Interlocked) is safe here because _lifecycleLock serializes
            // all Start/Stop access to _connectionCts/_connectionLoopTask.
            if (_connectionCts != null)
            {
                _connectionCts.Cancel();

                if (_connectionLoopTask != null)
                {
                    try { await _connectionLoopTask; }
                    catch { /* the loop already handles and logs its own errors */ }
                }

                _connectionCts.Dispose();
                _connectionCts = null;
                _connectionLoopTask = null;
            }

            IReadOnlyDictionary<string, string?>? settings;
            try
            {
                settings = _credentialStore.Load(CredentialType.Settings);
            }
            catch (Exception ex)
            {
                Debug($"Failed to read settings: {ex.Message}");
                SetStatus("disconnected");
                return;
            }

            var wsUrl = settings != null && settings.TryGetValue("zoom_websocket_url", out var url) ? url : null;
            if (string.IsNullOrEmpty(wsUrl))
            {
                Debug("No WebSocket URL configured, skipping");
                SetStatus("disconnected");
                return;
            }

            var generation = Interlocked.Increment(ref _generation);
            _connectionCts = new CancellationTokenSource();
            _connectionLoopTask = ConnectLoopAsync(wsUrl, generation, _connectionCts.Token); // errors are handled inside the loop
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopConnectionAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            Interlocked.Increment(ref _generation); // invalidates any in-flight reconnect attempt
            _connectionCts?.Cancel();

            var ws = _ws;
            _ws = null;
            if (ws != null)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
                    else if (ws.State == WebSocketState.Connecting)
                        ws.Abort(); // .NET analogue of ws.terminate() for a socket still mid-handshake (spec §9, docs §10)
                }
                catch { /* best-effort teardown */ }
                finally { ws.Dispose(); }
            }

            // Wait for the loop to actually observe cancellation and return before
            // disposing its CTS — disposing while the loop might still be touching the
            // token (e.g. inside Task.Delay(ct)) risks an unobserved ObjectDisposedException
            // that would kill the loop silently instead of via its normal, logged paths.
            if (_connectionLoopTask != null)
            {
                try { await _connectionLoopTask; }
                catch { /* the loop already handles and logs its own errors */ }
            }

            _connectionCts?.Dispose();
            _connectionCts = null;
            _connectionLoopTask = null;

            SetStatus("disconnected");
            _connectedAt = null;
            Debug("Stopped");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartConnectionAsync()
    {
        await StopConnectionAsync();
        await StartConnectionAsync();
    }

    // ---- Connection loop ----

    private async Task ConnectLoopAsync(string wsUrl, long generation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Interlocked.Read(ref _generation) == generation)
        {
            await TryConnectOnceAsync(wsUrl, generation, ct);
            if (ct.IsCancellationRequested || Interlocked.Read(ref _generation) != generation) return;

            Debug("Reconnecting in 5s...");
            try { await Task.Delay(ReconnectDelayMs, ct); }
            catch (OperationCanceledException) { return; }

            if (Interlocked.Read(ref _generation) != generation) return;

            var settings = _credentialStore.Load(CredentialType.Settings);
            var currentUrl = settings != null && settings.TryGetValue("zoom_websocket_url", out var url) ? url : null;
            if (string.IsNullOrEmpty(currentUrl))
            {
                Debug("WebSocket URL removed, not reconnecting");
                SetStatus("disconnected");
                return;
            }
            wsUrl = currentUrl;
        }
    }

    private async Task TryConnectOnceAsync(string wsUrl, long generation, CancellationToken ct)
    {
        SetStatus("connecting");
        Debug("Connecting...");

        string token;
        try { token = await _zoomAuth.GetAccessTokenAsync(ct); }
        catch (Exception ex)
        {
            Debug($"Failed to get access token: {ex.Message}");
            SetStatus("disconnected");
            return;
        }

        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"{wsUrl}&access_token={token}"), ct);
        }
        catch (Exception ex)
        {
            Debug($"Error: {ex.Message}");
            SetStatus("disconnected");
            ws.Dispose();
            return;
        }

        if (Interlocked.Read(ref _generation) != generation) { ws.Dispose(); return; } // StopConnectionAsync fired mid-handshake

        _ws = ws;
        _connectedAt = DateTimeOffset.UtcNow;
        lock (_stateLock) { _messages = 0; _heartbeats = 0; _events = 0; _errors = 0; }
        SetStatus("connected");
        Debug("Connected to Zoom");

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(ws, heartbeatCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, ct);
        }
        catch (Exception ex)
        {
            lock (_stateLock) _errors++;
            Debug($"Error: {ex.Message}");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { /* heartbeat loop only throws OperationCanceledException, swallowed there */ }
        }

        Debug("Disconnected");
        SetStatus("disconnected");
        _connectedAt = null;
        if (ReferenceEquals(_ws, ws)) _ws = null;
        ws.Dispose();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var raw = Encoding.UTF8.GetString(messageStream.ToArray());
            lock (_stateLock) _messages++;
            await HandleRawMessageAsync(raw, ct);
        }
    }

    private async Task RunHeartbeatAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HeartbeatIntervalMs));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (ws.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes("""{"module":"heartbeat"}""");
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- Message handling (testable without a real socket) ----

    internal async Task HandleRawMessageAsync(string rawJson, CancellationToken ct)
    {
        var routed = _router.Route(rawJson);

        switch (routed.Kind)
        {
            case ZoomWsMessageKind.Heartbeat:
                lock (_stateLock) _heartbeats++;
                Debug("Heartbeat ack");
                return;

            case ZoomWsMessageKind.BuildConnectionSuccess:
                Debug($"build_connection: success — raw: {routed.RawExcerpt}");
                return;

            case ZoomWsMessageKind.BuildConnectionFailure:
                Debug($"build_connection: failure — raw: {routed.RawExcerpt}");
                return;

            case ZoomWsMessageKind.ParseError:
                lock (_stateLock) _errors++;
                Debug($"Failed to parse message — raw: {routed.RawExcerpt}");
                return;

            case ZoomWsMessageKind.Ignored:
                Debug($"Ignored event: {routed.EventName ?? "unknown"}");
                return;

            case ZoomWsMessageKind.RecordingCompleted:
                lock (_stateLock) _events++;
                await HandleRecordingCompletedAsync(routed.Payload!.Value, ct);
                return;
        }
    }

    private async Task HandleRecordingCompletedAsync(JsonElement msg, CancellationToken ct)
    {
        if (!msg.TryGetProperty("payload", out var payloadWrapper) || !payloadWrapper.TryGetProperty("object", out var payload))
        {
            Debug("recording.completed with no payload object");
            return;
        }

        var uuid = payload.GetProperty("uuid").GetString()!;
        var hostEmail = payload.TryGetProperty("host_email", out var hostEmailEl) ? hostEmailEl.GetString() : null;
        var topic = payload.TryGetProperty("topic", out var topicEl) ? topicEl.GetString() : null;

        Debug($"Received recording.completed for meeting \"{topic}\" (uuid: {uuid})");

        var eventId = _eventsRepo.LogEvent("recording.completed", uuid, topic, hostEmail, msg.GetRawText());

        if (!_dedup.TryMarkProcessed(uuid))
        {
            Debug($"Already processed {uuid}, skipping");
            _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "skipped", SkipReason = "duplicate" });
            return;
        }

        var settings = _credentialStore.Load(CredentialType.Settings);
        var allUsers = settings != null && settings.TryGetValue("transfer_all_users", out var au) && au == "true";
        var configuredUser = settings != null && settings.TryGetValue("default_zoom_user", out var cu) ? cu : null;

        if (allUsers)
        {
            Debug($"All-users mode: accepting recording from {hostEmail ?? "unknown host"}");
        }
        else if (!string.IsNullOrEmpty(configuredUser))
        {
            var resolvedEmail = hostEmail;
            if (string.IsNullOrEmpty(resolvedEmail) && payload.TryGetProperty("host_id", out var hostIdEl))
                resolvedEmail = await _zoomRecordings.GetUserEmailAsync(hostIdEl.GetString()!, ct);

            if (!string.IsNullOrEmpty(resolvedEmail) && !resolvedEmail.Equals(configuredUser, StringComparison.OrdinalIgnoreCase))
            {
                Debug($"Ignoring recording from {resolvedEmail} (configured user: {configuredUser})");
                _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "skipped", SkipReason = $"wrong user: {resolvedEmail}" });
                return;
            }
        }

        var meetingId = uuid;
        if (meetingId.StartsWith('/') || meetingId.Contains("//"))
            meetingId = Uri.EscapeDataString(Uri.EscapeDataString(meetingId));

        void EventLogger(string message)
        {
            Debug(message);
            _eventsRepo.AppendLog(eventId, message);
        }

        var (active, queued, limit) = _transferQueue.GetQueueInfo();
        if (active >= limit)
        {
            Debug($"Queued auto-transfer for \"{topic}\" (position {queued + 1}, {active} active)");
            _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "queued" });
            _eventsRepo.AppendLog(eventId, $"Queued — {active} transfer(s) already running, position {queued + 1} in queue");
        }

        _transferQueue.Enqueue(async () =>
        {
            Debug($"Starting auto-transfer for \"{topic}\"");
            _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "transferring", TransferStartedAt = DateTimeOffset.UtcNow.ToString("o") });

            try
            {
                var result = await _transferService.TransferMeetingAsync(meetingId, EventLogger, CancellationToken.None);
                Debug($"Transfer complete: {result.FilesUploaded} files to \"{result.FolderName}\"");
                _eventsRepo.UpdateEvent(eventId, new EventUpdateFields
                {
                    Status = "completed",
                    TransferCompletedAt = DateTimeOffset.UtcNow.ToString("o"),
                    TransferFolderName = result.FolderName,
                    TransferFolderId = result.FolderId,
                    TransferFilesUploaded = result.FilesUploaded,
                });
            }
            catch (Exception ex)
            {
                Debug($"Transfer failed for {uuid}: {ex.Message}");
                _eventsRepo.UpdateEvent(eventId, new EventUpdateFields
                {
                    Status = "failed",
                    TransferCompletedAt = DateTimeOffset.UtcNow.ToString("o"),
                    TransferError = ex.Message,
                });
                _dedup.Forget(uuid); // allow a retry on the next recording.completed for this uuid
            }
        });
    }

    // ---- Status/debug plumbing ----

    private void SetStatus(string status) { lock (_stateLock) _status = status; }

    private void Debug(string message)
    {
        var entry = new DebugLogEntry(DateTimeOffset.UtcNow.ToString("o"), message);
        lock (_stateLock)
        {
            _debugLog.Add(entry);
            if (_debugLog.Count > MaxDebugLog) _debugLog.RemoveAt(0);
        }
        _logger.LogInformation("[websocket] {Message}", message);

        var (active, queued, limit) = _transferQueue.GetQueueInfo();
        _ = _sseHub.BroadcastAsync("append", new
        {
            entry,
            status = GetStatus(),
            connectedAt = _connectedAt?.ToString("o"),
            counters = new { messages = _messages, heartbeats = _heartbeats, events = _events, errors = _errors },
            transfers = new { active, queued, limit },
        });
    }
}
