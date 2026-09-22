using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Services;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/transfer")]
public class TransferController : ControllerBase
{
    private readonly ITransferService _transferService;

    public TransferController(ITransferService transferService) => _transferService = transferService;

    [HttpPost("{meetingId}")]
    public async Task Transfer(string meetingId, CancellationToken ct)
    {
        SseWriter.SetHeaders(Response);

        try
        {
            var result = await _transferService.TransferMeetingAsync(meetingId, msg =>
            {
                SseWriter.WriteDataAsync(Response, new { type = "progress", message = msg }, ct).GetAwaiter().GetResult();
            }, ct);

            await SseWriter.WriteDataAsync(Response, new { type = "complete", result }, ct);
        }
        catch (Exception ex)
        {
            await SseWriter.WriteDataAsync(Response, new { type = "error", message = ex.Message }, ct);
        }
    }
}
