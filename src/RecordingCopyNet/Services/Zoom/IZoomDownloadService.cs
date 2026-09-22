namespace RecordingCopyNet.Services.Zoom;

public interface IZoomDownloadService
{
    Task DownloadRecordingFileAsync(string downloadUrl, string destPath, CancellationToken ct = default);
    string GetTempDir(string meetingId);
}
