using System.Text.Json;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;

namespace RecordingCopyNet.Services;

// Direct port of lib/transfer.js — see spec §11 for the numbered sequence
// this follows.
public class TransferService : ITransferService
{
    private static readonly Dictionary<string, string> MimeMap = new()
    {
        ["MP4"] = "video/mp4",
        ["M4A"] = "audio/mp4",
        ["CHAT"] = "text/plain",
        ["TRANSCRIPT"] = "text/vtt",
        ["TIMELINE"] = "application/json",
        ["CC"] = "text/vtt",
        ["CSV"] = "text/csv",
    };

    private readonly IZoomRecordingsService _recordings;
    private readonly IZoomDownloadService _download;
    private readonly IGoogleDriveService _drive;
    private readonly ICredentialStore _credentialStore;

    public TransferService(IZoomRecordingsService recordings, IZoomDownloadService download,
        IGoogleDriveService drive, ICredentialStore credentialStore)
    {
        _recordings = recordings;
        _download = download;
        _drive = drive;
        _credentialStore = credentialStore;
    }

    public async Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default)
    {
        void Log(string message) => onProgress?.Invoke(message);

        var settings = _credentialStore.Load(CredentialType.Settings);
        if (settings == null || !settings.TryGetValue("google_folder_id", out var googleFolderId) || string.IsNullOrEmpty(googleFolderId))
            throw new InvalidOperationException("Google Drive folder ID not configured in settings");

        Log("Fetching meeting recording details from Zoom...");
        var meeting = await _recordings.GetMeetingRecordingsAsync(meetingId, ct);

        var files = meeting.RecordingFiles;
        if (files.Count == 0)
            throw new InvalidOperationException("No recording files found for this meeting");

        Log("Verifying target folder storage access...");
        var parentInfo = await _drive.VerifySharedDriveAsync(googleFolderId, ct);
        var hasSharedDrive = !string.IsNullOrEmpty(parentInfo.DriveId);
        var impersonateEmail = settings.TryGetValue("google_impersonate_email", out var imp) ? imp : null;
        var hasImpersonation = !string.IsNullOrEmpty(impersonateEmail);

        if (!hasSharedDrive && !hasImpersonation)
        {
            throw new InvalidOperationException(
                "Target folder is NOT on a Shared Drive and no impersonation email is configured. " +
                "Service account uploads will fail due to 0 storage quota. " +
                "Either use a Shared Drive folder or configure Domain-Wide Delegation in Settings.");
        }

        if (hasSharedDrive) Log($"Target folder \"{parentInfo.Name}\" on Shared Drive: {parentInfo.DriveId}");
        if (hasImpersonation) Log($"Impersonating {impersonateEmail} for storage quota");

        var startDate = meeting.StartTime != null && meeting.StartTime.Length >= 10 ? meeting.StartTime[..10] : "unknown-date";
        var topic = string.IsNullOrEmpty(meeting.Topic) ? "Untitled Meeting" : meeting.Topic;
        var folderName = SanitizeFolderName($"{startDate} - {topic}");

        var tempDir = _download.GetTempDir(meetingId);
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

        var metadata = new
        {
            meeting_id = meeting.Id?.ToString() ?? meeting.Uuid,
            topic = meeting.Topic,
            start_time = meeting.StartTime,
            duration = meeting.Duration,
            total_size = meeting.TotalSize,
            recording_count = files.Count,
            files = files.Select(f => new
            {
                id = f.Id,
                file_type = f.FileType,
                file_extension = f.FileExtension,
                file_size = f.FileSize,
                recording_start = f.RecordingStart,
                recording_end = f.RecordingEnd,
                status = f.Status,
            }),
        };

        var metadataPath = Path.Combine(tempDir, "metadata.json");
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), ct);
        Log("Saved metadata.json");

        var downloadedFiles = new List<(string Path, string Name, string? Type)>();
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.DownloadUrl)) continue;

            var ext = (file.FileExtension ?? file.FileType ?? "bin").ToLowerInvariant();
            var fileName = $"{file.FileType ?? "recording"}_{file.Id}.{ext}";
            var destPath = Path.Combine(tempDir, fileName);

            Log($"Downloading {fileName} ({FormatSize(file.FileSize)})...");
            await _download.DownloadRecordingFileAsync(file.DownloadUrl, destPath, ct);
            downloadedFiles.Add((destPath, fileName, file.FileType));
            Log($"Downloaded {fileName}");
        }

        Log($"Creating Drive folder: {folderName}");
        var folder = await _drive.CreateFolderAsync(folderName, googleFolderId, ct);
        Log($"Created folder: {folder.Name} (id: {folder.Id}, driveId: {folder.DriveId ?? "none"})");

        Log("Uploading metadata.json...");
        await _drive.UploadFileAsync(metadataPath, "metadata.json", folder.Id, "application/json", ct);
        Log("Uploaded metadata.json");

        foreach (var file in downloadedFiles)
        {
            var mime = file.Type != null && MimeMap.TryGetValue(file.Type, out var m) ? m : "application/octet-stream";
            Log($"Uploading {file.Name}...");
            await _drive.UploadFileAsync(file.Path, file.Name, folder.Id, mime, ct);
            Log($"Uploaded {file.Name}");
        }

        Log("Cleaning up temp files...");
        Directory.Delete(tempDir, recursive: true);
        var totalUploaded = downloadedFiles.Count + 1;
        Log($"Transfer complete! {totalUploaded} files uploaded to \"{folderName}\"");

        return new TransferResult(folderName, folder.Id, totalUploaded);
    }

    private static string SanitizeFolderName(string name)
    {
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in name)
            sanitized.Append("<>:\"/\\|?*".Contains(c) ? '_' : c);
        var result = sanitized.ToString();
        return result.Length > 200 ? result[..200] : result;
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null or 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes.Value;
        var i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }
}
