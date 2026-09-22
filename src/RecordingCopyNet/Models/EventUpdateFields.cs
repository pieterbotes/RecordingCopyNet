namespace RecordingCopyNet.Models;

// Only non-null properties are applied — mirrors the "allowed keys present
// in fields" filter in lib/events.js's updateEvent().
public class EventUpdateFields
{
    public string? Status { get; set; }
    public string? SkipReason { get; set; }
    public string? TransferStartedAt { get; set; }
    public string? TransferCompletedAt { get; set; }
    public string? TransferFolderName { get; set; }
    public string? TransferFolderId { get; set; }
    public int? TransferFilesUploaded { get; set; }
    public string? TransferError { get; set; }
    public string? TransferLogs { get; set; }
}
