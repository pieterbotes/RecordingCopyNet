using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly IEventsRepository _repo;

    public EventsController(IEventsRepository repo) => _repo = repo;

    [HttpGet("stats")]
    public ActionResult<EventStats> GetStats()
    {
        try { return Ok(_repo.GetStats()); }
        catch (Exception ex) { return StatusCode(500, new ErrorResponse(ex.Message)); }
    }

    [HttpGet("{id:long}")]
    public ActionResult GetEvent(long id)
    {
        try
        {
            var evt = _repo.GetEvent(id);
            return evt == null ? NotFound(new ErrorResponse("Event not found")) : Ok(evt);
        }
        catch (Exception ex) { return StatusCode(500, new ErrorResponse(ex.Message)); }
    }

    [HttpGet]
    public ActionResult GetEvents([FromQuery] int? limit, [FromQuery] int? offset)
    {
        try
        {
            var clampedLimit = Math.Min(limit ?? 50, 200);
            var events = _repo.ListEvents(clampedLimit, offset ?? 0);
            return Ok(new { events });
        }
        catch (Exception ex) { return StatusCode(500, new ErrorResponse(ex.Message)); }
    }
}
