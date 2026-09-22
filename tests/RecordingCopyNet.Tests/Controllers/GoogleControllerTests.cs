using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using Xunit;
using DriveInfo = RecordingCopyNet.Models.DriveInfo;

namespace RecordingCopyNet.Tests.Controllers;

public class GoogleControllerTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? SettingsFields;
        public bool Exists(CredentialType type) => false;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => type == CredentialType.Settings ? SettingsFields : null;
        public void Delete(CredentialType type) { }
    }

    private class FakeGoogleDriveService : IGoogleDriveService
    {
        public DriveFolderInfo VerifyResult = new("folder-id", "My Folder", "shared-drive-1");
        public List<DriveFileInfo> Contents = new() { new("f1", "a.txt", 10) };
        public List<DriveInfo> Drives = new() { new("d1", "Team Drive") };
        public Exception? ThrowOnListDrives;

        public Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default) => Task.FromResult(VerifyResult);
        public Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default) =>
            ThrowOnListDrives != null ? Task.FromException<List<DriveInfo>>(ThrowOnListDrives) : Task.FromResult(Drives);
        public Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default) => Task.FromResult(Contents);
    }

    [Fact]
    public async Task Test_ReturnsBadRequest_WhenFolderIdNotConfigured()
    {
        var controller = new GoogleController(new FakeCredentialStore(), new FakeGoogleDriveService());
        var result = await controller.Test();
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Test_ReturnsOkTrue_WhenOnSharedDrive()
    {
        var store = new FakeCredentialStore { SettingsFields = new() { ["google_folder_id"] = "folder-id" } };
        var controller = new GoogleController(store, new FakeGoogleDriveService());

        var result = Assert.IsType<OkObjectResult>(await controller.Test());
        var body = (GoogleTestResponse)result.Value!;
        Assert.True(body.Ok);
        Assert.True(body.SharedDrive);
        Assert.Equal("shared-drive-1", body.DriveId);
        Assert.Null(body.Impersonating);
    }

    [Fact]
    public async Task Test_ReturnsOkFalse_WhenNotOnSharedDriveAndNoImpersonation()
    {
        var store = new FakeCredentialStore { SettingsFields = new() { ["google_folder_id"] = "folder-id" } };
        var drive = new FakeGoogleDriveService { VerifyResult = new DriveFolderInfo("folder-id", "My Folder", null) };
        var controller = new GoogleController(store, drive);

        var result = Assert.IsType<OkObjectResult>(await controller.Test());
        Assert.False(((GoogleTestResponse)result.Value!).Ok);
    }

    [Fact]
    public async Task GetDrives_ReturnsOkTrueWithDrives()
    {
        var controller = new GoogleController(new FakeCredentialStore(), new FakeGoogleDriveService());
        var result = Assert.IsType<OkObjectResult>(await controller.GetDrives());
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task GetDrives_ReturnsBadRequest_OnFailure()
    {
        var drive = new FakeGoogleDriveService { ThrowOnListDrives = new InvalidOperationException("no access") };
        var controller = new GoogleController(new FakeCredentialStore(), drive);
        var result = await controller.GetDrives();
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
