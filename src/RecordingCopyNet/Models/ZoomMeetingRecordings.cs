using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class ZoomMeetingRecordings
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
    [JsonPropertyName("start_time")] public string? StartTime { get; set; }
    [JsonPropertyName("duration")] public int? Duration { get; set; }
    [JsonPropertyName("total_size")] public long? TotalSize { get; set; }
    [JsonPropertyName("recording_files")] public List<ZoomRecordingFile> RecordingFiles { get; set; } = new();
}
