using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/google")]
public class GoogleController : ControllerBase
{
    private readonly ICredentialStore _store;
    private readonly IGoogleDriveService _drive;

    public GoogleController(ICredentialStore store, IGoogleDriveService drive)
    {
        _store = store;
        _drive = drive;
    }

    [HttpPost("test")]
    public async Task<ActionResult> Test()
    {
        var settings = _store.Load(CredentialType.Settings);
        if (settings == null || !settings.TryGetValue("google_folder_id", out var folderId) || string.IsNullOrEmpty(folderId))
            return BadRequest(new OkFalseErrorResponse(false, "Google Drive folder ID not configured"));

        try
        {
            var folderInfo = await _drive.VerifySharedDriveAsync(folderId);
            var files = await _drive.ListFolderContentsAsync(folderId);

            var onSharedDrive = !string.IsNullOrEmpty(folderInfo.DriveId);
            var impersonateEmail = settings.TryGetValue("google_impersonate_email", out var imp) ? imp : null;
            var hasImpersonation = !string.IsNullOrEmpty(impersonateEmail);
            var ok = onSharedDrive || hasImpersonation;

            string message;
            if (hasImpersonation && onSharedDrive)
                message = $"Connected. Impersonating {impersonateEmail} + Shared Drive {folderInfo.DriveId}. Folder \"{folderInfo.Name}\" — {files.Count} items.";
            else if (hasImpersonation)
                message = $"Connected. Impersonating {impersonateEmail}. Folder \"{folderInfo.Name}\" — {files.Count} items.";
            else if (onSharedDrive)
                message = $"Connected. Folder \"{folderInfo.Name}\" is on Shared Drive {folderInfo.DriveId}. {files.Count} items found.";
            else
                message = $"Folder \"{folderInfo.Name}\" is NOT on a Shared Drive and no impersonation is configured. Configure Domain-Wide Delegation or use a Shared Drive folder.";

            return Ok(new GoogleTestResponse(ok, message, onSharedDrive, hasImpersonation ? impersonateEmail : null, folderInfo.DriveId));
        }
        catch (Exception ex)
        {
            return BadRequest(new OkFalseErrorResponse(false, ex.Message));
        }
    }

    [HttpGet("drives")]
    public async Task<ActionResult> GetDrives()
    {
        try
        {
            var drives = await _drive.ListSharedDrivesAsync();
            return Ok(new { ok = true, drives });
        }
        catch (Exception ex)
        {
            return BadRequest(new OkFalseErrorResponse(false, ex.Message));
        }
    }
}
