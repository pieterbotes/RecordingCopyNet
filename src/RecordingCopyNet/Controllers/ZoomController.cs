using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using RecordingCopyNet.Services.Zoom;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/zoom")]
public class ZoomController : ControllerBase
{
    private readonly IZoomAuthService _zoomAuth;
    private readonly IZoomRecordingsService _recordings;
    private readonly IZoomWebSocketController _ws;
    private readonly ICredentialStore _store;

    public ZoomController(IZoomAuthService zoomAuth, IZoomRecordingsService recordings, IZoomWebSocketController ws, ICredentialStore store)
    {
        _zoomAuth = zoomAuth;
        _recordings = recordings;
        _ws = ws;
        _store = store;
    }

    [HttpPost("test")]
    public async Task<ActionResult> Test()
    {
        try
        {
            await _zoomAuth.GetAccessTokenAsync();
            return Ok(new OkTrueMessageResponse(true, "Zoom connection successful"));
        }
        catch (Exception ex)
        {
            return BadRequest(new OkFalseErrorResponse(false, ex.Message));
        }
    }

    [HttpGet("recordings")]
    public async Task<ActionResult> GetRecordings([FromQuery] string? userId, [FromQuery] string? from, [FromQuery] string? to)
    {
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new ErrorResponse("userId query parameter is required"));

        try
        {
            var meetings = await _recordings.ListRecordingsAsync(userId, from, to);
            return Ok(new { meetings });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ErrorResponse(ex.Message));
        }
    }

    [HttpGet("websocket/status")]
    public ActionResult GetWebSocketStatus()
    {
        var info = _ws.GetDebugInfo();
        var settings = _store.Load(CredentialType.Settings);
        var url = settings != null && settings.TryGetValue("zoom_websocket_url", out var u) ? u : null;
        return Ok(new { status = info.Status, connectedAt = info.ConnectedAt, counters = info.Counters, transfers = info.Transfers, log = info.Log, url });
    }

    [HttpGet("websocket/stream")]
    public async Task Stream(CancellationToken ct)
    {
        SseWriter.SetHeaders(Response);

        var info = _ws.GetDebugInfo();
        var settings = _store.Load(CredentialType.Settings);
        var url = settings != null && settings.TryGetValue("zoom_websocket_url", out var u) ? u : null;
        await SseWriter.WriteNamedEventAsync(Response, "snapshot", new
        {
            status = info.Status, connectedAt = info.ConnectedAt, counters = info.Counters,
            transfers = info.Transfers, log = info.Log, url,
        }, ct);

        using var subscription = _ws.SubscribeDebugLog(async frame =>
        {
            await Response.WriteAsync(frame, ct);
            await Response.Body.FlushAsync(ct);
        });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await SseWriter.WriteCommentAsync(Response, "ping", ct);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected — `using` above unsubscribes on the way out
        }
    }

    [HttpGet("websocket/subscribers")]
    public ActionResult GetSubscribers() => Ok(new SubscribersResponse(_ws.SseSubscriberCount));

    [HttpPost("websocket/stop")]
    public async Task<ActionResult<string>> Stop()
    {
        await _ws.StopConnectionAsync();
        return Ok(_ws.GetStatus());
    }

    [HttpPost("websocket/start")]
    public async Task<ActionResult<string>> Start()
    {
        await _ws.StopConnectionAsync();
        await _ws.StartConnectionAsync();
        return Ok(_ws.GetStatus());
    }

    [HttpPost("websocket/restart")]
    public async Task<ActionResult> Restart()
    {
        await _ws.StopConnectionAsync();
        await _ws.StartConnectionAsync();
        return Ok(new RestartResponse(_ws.GetStatus()));
    }
}
