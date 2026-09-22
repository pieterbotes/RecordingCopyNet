using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class ZoomControllerTests
{
    private class FakeZoomAuthService : IZoomAuthService
    {
        public Exception? ThrowOnGet;
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            ThrowOnGet != null ? Task.FromException<string>(ThrowOnGet) : Task.FromResult("tok");
        public void ClearTokenCache() { }
    }

    private class FakeZoomRecordingsService : IZoomRecordingsService
    {
        public Task<JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default) =>
            Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""[{"id":"m1"}]"""));
        public Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? SettingsFields;
        public bool Exists(CredentialType type) => false;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => type == CredentialType.Settings ? SettingsFields : null;
        public void Delete(CredentialType type) { }
    }

    private class TrackingWsController : IZoomWebSocketController
    {
        private readonly List<Func<string, Task>> _subscribers = new();
        public int SubscriberCount => _subscribers.Count;
        public bool StopCalled, StartCalled;
        public string Status = "disconnected";

        public string GetStatus() => Status;
        public ZoomWsDebugInfo GetDebugInfo() => new(Status, null, new ZoomWsCounters(1, 2, 3, 0), new ZoomWsQueueInfo(0, 0, 3), new());
        public IDisposable SubscribeDebugLog(Func<string, Task> writer)
        {
            _subscribers.Add(writer);
            return new Unsub(() => _subscribers.Remove(writer));
        }
        public int SseSubscriberCount => SubscriberCount;
        public Task StartConnectionAsync() { StartCalled = true; Status = "connected"; return Task.CompletedTask; }
        public Task StopConnectionAsync() { StopCalled = true; Status = "disconnected"; return Task.CompletedTask; }
        public Task RestartConnectionAsync() => Task.CompletedTask;

        private class Unsub : IDisposable
        {
            private readonly Action _action;
            public Unsub(Action action) => _action = action;
            public void Dispose() => _action();
        }
    }

    private static ZoomController Build(FakeZoomAuthService? auth = null, TrackingWsController? ws = null, FakeCredentialStore? store = null) =>
        new(auth ?? new FakeZoomAuthService(), new FakeZoomRecordingsService(), ws ?? new TrackingWsController(), store ?? new FakeCredentialStore());

    [Fact]
    public async Task Test_ReturnsOkTrue_WhenTokenFetchSucceeds()
    {
        var controller = Build();
        var result = await controller.Test();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(((OkTrueMessageResponse)ok.Value!).Ok);
    }

    [Fact]
    public async Task Test_ReturnsBadRequestWithOkFalse_WhenTokenFetchFails()
    {
        var controller = Build(new FakeZoomAuthService { ThrowOnGet = new InvalidOperationException("bad creds") });
        var result = await controller.Test();
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(((OkFalseErrorResponse)bad.Value!).Ok);
    }

    [Fact]
    public async Task GetRecordings_ReturnsBadRequest_WhenUserIdMissing()
    {
        var controller = Build();
        var result = await controller.GetRecordings(null, null, null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetRecordings_ReturnsMeetingsArray()
    {
        var controller = Build();
        var result = await controller.GetRecordings("user@x.com", null, null);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void GetWebSocketStatus_IncludesUrlFromSettings()
    {
        var store = new FakeCredentialStore { SettingsFields = new() { ["zoom_websocket_url"] = "wss://example" } };
        var controller = Build(store: store);
        var result = Assert.IsType<OkObjectResult>(controller.GetWebSocketStatus());
        Assert.NotNull(result.Value);
    }

    [Fact]
    public void GetSubscribers_ReturnsCurrentCount()
    {
        var ws = new TrackingWsController();
        ws.SubscribeDebugLog(_ => Task.CompletedTask);
        var controller = Build(ws: ws);

        var result = Assert.IsType<OkObjectResult>(controller.GetSubscribers());
        Assert.Equal(1, ((SubscribersResponse)result.Value!).Subscribers);
    }

    [Fact]
    public async Task Stop_ReturnsBareStatusString()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var result = await controller.Stop();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("disconnected", ok.Value);
        Assert.True(ws.StopCalled);
    }

    [Fact]
    public async Task Start_StopsThenStarts_AndReturnsBareStatusString()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var result = await controller.Start();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("connected", ok.Value);
        Assert.True(ws.StopCalled);
        Assert.True(ws.StartCalled);
    }

    [Fact]
    public async Task Restart_ReturnsWrappedStatusObject()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var result = Assert.IsType<OkObjectResult>(await controller.Restart());
        Assert.Equal("connected", ((RestartResponse)result.Value!).Status);
    }

    [Fact]
    public async Task Stream_WritesSnapshotFirst_AndUnsubscribesWhenClientDisconnects()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var cts = new CancellationTokenSource();

        var streamTask = controller.Stream(cts.Token);
        await Task.Delay(50);
        Assert.Equal(1, ws.SubscriberCount);

        cts.Cancel();
        await streamTask;

        Assert.Equal(0, ws.SubscriberCount);
        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.StartsWith("event: snapshot\n", text);
    }
}
