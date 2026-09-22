using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Services.Zoom;
using RecordingCopyNet.Tests.TestHelpers;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomRecordingsServiceTests
{
    private class FakeZoomAuthService : IZoomAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok-abc");
        public void ClearTokenCache() { }
    }

    [Fact]
    public async Task ListRecordingsAsync_ReturnsMeetingsArrayFromResponse_AndSendsBearerToken()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { meetings = new[] { new { id = "m1", topic = "Standup" } } })
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var meetings = await service.ListRecordingsAsync("user@x.com", null, null);

        Assert.Equal(1, meetings.GetArrayLength());
        Assert.Equal("m1", meetings[0].GetProperty("id").GetString());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("tok-abc", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ListRecordingsAsync_ReturnsEmptyArray_WhenNoMeetingsKey()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { })
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var meetings = await service.ListRecordingsAsync("user@x.com", null, null);

        Assert.Equal(0, meetings.GetArrayLength());
    }

    [Fact]
    public async Task GetMeetingRecordingsAsync_ParsesRecordingFiles()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = 123,
                uuid = "abc==",
                topic = "Standup",
                start_time = "2026-09-01T10:00:00Z",
                recording_files = new[] { new { id = "f1", file_type = "MP4", download_url = "https://x/y" } }
            })
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var meeting = await service.GetMeetingRecordingsAsync("abc==");

        Assert.Equal("Standup", meeting.Topic);
        Assert.Single(meeting.RecordingFiles);
        Assert.Equal("MP4", meeting.RecordingFiles[0].FileType);
    }

    [Fact]
    public async Task GetMeetingRecordingsAsync_ThrowsWithBodyOnFailure()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("meeting not found")
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMeetingRecordingsAsync("bad-id"));
        Assert.Contains("meeting not found", ex.Message);
    }

    [Fact]
    public async Task GetUserEmailAsync_ReturnsNull_OnFailure()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var email = await service.GetUserEmailAsync("user-id");

        Assert.Null(email);
    }
}
