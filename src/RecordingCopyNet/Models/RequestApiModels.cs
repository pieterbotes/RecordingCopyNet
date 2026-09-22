using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class CreateRequestBody
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("surname")] public string? Surname { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("meeting_id")] public string? MeetingId { get; set; }
    [JsonPropertyName("meeting_date")] public string? MeetingDate { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public record CreateRequestResponse([property: JsonPropertyName("id")] long Id);
