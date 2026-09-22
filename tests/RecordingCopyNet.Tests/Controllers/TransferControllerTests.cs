using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class TransferControllerTests
{
    private class FakeTransferService : ITransferService
    {
        public Exception? ThrowInstead;
        public Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default)
        {
            onProgress?.Invoke("Step 1");
            return ThrowInstead != null
                ? Task.FromException<TransferResult>(ThrowInstead)
                : Task.FromResult(new TransferResult("folder", "id", 1));
        }
    }

    private static (TransferController, MemoryStream) Build(FakeTransferService transfer)
    {
        var controller = new TransferController(transfer);
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return (controller, body);
    }

    [Fact]
    public async Task Transfer_WritesProgressThenCompleteFrames()
    {
        var (controller, body) = Build(new FakeTransferService());

        await controller.Transfer("meeting-1", CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.Contains("\"type\":\"progress\"", text);
        Assert.Contains("\"type\":\"complete\"", text);
    }

    [Fact]
    public async Task Transfer_WritesErrorFrame_OnFailure()
    {
        var (controller, body) = Build(new FakeTransferService { ThrowInstead = new InvalidOperationException("boom") });

        await controller.Transfer("meeting-1", CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.Contains("\"type\":\"error\"", text);
        Assert.Contains("boom", text);
    }
}
