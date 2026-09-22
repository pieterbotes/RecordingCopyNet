using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/download.js. Streams the response body straight
// to disk rather than buffering — recording files can be gigabytes.
public class ZoomDownloadService : IZoomDownloadService
{
    private readonly HttpClient _http;
    private readonly IZoomAuthService _auth;
    private readonly AppConfig _config;

    public ZoomDownloadService(HttpClient http, IZoomAuthService auth, IOptions<AppConfig> config)
    {
        _http = http;
        _auth = auth;
        _config = config.Value;
    }

    public async Task DownloadRecordingFileAsync(string downloadUrl, string destPath, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        var url = $"{downloadUrl}?access_token={token}";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Download failed ({(int)response.StatusCode}): {downloadUrl}");

        var dir = Path.GetDirectoryName(destPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        await using var sourceStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(destPath);
        await sourceStream.CopyToAsync(fileStream, ct);
    }

    public string GetTempDir(string meetingId) => Path.Combine(_config.TempDir, meetingId);
}
