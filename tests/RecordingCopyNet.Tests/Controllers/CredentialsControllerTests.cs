using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class CredentialsControllerTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<CredentialType, Dictionary<string, string?>> Saved = new();
        public HashSet<CredentialType> Deleted = new();
        public bool Exists(CredentialType type) => Saved.ContainsKey(type);
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) => Saved[type] = new(fields);
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => Saved.GetValueOrDefault(type);
        public void Delete(CredentialType type) { Saved.Remove(type); Deleted.Add(type); }
    }

    private class FakeZoomAuthService : IZoomAuthService
    {
        public bool Cleared;
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok");
        public void ClearTokenCache() => Cleared = true;
    }

    private class FakeGoogleAuthService : IGoogleAuthService
    {
        public bool Cleared;
        public Google.Apis.Drive.v3.DriveService GetDriveService() => throw new NotImplementedException();
        public void ClearDriveClient() => Cleared = true;
    }

    private class FakeWsController : IZoomWebSocketController
    {
        public bool Stopped, Started;
        public string GetStatus() => "disconnected";
        public Models.ZoomWsDebugInfo GetDebugInfo() => throw new NotImplementedException();
        public IDisposable SubscribeDebugLog(Func<string, Task> writer) => throw new NotImplementedException();
        public int SseSubscriberCount => 0;
        public Task StartConnectionAsync() { Started = true; return Task.CompletedTask; }
        public Task StopConnectionAsync() { Stopped = true; return Task.CompletedTask; }
        public Task RestartConnectionAsync() => Task.CompletedTask;
    }

    private static (CredentialsController, FakeCredentialStore, FakeZoomAuthService, FakeGoogleAuthService, FakeWsController) Build()
    {
        var store = new FakeCredentialStore();
        var zoomAuth = new FakeZoomAuthService();
        var googleAuth = new FakeGoogleAuthService();
        var ws = new FakeWsController();
        return (new CredentialsController(store, zoomAuth, googleAuth, ws), store, zoomAuth, googleAuth, ws);
    }

    [Fact]
    public void GetConfigured_ReturnsBadRequest_ForUnknownType()
    {
        var (controller, _, _, _, _) = Build();
        var result = controller.GetConfigured("bogus");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void GetConfigured_ReturnsFalse_WhenNothingSaved()
    {
        var (controller, _, _, _, _) = Build();
        var result = controller.GetConfigured("zoom");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(((ConfiguredResponse)ok.Value!).Configured);
    }

    [Fact]
    public void Save_PersistsFields_AndClearsZoomTokenCache()
    {
        var (controller, store, zoomAuth, _, _) = Build();
        var result = controller.Save("zoom", new Dictionary<string, string?> { ["client_id"] = "abc" });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("abc", store.Saved[CredentialType.Zoom]["client_id"]);
        Assert.True(zoomAuth.Cleared);
    }

    [Fact]
    public void Save_OnSettings_ClearsGoogleClientAndRestartsWebSocket()
    {
        var (controller, _, _, googleAuth, ws) = Build();

        controller.Save("settings", new Dictionary<string, string?> { ["zoom_websocket_url"] = "wss://x" });

        Assert.True(googleAuth.Cleared);
        Assert.True(ws.Stopped);
        Assert.True(ws.Started);
    }

    [Fact]
    public void Delete_RemovesCredentialAndClearsCaches()
    {
        var (controller, store, zoomAuth, _, _) = Build();
        store.Save(CredentialType.Zoom, new Dictionary<string, string?> { ["client_id"] = "abc" });

        var result = controller.Delete("zoom");

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(store.Exists(CredentialType.Zoom));
        Assert.True(zoomAuth.Cleared);
    }
}
