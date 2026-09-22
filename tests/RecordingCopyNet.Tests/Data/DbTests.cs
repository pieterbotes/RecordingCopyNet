using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using Xunit;

namespace RecordingCopyNet.Tests.Data;

public class DbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public DbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rc-net-{Guid.NewGuid():N}.db");
        var config = new AppConfig { DbPath = _dbPath, DataDir = Path.GetTempPath() };
        _db = new Db(Options.Create(config));
    }

    [Fact]
    public void InitializeSchema_CreatesAllExpectedTables()
    {
        _db.InitializeSchema();

        using var conn = _db.CreateOpenConnection();
        var tables = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));

        Assert.Contains("events", tables);
        Assert.Contains("requests", tables);
        Assert.Contains("credentials_zoom", tables);
        Assert.Contains("credentials_google", tables);
        Assert.Contains("credentials_settings", tables);
    }

    [Fact]
    public void InitializeSchema_IsIdempotent()
    {
        _db.InitializeSchema();
        _db.InitializeSchema(); // must not throw on second call
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
