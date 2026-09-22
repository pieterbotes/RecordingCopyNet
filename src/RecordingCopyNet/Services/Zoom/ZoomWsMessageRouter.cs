using System.Text.Json;

namespace RecordingCopyNet.Services.Zoom;

public enum ZoomWsMessageKind { Heartbeat, BuildConnectionSuccess, BuildConnectionFailure, RecordingCompleted, Ignored, ParseError }

public record ZoomWsRoutedMessage(ZoomWsMessageKind Kind, string? EventName, JsonElement? Payload, string? RawExcerpt);

public class ZoomWsMessageRouter
{
    public ZoomWsRoutedMessage Route(string rawJson)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(rawJson);
        }
        catch (JsonException)
        {
            return new ZoomWsRoutedMessage(ZoomWsMessageKind.ParseError, null, null, Excerpt(rawJson));
        }

        var module = root.TryGetProperty("module", out var moduleEl) ? moduleEl.GetString() : null;

        if (module == "heartbeat")
            return new ZoomWsRoutedMessage(ZoomWsMessageKind.Heartbeat, null, null, null);

        if (module == "build_connection")
        {
            var success = root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.True;
            return new ZoomWsRoutedMessage(
                success ? ZoomWsMessageKind.BuildConnectionSuccess : ZoomWsMessageKind.BuildConnectionFailure,
                null, root, Excerpt(rawJson));
        }

        if (module == "message" && root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            JsonElement inner;
            try
            {
                inner = JsonSerializer.Deserialize<JsonElement>(contentEl.GetString()!);
            }
            catch (JsonException)
            {
                return new ZoomWsRoutedMessage(ZoomWsMessageKind.ParseError, null, null, Excerpt(contentEl.GetString() ?? ""));
            }

            var eventName = inner.TryGetProperty("event", out var eventEl) ? eventEl.GetString() : null;
            return eventName == "recording.completed"
                ? new ZoomWsRoutedMessage(ZoomWsMessageKind.RecordingCompleted, eventName, inner, null)
                : new ZoomWsRoutedMessage(ZoomWsMessageKind.Ignored, eventName, inner, null);
        }

        return new ZoomWsRoutedMessage(ZoomWsMessageKind.Ignored, module, root, Excerpt(rawJson));
    }

    private static string Excerpt(string raw) => raw.Length > 500 ? raw[..500] : raw;
}
