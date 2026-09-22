using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record DebugLogEntry(
    [property: JsonPropertyName("time")] string Time,
    [property: JsonPropertyName("msg")] string Msg);

public record ZoomWsCounters(
    [property: JsonPropertyName("messages")] int Messages,
    [property: JsonPropertyName("heartbeats")] int Heartbeats,
    [property: JsonPropertyName("events")] int Events,
    [property: JsonPropertyName("errors")] int Errors);

public record ZoomWsQueueInfo(
    [property: JsonPropertyName("active")] int Active,
    [property: JsonPropertyName("queued")] int Queued,
    [property: JsonPropertyName("limit")] int Limit);

public record ZoomWsDebugInfo(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("connectedAt")] string? ConnectedAt,
    [property: JsonPropertyName("counters")] ZoomWsCounters Counters,
    [property: JsonPropertyName("transfers")] ZoomWsQueueInfo Transfers,
    [property: JsonPropertyName("log")] List<DebugLogEntry> Log);
