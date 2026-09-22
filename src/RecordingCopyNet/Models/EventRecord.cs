using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class EventRecord
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "";
    [JsonPropertyName("meeting_uuid")] public string? MeetingUuid { get; set; }
    [JsonPropertyName("meeting_topic")] public string? MeetingTopic { get; set; }
    [JsonPropertyName("host_email")] public string? HostEmail { get; set; }
    [JsonPropertyName("received_at")] public string? ReceivedAt { get; set; }
    [JsonPropertyName("raw_payload")] public string? RawPayload { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "received";
    [JsonPropertyName("skip_reason")] public string? SkipReason { get; set; }
    [JsonPropertyName("transfer_started_at")] public string? TransferStartedAt { get; set; }
    [JsonPropertyName("transfer_completed_at")] public string? TransferCompletedAt { get; set; }
    [JsonPropertyName("transfer_folder_name")] public string? TransferFolderName { get; set; }
    [JsonPropertyName("transfer_folder_id")] public string? TransferFolderId { get; set; }
    [JsonPropertyName("transfer_files_uploaded")] public int? TransferFilesUploaded { get; set; }
    [JsonPropertyName("transfer_error")] public string? TransferError { get; set; }
    [JsonPropertyName("transfer_logs")] public string? TransferLogs { get; set; }
}
