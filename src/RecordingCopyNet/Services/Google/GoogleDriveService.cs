using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using RecordingCopyNet.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using DriveInfo = RecordingCopyNet.Models.DriveInfo;
using GoogleSharedDrive = Google.Apis.Drive.v3.Data.Drive;

namespace RecordingCopyNet.Services.Google;

// Direct port of lib/google/drive.js.
public class GoogleDriveService : IGoogleDriveService
{
    private readonly IGoogleAuthService _auth;

    public GoogleDriveService(IGoogleAuthService auth) => _auth = auth;

    public async Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var request = drive.Files.Get(folderId);
        request.Fields = "id, name, driveId, parents, spaces";
        request.SupportsAllDrives = true;
        var file = await request.ExecuteAsync(ct);
        return new DriveFolderInfo(file.Id, file.Name, file.DriveId);
    }

    public async Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var body = new DriveFile
        {
            Name = name,
            MimeType = "application/vnd.google-apps.folder",
            Parents = new List<string> { parentFolderId },
        };
        var request = drive.Files.Create(body);
        request.Fields = "id, name, driveId";
        request.SupportsAllDrives = true;
        var file = await request.ExecuteAsync(ct);
        return new DriveFolderInfo(file.Id, file.Name, file.DriveId);
    }

    public async Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var body = new DriveFile { Name = fileName, Parents = new List<string> { folderId } };

        await using var stream = File.OpenRead(filePath);
        var request = drive.Files.Create(body, stream, mimeType);
        request.Fields = "id, name, size";
        request.SupportsAllDrives = true;

        var progress = await request.UploadAsync(ct);
        if (progress.Status != UploadStatus.Completed)
            throw new InvalidOperationException($"Upload of {fileName} failed: {progress.Exception?.Message}");

        var file = request.ResponseBody;
        return new DriveFileInfo(file.Id, file.Name, file.Size);
    }

    public async Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var request = drive.Drives.List();
        request.PageSize = 50;
        request.Fields = "drives(id, name)";
        var result = await request.ExecuteAsync(ct);
        return (result.Drives ?? new List<GoogleSharedDrive>())
            .Select(d => new DriveInfo(d.Id, d.Name)).ToList();
    }

    public async Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var request = drive.Files.List();
        request.Q = $"'{folderId}' in parents and trashed = false";
        request.Fields = "files(id, name, mimeType)";
        request.PageSize = 10;
        request.IncludeItemsFromAllDrives = true;
        request.SupportsAllDrives = true;
        var result = await request.ExecuteAsync(ct);
        return (result.Files ?? new List<DriveFile>())
            .Select(f => new DriveFileInfo(f.Id, f.Name, f.Size)).ToList();
    }
}
