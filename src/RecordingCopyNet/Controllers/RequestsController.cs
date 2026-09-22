using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/requests")]
public class RequestsController : ControllerBase
{
    private readonly IRequestsRepository _repo;
    private readonly ITransferService _transferService;

    public RequestsController(IRequestsRepository repo, ITransferService transferService)
    {
        _repo = repo;
        _transferService = transferService;
    }

    [HttpPost]
    public ActionResult Create([FromBody] CreateRequestBody body)
    {
        if (string.IsNullOrEmpty(body.Name) || string.IsNullOrEmpty(body.Surname) || string.IsNullOrEmpty(body.Email)
            || string.IsNullOrEmpty(body.MeetingId) || string.IsNullOrEmpty(body.MeetingDate))
        {
            return BadRequest(new ErrorResponse("Missing required fields: name, surname, email, meeting_id, meeting_date"));
        }

        try
        {
            var id = _repo.CreateRequest(body.Name, body.Surname, body.Email, body.MeetingId, body.MeetingDate, body.Reason);
            return Ok(new CreateRequestResponse(id));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ErrorResponse(ex.Message));
        }
    }

    [HttpGet]
    public ActionResult List([FromQuery] int? limit, [FromQuery] int? offset)
    {
        try
        {
            var clampedLimit = Math.Min(limit ?? 50, 200);
            var requests = _repo.ListRequests(clampedLimit, offset ?? 0);
            return Ok(new { requests });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ErrorResponse(ex.Message));
        }
    }

    [HttpPost("{id:long}/transfer")]
    public async Task Transfer(long id, CancellationToken ct)
    {
        var request = _repo.GetRequest(id);
        if (request == null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsJsonAsync(new ErrorResponse("Request not found"), ct);
            return;
        }

        SseWriter.SetHeaders(Response);
        _repo.UpdateRequest(id, new RequestUpdateFields { Status = "transferring" });
        await SseWriter.WriteDataAsync(Response, new { type = "progress", message = "Starting transfer..." }, ct);

        try
        {
            var result = await _transferService.TransferMeetingAsync(request.MeetingId, msg =>
            {
                SseWriter.WriteDataAsync(Response, new { type = "progress", message = msg }, ct).GetAwaiter().GetResult();
            }, ct);

            _repo.UpdateRequest(id, new RequestUpdateFields
            {
                Status = "completed",
                TransferFolderName = result.FolderName,
                TransferFolderId = result.FolderId,
                TransferFilesUploaded = result.FilesUploaded,
            });
            await SseWriter.WriteDataAsync(Response, new { type = "complete", result }, ct);
        }
        catch (Exception ex)
        {
            _repo.UpdateRequest(id, new RequestUpdateFields { Status = "failed", TransferError = ex.Message });
            await SseWriter.WriteDataAsync(Response, new { type = "error", message = ex.Message }, ct);
        }
    }
}
