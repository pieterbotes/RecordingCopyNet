using RecordingCopyNet.Models;
using DriveInfo = RecordingCopyNet.Models.DriveInfo;

namespace RecordingCopyNet.Services.Google;

public interface IGoogleDriveService
{
    Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default);
    Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default);
    Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default);
    Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default);
    Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default);
}
