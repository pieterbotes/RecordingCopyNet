using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class EventsControllerTests
{
    private class SpyEventsRepository : IEventsRepository
    {
        public int? LastLimit, LastOffset;
        public List<EventRecord> ToReturn = new();
        public EventStats Stats = new(3, 1, 1, 1);
        public EventRecord? EventToReturn;

        public long LogEvent(string eventType, string? meetingUuid, string? meetingTopic, string? hostEmail, string? rawPayload) => 1;
        public void UpdateEvent(long id, EventUpdateFields fields) { }
        public void AppendLog(long id, string message) { }
        public List<EventRecord> ListEvents(int limit = 50, int offset = 0) { LastLimit = limit; LastOffset = offset; return ToReturn; }
        public EventRecord? GetEvent(long id) => EventToReturn;
        public EventStats GetStats() => Stats;
    }

    [Fact]
    public void GetStats_ReturnsRepositoryStats()
    {
        var repo = new SpyEventsRepository();
        var controller = new EventsController(repo);

        var result = Assert.IsType<OkObjectResult>(controller.GetStats().Result);
        Assert.Equal(3, ((EventStats)result.Value!).Total);
    }

    [Fact]
    public void GetEvent_ReturnsNotFound_WhenMissing()
    {
        var controller = new EventsController(new SpyEventsRepository());
        var result = controller.GetEvent(999);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetEvent_ReturnsEvent_WhenFound()
    {
        var repo = new SpyEventsRepository { EventToReturn = new EventRecord { Id = 5, EventType = "recording.completed" } };
        var controller = new EventsController(repo);

        var result = Assert.IsType<OkObjectResult>(controller.GetEvent(5));
        Assert.Equal(5, ((EventRecord)result.Value!).Id);
    }

    [Fact]
    public void GetEvents_ClampsLimitTo200_AndDefaultsOffsetToZero()
    {
        var repo = new SpyEventsRepository();
        var controller = new EventsController(repo);

        controller.GetEvents(limit: 500, offset: null);

        Assert.Equal(200, repo.LastLimit);
        Assert.Equal(0, repo.LastOffset);
    }

    [Fact]
    public void GetEvents_DefaultsLimitTo50_WhenNotProvided()
    {
        var repo = new SpyEventsRepository();
        var controller = new EventsController(repo);

        controller.GetEvents(limit: null, offset: null);

        Assert.Equal(50, repo.LastLimit);
    }
}
