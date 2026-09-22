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

        // The debug-log subscription callback and the periodic keep-alive ping below both
        // write to this SAME HttpResponse.Body from potentially concurrent contexts (a
        // broadcast can fire at any time relative to the 30s ping timer). Two concurrent
        // writes to one Kestrel response body can throw, and SseBroadcastHub's
        // catch-and-evict logic then silently drops this subscriber — losing the live
        // debug log exactly when it's busiest. Serialize the two writers against each other.
        using var writeLock = new SemaphoreSlim(1, 1);

        using var subscription = _ws.SubscribeDebugLog(async frame =>
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await Response.WriteAsync(frame, ct);
                await Response.Body.FlushAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
        });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await writeLock.WaitAsync(ct);
                try
                {
                    await SseWriter.WriteCommentAsync(Response, "ping", ct);
                }
                finally
                {
                    writeLock.Release();
                }
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
        // A bare `string` return value from Ok(...) gets picked up by MVC's
        // StringOutputFormatter ahead of the JSON formatter, producing
        // `Content-Type: text/plain` with an unquoted body — not the JSON string
        // the original Node app returns (res.json(getWsStatus())) and that
        // public/js/api.js's `await res.json()` requires. JsonResult forces JSON.
        return new JsonResult(_ws.GetStatus());
    }

    [HttpPost("websocket/start")]
    public async Task<ActionResult<string>> Start()
    {
        await _ws.StopConnectionAsync();
        await _ws.StartConnectionAsync();
        return new JsonResult(_ws.GetStatus());
    }

    [HttpPost("websocket/restart")]
    public async Task<ActionResult> Restart()
    {
        await _ws.StopConnectionAsync();
        await _ws.StartConnectionAsync();
        return Ok(new RestartResponse(_ws.GetStatus()));
    }
}
