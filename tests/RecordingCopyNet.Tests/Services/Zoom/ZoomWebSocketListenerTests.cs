using Microsoft.Extensions.Logging.Abstractions;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomWebSocketListenerTests
{
    private class FakeEventsRepository : IEventsRepository
    {
        public long NextId = 1;
        public Dictionary<long, EventRecord> Rows = new();

        public long LogEvent(string eventType, string? meetingUuid, string? meetingTopic, string? hostEmail, string? rawPayload)
        {
            var id = NextId++;
            Rows[id] = new EventRecord { Id = id, EventType = eventType, MeetingUuid = meetingUuid, MeetingTopic = meetingTopic, HostEmail = hostEmail, RawPayload = rawPayload, Status = "received" };
            return id;
        }

        public void UpdateEvent(long id, EventUpdateFields fields)
        {
            var row = Rows[id];
            if (fields.Status != null) row.Status = fields.Status;
            if (fields.SkipReason != null) row.SkipReason = fields.SkipReason;
            if (fields.TransferError != null) row.TransferError = fields.TransferError;
            if (fields.TransferFolderName != null) row.TransferFolderName = fields.TransferFolderName;
            if (fields.TransferFolderId != null) row.TransferFolderId = fields.TransferFolderId;
            if (fields.TransferFilesUploaded != null) row.TransferFilesUploaded = fields.TransferFilesUploaded;
            if (fields.TransferStartedAt != null) row.TransferStartedAt = fields.TransferStartedAt;
            if (fields.TransferCompletedAt != null) row.TransferCompletedAt = fields.TransferCompletedAt;
        }

        public void AppendLog(long id, string message) { }
        public List<EventRecord> ListEvents(int limit = 50, int offset = 0) => Rows.Values.ToList();
        public EventRecord? GetEvent(long id) => Rows.GetValueOrDefault(id);
        public EventStats GetStats() => new(Rows.Count, 0, 0, 0);
    }

    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? SettingsFields;
        public bool Exists(CredentialType type) => type == CredentialType.Settings && SettingsFields != null;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => type == CredentialType.Settings ? SettingsFields : null;
        public void Delete(CredentialType type) { }
    }

    private class FakeTransferService : ITransferService
    {
        public Exception? ThrowInstead;
        public List<string> CalledWithMeetingIds = new();
        public Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default)
        {
            CalledWithMeetingIds.Add(meetingId);
            if (ThrowInstead != null) return Task.FromException<TransferResult>(ThrowInstead);
            return Task.FromResult(new TransferResult("folder-name", "folder-id", 2));
        }
    }

    private class FakeZoomRecordingsService : IZoomRecordingsService
    {
        public Task<System.Text.Json.JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private class NoopZoomAuthService : IZoomAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok");
        public void ClearTokenCache() { }
    }

    // Simulates a corrupt/locked settings store (e.g. SQLite row error) to prove
    // StartConnectionAsync's settings read is guarded and never faults the host.
    private class ThrowingCredentialStore : ICredentialStore
    {
        public bool Exists(CredentialType type) => false;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => throw new InvalidOperationException("db locked");
        public void Delete(CredentialType type) { }
    }

    private static ZoomWebSocketListener BuildListener(
        FakeEventsRepository events, FakeCredentialStore store, FakeTransferService transfer,
        FakeZoomRecordingsService? recordings = null, EventDedupTracker? dedup = null)
    {
        return new ZoomWebSocketListener(
            new NoopZoomAuthService(),
            recordings ?? new FakeZoomRecordingsService(),
            transfer,
            store,
            events,
            new ZoomWsMessageRouter(),
            dedup ?? new EventDedupTracker(TimeProvider.System),
            new TransferQueue(limit: 3),
            new SseBroadcastHub(),
            NullLogger<ZoomWebSocketListener>.Instance);
    }

    private const string RecordingCompletedTemplate =
        """{"module":"message","content":"{\"event\":\"recording.completed\",\"payload\":{\"object\":{\"uuid\":\"__UUID__\",\"topic\":\"Standup\",\"host_email\":\"__HOST__\"}}}"}""";

    [Fact]
    public async Task HandleRawMessageAsync_RecordingCompleted_RunsTransferAndMarksCompleted()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService();
        var listener = BuildListener(events, new FakeCredentialStore { SettingsFields = new() }, transfer);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-1").Replace("__HOST__", "host@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(100);

        var row = events.Rows.Values.Single();
        Assert.Equal("completed", row.Status);
        Assert.Equal(2, row.TransferFilesUploaded);
        Assert.Contains("uuid-1", transfer.CalledWithMeetingIds);
    }

    [Fact]
    public async Task HandleRawMessageAsync_DuplicateUuid_SkipsSecondOccurrence()
    {
        var events = new FakeEventsRepository();
        var listener = BuildListener(events, new FakeCredentialStore { SettingsFields = new() }, new FakeTransferService());
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-dup").Replace("__HOST__", "host@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);
        await listener.HandleRawMessageAsync(raw, CancellationToken.None);

        Assert.Equal(2, events.Rows.Count);
        Assert.Equal("skipped", events.Rows.Values.Last().Status);
        Assert.Equal("duplicate", events.Rows.Values.Last().SkipReason);
    }

    [Fact]
    public async Task HandleRawMessageAsync_FiltersByConfiguredUser_WhenHostEmailDoesNotMatch()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService();
        var store = new FakeCredentialStore { SettingsFields = new() { ["default_zoom_user"] = "me@x.com" } };
        var listener = BuildListener(events, store, transfer);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-2").Replace("__HOST__", "other@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);

        var row = events.Rows.Values.Single();
        Assert.Equal("skipped", row.Status);
        Assert.Contains("wrong user", row.SkipReason);
        Assert.Empty(transfer.CalledWithMeetingIds);
    }

    [Fact]
    public async Task HandleRawMessageAsync_AllUsersMode_AcceptsAnyHost()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService();
        var store = new FakeCredentialStore { SettingsFields = new() { ["default_zoom_user"] = "me@x.com", ["transfer_all_users"] = "true" } };
        var listener = BuildListener(events, store, transfer);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-3").Replace("__HOST__", "other@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);

        Assert.Contains("uuid-3", transfer.CalledWithMeetingIds);
    }

    [Fact]
    public async Task HandleRawMessageAsync_TransferFailure_MarksFailedAndForgetsDedupForRetry()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService { ThrowInstead = new InvalidOperationException("upload failed") };
        var dedup = new EventDedupTracker(TimeProvider.System);
        var listener = BuildListener(events, new FakeCredentialStore { SettingsFields = new() }, transfer, dedup: dedup);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-4").Replace("__HOST__", "host@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal("failed", events.Rows.Values.Single().Status);
        Assert.Equal("upload failed", events.Rows.Values.Single().TransferError);

        await listener.HandleRawMessageAsync(raw, CancellationToken.None); // retry after failure
        await Task.Delay(50);

        Assert.Equal(2, events.Rows.Count);
        Assert.NotEqual("skipped", events.Rows.Values.Last().Status);
    }

    [Fact]
    public async Task HandleRawMessageAsync_HeartbeatIgnoredAndParseError_UpdateCountersWithoutThrowing()
    {
        var listener = BuildListener(new FakeEventsRepository(), new FakeCredentialStore { SettingsFields = new() }, new FakeTransferService());

        await listener.HandleRawMessageAsync("""{"module":"heartbeat"}""", CancellationToken.None);
        await listener.HandleRawMessageAsync("""{"module":"message","content":"{\"event\":\"meeting.started\",\"payload\":{}}"}""", CancellationToken.None);
        await listener.HandleRawMessageAsync("not json", CancellationToken.None);

        var info = listener.GetDebugInfo();
        Assert.True(info.Counters.Heartbeats >= 1);
        Assert.True(info.Counters.Errors >= 1);
    }

    [Fact]
    public void GetStatus_StartsDisconnected()
    {
        var listener = BuildListener(new FakeEventsRepository(), new FakeCredentialStore(), new FakeTransferService());
        Assert.Equal("disconnected", listener.GetStatus());
    }

    [Fact]
    public async Task StartConnectionAsync_WithNoConfiguredUrl_StaysDisconnected()
    {
        var listener = BuildListener(new FakeEventsRepository(), new FakeCredentialStore { SettingsFields = new() }, new FakeTransferService());

        await listener.StartConnectionAsync();

        Assert.Equal("disconnected", listener.GetStatus());
    }

    [Fact]
    public async Task StartConnectionAsync_CalledTwice_CancelsAndDisposesPreviousConnectionAttempt()
    {
        // An unparsable URL makes TryConnectOnceAsync fail synchronously (no real socket),
        // while still exercising the real StartConnectionAsync code path that creates and
        // tracks a CancellationTokenSource for the connection attempt/loop.
        var store = new FakeCredentialStore { SettingsFields = new() { ["zoom_websocket_url"] = "not a url" } };
        var listener = BuildListener(new FakeEventsRepository(), store, new FakeTransferService());

        await listener.StartConnectionAsync();
        var firstCts = listener.ConnectionCtsForTests;
        Assert.NotNull(firstCts);

        await listener.StartConnectionAsync();
        var secondCts = listener.ConnectionCtsForTests;

        Assert.NotNull(secondCts);
        Assert.NotSame(firstCts, secondCts);
        Assert.True(firstCts!.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => firstCts.Token);

        await listener.StopConnectionAsync();
    }

    [Fact]
    public async Task StartConnectionAsync_WhenSettingsReadThrows_StaysDisconnectedWithoutPropagating()
    {
        var listener = new ZoomWebSocketListener(
            new NoopZoomAuthService(),
            new FakeZoomRecordingsService(),
            new FakeTransferService(),
            new ThrowingCredentialStore(),
            new FakeEventsRepository(),
            new ZoomWsMessageRouter(),
            new EventDedupTracker(TimeProvider.System),
            new TransferQueue(limit: 3),
            new SseBroadcastHub(),
            NullLogger<ZoomWebSocketListener>.Instance);

        var ex = await Record.ExceptionAsync(() => listener.StartConnectionAsync());

        Assert.Null(ex);
        Assert.Equal("disconnected", listener.GetStatus());
    }

    [Fact]
    public async Task StartConnectionAsync_TrueConcurrentCalls_SerializeAndLeaveExactlyOneLiveConnectionCts()
    {
        // Fires two StartConnectionAsync() calls via Task.WhenAll so they race for real
        // (different thread-pool threads, not just sequential re-entry) against the
        // _lifecycleLock semaphore. An unparsable URL keeps each attempt's own work
        // synchronous/fast (no real socket), so this isolates the lock's serialization
        // behavior rather than real network timing.
        var store = new FakeCredentialStore { SettingsFields = new() { ["zoom_websocket_url"] = "not a url" } };
        var listener = BuildListener(new FakeEventsRepository(), store, new FakeTransferService());

        var ex = await Record.ExceptionAsync(() =>
            Task.WhenAll(listener.StartConnectionAsync(), listener.StartConnectionAsync()));
        await Task.Delay(50); // let both fire-and-forget connect loops settle

        Assert.Null(ex);

        // Exactly one CTS should have survived: whichever call the semaphore let run
        // second cancelled+disposed the other's CTS (under the lock, no race) before
        // installing its own. If the two calls had instead raced on a plain field write
        // (the round-1 bug this test targets), the loser's CTS would still be reachable
        // as the "final" _connectionCts while never being cancelled/disposed — or the
        // final CTS itself could already be disposed by the other racing writer.
        var finalCts = listener.ConnectionCtsForTests;
        Assert.NotNull(finalCts);
        Assert.False(finalCts!.IsCancellationRequested);
        var tokenEx = Record.Exception(() => finalCts.Token);
        Assert.Null(tokenEx); // not disposed

        await listener.StopConnectionAsync();
    }
}
