using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;
using Xunit;
using DriveInfo = RecordingCopyNet.Models.DriveInfo;

namespace RecordingCopyNet.Tests.Services;

public class TransferServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly CredentialStore _credentialStore;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rc-net-transfer-{Guid.NewGuid():N}");

    private class FakeRecordingsService : IZoomRecordingsService
    {
        public ZoomMeetingRecordings? Meeting;
        public Task<System.Text.Json.JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default)
            => Task.FromResult(Meeting!);
        public Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private class FakeDownloadService : IZoomDownloadService
    {
        public List<string> DownloadedTo = new();
        public Task DownloadRecordingFileAsync(string downloadUrl, string destPath, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllBytes(destPath, new byte[] { 9, 9 });
            DownloadedTo.Add(destPath);
            return Task.CompletedTask;
        }
        public string GetTempDir(string meetingId) => Path.Combine(Path.GetTempPath(), "rc-net-transfer-work", meetingId);
    }

    private class FakeDriveService : IGoogleDriveService
    {
        public DriveFolderInfo VerifyResult = new("parent-id", "Parent", "shared-drive-id");
        public List<string> UploadedNames = new();

        public Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default) => Task.FromResult(VerifyResult);
        public Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default) =>
            Task.FromResult(new DriveFolderInfo("new-folder-id", name, "shared-drive-id"));
        public Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default)
        {
            UploadedNames.Add(fileName);
            return Task.FromResult(new DriveFileInfo("file-id", fileName, 100));
        }
        public Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default) => Task.FromResult(new List<DriveInfo>());
        public Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default) => Task.FromResult(new List<DriveFileInfo>());
    }

    public TransferServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rc-net-{Guid.NewGuid():N}.db");
        var config = new AppConfig { DbPath = _dbPath, DataDir = Path.GetTempPath(), TempDir = _tempDir };
        _db = new Db(Options.Create(config));
        _db.InitializeSchema();
        var cipher = new RecordingCopyNet.Security.AesGcmFieldCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _credentialStore = new CredentialStore(_db, cipher);
    }

    private static ZoomMeetingRecordings SampleMeeting() => new()
    {
        Id = 123,
        Uuid = "uuid-abc",
        Topic = "Team Standup",
        StartTime = "2026-09-01T10:00:00Z",
        RecordingFiles = new List<ZoomRecordingFile>
        {
            new() { Id = "f1", FileType = "MP4", FileExtension = "MP4", DownloadUrl = "https://zoom/f1", FileSize = 100 },
            new() { Id = "f2", FileType = "CHAT", FileExtension = "TXT", DownloadUrl = "https://zoom/f2", FileSize = 10 },
        },
    };

    [Fact]
    public async Task TransferMeetingAsync_HappyPath_UploadsMetadataPlusEachFile()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var download = new FakeDownloadService();
        var drive = new FakeDriveService();
        var service = new TransferService(recordings, download, drive, _credentialStore);

        var result = await service.TransferMeetingAsync("uuid-abc", null);

        Assert.Equal("new-folder-id", result.FolderId);
        Assert.Equal(3, result.FilesUploaded); // metadata.json + 2 recording files
        Assert.Contains("metadata.json", drive.UploadedNames);
        Assert.Contains(drive.UploadedNames, n => n.StartsWith("MP4_f1"));
        Assert.Contains(drive.UploadedNames, n => n.StartsWith("CHAT_f2"));
    }

    [Fact]
    public async Task TransferMeetingAsync_ThrowsWhenGoogleFolderIdNotConfigured()
    {
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var service = new TransferService(recordings, new FakeDownloadService(), new FakeDriveService(), _credentialStore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferMeetingAsync("uuid-abc", null));
        Assert.Contains("Google Drive folder ID not configured", ex.Message);
    }

    [Fact]
    public async Task TransferMeetingAsync_ThrowsWhenNoRecordingFiles()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = new ZoomMeetingRecordings { Uuid = "uuid-abc", RecordingFiles = new() } };
        var service = new TransferService(recordings, new FakeDownloadService(), new FakeDriveService(), _credentialStore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferMeetingAsync("uuid-abc", null));
        Assert.Contains("No recording files found", ex.Message);
    }

    [Fact]
    public async Task TransferMeetingAsync_ThrowsWhenNoSharedDriveAndNoImpersonation()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var drive = new FakeDriveService { VerifyResult = new DriveFolderInfo("parent-id", "Parent", null) }; // not on a shared drive
        var service = new TransferService(recordings, new FakeDownloadService(), drive, _credentialStore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferMeetingAsync("uuid-abc", null));
        Assert.Contains("NOT on a Shared Drive", ex.Message);
    }

    [Fact]
    public async Task TransferMeetingAsync_AllowsNonSharedDrive_WhenImpersonationConfigured()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?>
        {
            ["google_folder_id"] = "parent-id",
            ["google_impersonate_email"] = "svc@example.com",
        });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var drive = new FakeDriveService { VerifyResult = new DriveFolderInfo("parent-id", "Parent", null) };
        var service = new TransferService(recordings, new FakeDownloadService(), drive, _credentialStore);

        var result = await service.TransferMeetingAsync("uuid-abc", null);

        Assert.Equal(3, result.FilesUploaded);
    }

    [Fact]
    public async Task TransferMeetingAsync_SanitizesFolderName()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var meeting = SampleMeeting();
        meeting.Topic = "Q&A: Sprint/Review? <2026>";
        var recordings = new FakeRecordingsService { Meeting = meeting };
        var drive = new FakeDriveService();
        var service = new TransferService(recordings, new FakeDownloadService(), drive, _credentialStore);

        var result = await service.TransferMeetingAsync("uuid-abc", null);

        Assert.DoesNotContain("<", result.FolderName);
        Assert.DoesNotContain("/", result.FolderName);
        Assert.DoesNotContain(":", result.FolderName);
        Assert.StartsWith("2026-09-01", result.FolderName);
    }

    [Fact]
    public async Task TransferMeetingAsync_ReportsProgressMessages()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var service = new TransferService(recordings, new FakeDownloadService(), new FakeDriveService(), _credentialStore);
        var messages = new List<string>();

        await service.TransferMeetingAsync("uuid-abc", messages.Add);

        Assert.Contains(messages, m => m.Contains("Fetching meeting recording details"));
        Assert.Contains(messages, m => m.StartsWith("Transfer complete!"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
