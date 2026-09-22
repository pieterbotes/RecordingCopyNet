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

    public string GetTempDir(string meetingId) => Path.Combine(_config.TempDir, SanitizeMeetingId(meetingId));

    // Defense-in-depth: `meetingId` ends up as a filesystem path segment that's later
    // recursively deleted (see TransferService.GetTempDir usage). Not currently
    // exploitable in practice (a real Zoom meeting fetch has to succeed first), but
    // strips filesystem-illegal characters and neutralizes ".." path-traversal
    // sequences, matching the sanitization already applied to Drive folder names
    // (SanitizeFolderName in TransferService).
    private static string SanitizeMeetingId(string meetingId)
    {
        if (string.IsNullOrEmpty(meetingId)) return meetingId;
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new System.Text.StringBuilder(meetingId.Length);
        foreach (var c in meetingId)
            sanitized.Append(invalid.Contains(c) ? '_' : c);
        return sanitized.ToString().Replace("..", "__");
    }
}
