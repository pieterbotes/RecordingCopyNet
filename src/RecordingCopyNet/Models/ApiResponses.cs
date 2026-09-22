using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record ConfiguredResponse([property: JsonPropertyName("configured")] bool Configured);
public record OkResponse([property: JsonPropertyName("ok")] bool Ok);
public record ErrorResponse([property: JsonPropertyName("error")] string Error);
public record VersionResponse([property: JsonPropertyName("version")] string Version);

public record SettingsResponse(
    [property: JsonPropertyName("settings")] IReadOnlyDictionary<string, string?> Settings,
    [property: JsonPropertyName("zoomConfigured")] bool ZoomConfigured,
    [property: JsonPropertyName("googleConfigured")] bool GoogleConfigured);

public record OkTrueMessageResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message);

public record OkFalseErrorResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string Error);

public record SubscribersResponse([property: JsonPropertyName("subscribers")] int Subscribers);
public record RestartResponse([property: JsonPropertyName("status")] string Status);

public record GoogleTestResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("sharedDrive")] bool SharedDrive,
    [property: JsonPropertyName("impersonating")] string? Impersonating,
    [property: JsonPropertyName("driveId")] string? DriveId);
