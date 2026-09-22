using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using Xunit;

namespace RecordingCopyNet.Tests.Data;

public class EventsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly EventsRepository _repo;

    public EventsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rc-net-{Guid.NewGuid():N}.db");
        var db = new Db(Options.Create(new AppConfig { DbPath = _dbPath, DataDir = Path.GetTempPath() }));
        db.InitializeSchema();
        _repo = new EventsRepository(db);
    }

    [Fact]
    public void LogEvent_ThenGetEvent_RoundTrips()
    {
        var id = _repo.LogEvent("recording.completed", "uuid-1", "Standup", "host@x.com", "{\"raw\":true}");

        var loaded = _repo.GetEvent(id);

        Assert.NotNull(loaded);
        Assert.Equal("recording.completed", loaded!.EventType);
        Assert.Equal("uuid-1", loaded.MeetingUuid);
        Assert.Equal("received", loaded.Status);
    }

    [Fact]
    public void UpdateEvent_OnlyAppliesProvidedFields()
    {
        var id = _repo.LogEvent("recording.completed", "uuid-2", "Topic", null, null);

        _repo.UpdateEvent(id, new EventUpdateFields { Status = "completed", TransferFilesUploaded = 3 });

        var loaded = _repo.GetEvent(id)!;
        Assert.Equal("completed", loaded.Status);
        Assert.Equal(3, loaded.TransferFilesUploaded);
        Assert.Null(loaded.TransferError);
    }

    [Fact]
    public void AppendLog_AccumulatesJsonArray()
    {
        var id = _repo.LogEvent("recording.completed", "uuid-3", "Topic", null, null);

        _repo.AppendLog(id, "first line");
        _repo.AppendLog(id, "second line");

        var loaded = _repo.GetEvent(id)!;
        Assert.Contains("first line", loaded.TransferLogs);
        Assert.Contains("second line", loaded.TransferLogs);
    }

    [Fact]
    public void ListEvents_ReturnsNewestFirst_RespectingLimitAndOffset()
    {
        _repo.LogEvent("a", "u1", null, null, null);
        _repo.LogEvent("b", "u2", null, null, null);
        _repo.LogEvent("c", "u3", null, null, null);

        var page = _repo.ListEvents(limit: 2, offset: 0);

        Assert.Equal(2, page.Count);
        Assert.Equal("u3", page[0].MeetingUuid);
        Assert.Equal("u2", page[1].MeetingUuid);
    }

    [Fact]
    public void GetStats_CountsByStatus()
    {
        var id1 = _repo.LogEvent("a", "u1", null, null, null);
        var id2 = _repo.LogEvent("b", "u2", null, null, null);
        _repo.UpdateEvent(id1, new EventUpdateFields { Status = "completed" });
        _repo.UpdateEvent(id2, new EventUpdateFields { Status = "failed" });

        var stats = _repo.GetStats();

        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(0, stats.Skipped);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
