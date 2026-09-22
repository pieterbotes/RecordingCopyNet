using System.Net;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Services.Zoom;
using RecordingCopyNet.Tests.TestHelpers;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomDownloadServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rc-net-dl-{Guid.NewGuid():N}");

    private class FakeZoomAuthService : IZoomAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok-abc");
        public void ClearTokenCache() { }
    }

    [Fact]
    public async Task DownloadRecordingFileAsync_WritesResponseBodyToDestPath_AndAppendsTokenToUrl()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
        });
        var client = new HttpClient(handler);
        var service = new ZoomDownloadService(client, new FakeZoomAuthService(), Options.Create(new AppConfig { TempDir = _tempDir }));
        var destPath = Path.Combine(_tempDir, "file.mp4");

        await service.DownloadRecordingFileAsync("https://zoom.us/rec/download/abc", destPath);

        Assert.True(File.Exists(destPath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destPath));
        Assert.Contains("access_token=tok-abc", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadRecordingFileAsync_ThrowsOnFailureStatus()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new HttpClient(handler);
        var service = new ZoomDownloadService(client, new FakeZoomAuthService(), Options.Create(new AppConfig { TempDir = _tempDir }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadRecordingFileAsync("https://zoom.us/rec/download/abc", Path.Combine(_tempDir, "f.mp4")));
    }

    [Fact]
    public void GetTempDir_JoinsMeetingIdUnderConfiguredTempDir()
    {
        var service = new ZoomDownloadService(new HttpClient(), new FakeZoomAuthService(), Options.Create(new AppConfig { TempDir = _tempDir }));

        var dir = service.GetTempDir("meeting-123");

        Assert.Equal(Path.Combine(_tempDir, "meeting-123"), dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
