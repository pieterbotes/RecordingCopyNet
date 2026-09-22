using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using Xunit;

namespace RecordingCopyNet.Tests.Data;

public class RequestsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RequestsRepository _repo;

    public RequestsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rc-net-{Guid.NewGuid():N}.db");
        var db = new Db(Options.Create(new AppConfig { DbPath = _dbPath, DataDir = Path.GetTempPath() }));
        db.InitializeSchema();
        _repo = new RequestsRepository(db);
    }

    [Fact]
    public void CreateRequest_ThenGetRequest_RoundTrips()
    {
        var id = _repo.CreateRequest("Jane", "Doe", "jane@x.com", "meeting-1", "2026-09-01", "wanted a copy");

        var loaded = _repo.GetRequest(id);

        Assert.NotNull(loaded);
        Assert.Equal("Jane", loaded!.Name);
        Assert.Equal("meeting-1", loaded.MeetingId);
        Assert.Equal("pending", loaded.Status);
    }

    [Fact]
    public void UpdateRequest_OnlyAppliesProvidedFields()
    {
        var id = _repo.CreateRequest("Jane", "Doe", "jane@x.com", "meeting-2", "2026-09-01", null);

        _repo.UpdateRequest(id, new RequestUpdateFields { Status = "completed", TransferFilesUploaded = 5 });

        var loaded = _repo.GetRequest(id)!;
        Assert.Equal("completed", loaded.Status);
        Assert.Equal(5, loaded.TransferFilesUploaded);
        Assert.Null(loaded.TransferError);
    }

    [Fact]
    public void ListRequests_ReturnsNewestFirst()
    {
        _repo.CreateRequest("A", "A", "a@x.com", "m1", "2026-09-01", null);
        _repo.CreateRequest("B", "B", "b@x.com", "m2", "2026-09-01", null);

        var page = _repo.ListRequests(limit: 50, offset: 0);

        Assert.Equal("m2", page[0].MeetingId);
        Assert.Equal("m1", page[1].MeetingId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
