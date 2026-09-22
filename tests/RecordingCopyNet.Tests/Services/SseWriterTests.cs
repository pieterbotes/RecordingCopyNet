using Microsoft.AspNetCore.Http;
using RecordingCopyNet.Services;
using Xunit;

namespace RecordingCopyNet.Tests.Services;

public class SseWriterTests
{
    [Fact]
    public async Task WriteNamedEventAsync_WritesEventAndDataLines()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await SseWriter.WriteNamedEventAsync(context.Response, "snapshot", new { status = "connected" }, CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.Equal("event: snapshot\ndata: {\"status\":\"connected\"}\n\n", text);
    }

    [Fact]
    public async Task WriteDataAsync_WritesUnnamedDataFrame()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await SseWriter.WriteDataAsync(context.Response, new { type = "progress", message = "hi" }, CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.Equal("data: {\"type\":\"progress\",\"message\":\"hi\"}\n\n", text);
    }

    [Fact]
    public void SetHeaders_SetsSseHeadersIncludingProxyBufferingOptOut()
    {
        var context = new DefaultHttpContext();

        SseWriter.SetHeaders(context.Response);

        Assert.Equal("text/event-stream", context.Response.Headers.ContentType.ToString());
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"].ToString());
    }
}
