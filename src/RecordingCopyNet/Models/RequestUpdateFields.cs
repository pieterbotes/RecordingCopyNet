namespace RecordingCopyNet.Models;

public class RequestUpdateFields
{
    public string? Status { get; set; }
    public string? TransferError { get; set; }
    public string? TransferFolderName { get; set; }
    public string? TransferFolderId { get; set; }
    public int? TransferFilesUploaded { get; set; }
}
