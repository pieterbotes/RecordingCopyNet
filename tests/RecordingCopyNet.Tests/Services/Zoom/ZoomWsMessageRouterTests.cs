using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomWsMessageRouterTests
{
    private readonly ZoomWsMessageRouter _router = new();

    [Fact]
    public void Route_RecognizesHeartbeatAck()
    {
        var result = _router.Route("""{"module":"heartbeat"}""");
        Assert.Equal(ZoomWsMessageKind.Heartbeat, result.Kind);
    }

    [Fact]
    public void Route_RecognizesBuildConnectionSuccess()
    {
        var result = _router.Route("""{"module":"build_connection","success":true,"content":"ok"}""");
        Assert.Equal(ZoomWsMessageKind.BuildConnectionSuccess, result.Kind);
    }

    [Fact]
    public void Route_RecognizesBuildConnectionFailure_EvenWithContentPresent()
    {
        // Regression case from spec §9 / docs §15.11: a failed handshake can
        // still carry a non-empty "content" field. Must branch on `success`.
        var result = _router.Route("""{"module":"build_connection","success":false,"content":"Invalid Token"}""");
        Assert.Equal(ZoomWsMessageKind.BuildConnectionFailure, result.Kind);
    }

    [Fact]
    public void Route_UnwrapsDoubleEncodedRecordingCompletedEvent()
    {
        var raw = """
            {"module":"message","content":"{\"event\":\"recording.completed\",\"payload\":{\"object\":{\"uuid\":\"abc123\",\"topic\":\"Team Standup\",\"host_email\":\"user@example.com\"}}}"}
            """;

        var result = _router.Route(raw.Trim());

        Assert.Equal(ZoomWsMessageKind.RecordingCompleted, result.Kind);
        Assert.Equal("recording.completed", result.EventName);
        Assert.NotNull(result.Payload);
        Assert.Equal("abc123", result.Payload!.Value.GetProperty("payload").GetProperty("object").GetProperty("uuid").GetString());
    }

    [Fact]
    public void Route_IgnoresNonRecordingCompletedEvents()
    {
        var raw = """{"module":"message","content":"{\"event\":\"meeting.started\",\"payload\":{}}"}""";
        var result = _router.Route(raw);
        Assert.Equal(ZoomWsMessageKind.Ignored, result.Kind);
        Assert.Equal("meeting.started", result.EventName);
    }

    [Fact]
    public void Route_ReturnsParseError_OnInvalidTopLevelJson()
    {
        var result = _router.Route("not json at all");
        Assert.Equal(ZoomWsMessageKind.ParseError, result.Kind);
    }

    [Fact]
    public void Route_ReturnsParseError_OnMalformedDoubleEncodedContent()
    {
        var result = _router.Route("""{"module":"message","content":"{not valid json"}""");
        Assert.Equal(ZoomWsMessageKind.ParseError, result.Kind);
    }

    [Fact]
    public void Route_ReturnsIgnored_ForUnknownTopLevelModule()
    {
        var result = _router.Route("""{"module":"something_else"}""");
        Assert.Equal(ZoomWsMessageKind.Ignored, result.Kind);
    }
}
