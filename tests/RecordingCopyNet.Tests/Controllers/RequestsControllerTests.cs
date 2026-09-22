using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class RequestsControllerTests
{
    private class SpyRequestsRepository : IRequestsRepository
    {
        public long NextId = 1;
        public Dictionary<long, RequestRecord> Rows = new();
        public int? LastLimit, LastOffset;

        public long CreateRequest(string name, string surname, string email, string meetingId, string meetingDate, string? reason)
        {
            var id = NextId++;
            Rows[id] = new RequestRecord { Id = id, Name = name, Surname = surname, Email = email, MeetingId = meetingId, MeetingDate = meetingDate, Reason = reason, Status = "pending" };
            return id;
        }

        public void UpdateRequest(long id, RequestUpdateFields fields)
        {
            var row = Rows[id];
            if (fields.Status != null) row.Status = fields.Status;
            if (fields.TransferError != null) row.TransferError = fields.TransferError;
            if (fields.TransferFolderName != null) row.TransferFolderName = fields.TransferFolderName;
            if (fields.TransferFolderId != null) row.TransferFolderId = fields.TransferFolderId;
            if (fields.TransferFilesUploaded != null) row.TransferFilesUploaded = fields.TransferFilesUploaded;
        }

        public List<RequestRecord> ListRequests(int limit = 50, int offset = 0) { LastLimit = limit; LastOffset = offset; return Rows.Values.ToList(); }
        public RequestRecord? GetRequest(long id) => Rows.GetValueOrDefault(id);
    }

    private class FakeTransferService : ITransferService
    {
        public Exception? ThrowInstead;
        public Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default)
        {
            onProgress?.Invoke("working...");
            return ThrowInstead != null
                ? Task.FromException<TransferResult>(ThrowInstead)
                : Task.FromResult(new TransferResult("folder", "id", 1));
        }
    }

    [Fact]
    public void Create_ReturnsBadRequest_WhenRequiredFieldsMissing()
    {
        var controller = new RequestsController(new SpyRequestsRepository(), new FakeTransferService());
        var result = controller.Create(new CreateRequestBody { Name = "Jane" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Create_ReturnsId_OnSuccess()
    {
        var controller = new RequestsController(new SpyRequestsRepository(), new FakeTransferService());
        var body = new CreateRequestBody { Name = "Jane", Surname = "Doe", Email = "j@x.com", MeetingId = "m1", MeetingDate = "2026-09-01" };

        var result = Assert.IsType<OkObjectResult>(controller.Create(body));
        Assert.Equal(1L, ((CreateRequestResponse)result.Value!).Id);
    }

    [Fact]
    public void List_ClampsLimitTo200()
    {
        var repo = new SpyRequestsRepository();
        var controller = new RequestsController(repo, new FakeTransferService());

        controller.List(limit: 999, offset: null);

        Assert.Equal(200, repo.LastLimit);
    }

    [Fact]
    public async Task Transfer_Returns404_WhenRequestMissing()
    {
        var repo = new SpyRequestsRepository();
        var controller = new RequestsController(repo, new FakeTransferService());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        await controller.Transfer(999, CancellationToken.None);

        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task Transfer_WritesCompleteFrame_AndUpdatesRequest_OnSuccess()
    {
        var repo = new SpyRequestsRepository();
        var id = repo.CreateRequest("Jane", "Doe", "j@x.com", "m1", "2026-09-01", null);
        var controller = new RequestsController(repo, new FakeTransferService());
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        await controller.Transfer(id, CancellationToken.None);

        Assert.Equal("completed", repo.Rows[id].Status);
        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.Contains("\"type\":\"complete\"", text);
    }

    [Fact]
    public async Task Transfer_WritesErrorFrame_AndMarksFailed_OnFailure()
    {
        var repo = new SpyRequestsRepository();
        var id = repo.CreateRequest("Jane", "Doe", "j@x.com", "m1", "2026-09-01", null);
        var controller = new RequestsController(repo, new FakeTransferService { ThrowInstead = new InvalidOperationException("upload failed") });
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        await controller.Transfer(id, CancellationToken.None);

        Assert.Equal("failed", repo.Rows[id].Status);
        Assert.Equal("upload failed", repo.Rows[id].TransferError);
    }
}
