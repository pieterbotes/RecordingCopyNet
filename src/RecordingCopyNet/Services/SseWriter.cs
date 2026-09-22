using System.Text.Json;

namespace RecordingCopyNet.Services;

public static class SseWriter
{
    public static void SetHeaders(Microsoft.AspNetCore.Http.HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // stop reverse proxies from buffering the stream
    }

    public static async Task WriteNamedEventAsync(Microsoft.AspNetCore.Http.HttpResponse response, string eventName, object data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteDataAsync(Microsoft.AspNetCore.Http.HttpResponse response, object data, CancellationToken ct)
    {
        await response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteCommentAsync(Microsoft.AspNetCore.Http.HttpResponse response, string comment, CancellationToken ct)
    {
        await response.WriteAsync($": {comment}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
