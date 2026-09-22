using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record EventStats(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("skipped")] int Skipped);
