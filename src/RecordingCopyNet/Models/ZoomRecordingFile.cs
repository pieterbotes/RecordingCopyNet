using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class ZoomRecordingFile
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("file_type")] public string? FileType { get; set; }
    [JsonPropertyName("file_extension")] public string? FileExtension { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("recording_start")] public string? RecordingStart { get; set; }
    [JsonPropertyName("recording_end")] public string? RecordingEnd { get; set; }
}
