using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class RequestRecord
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("surname")] public string Surname { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("meeting_id")] public string MeetingId { get; set; } = "";
    [JsonPropertyName("meeting_date")] public string MeetingDate { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("transfer_error")] public string? TransferError { get; set; }
    [JsonPropertyName("transfer_folder_name")] public string? TransferFolderName { get; set; }
    [JsonPropertyName("transfer_folder_id")] public string? TransferFolderId { get; set; }
    [JsonPropertyName("transfer_files_uploaded")] public int? TransferFilesUploaded { get; set; }
}
