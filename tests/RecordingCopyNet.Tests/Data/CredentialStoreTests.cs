using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using RecordingCopyNet.Security;
using Xunit;

namespace RecordingCopyNet.Tests.Data;

public class CredentialStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly CredentialStore _store;

    public CredentialStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rc-net-{Guid.NewGuid():N}.db");
        var config = new AppConfig { DbPath = _dbPath, DataDir = Path.GetTempPath() };
        _db = new Db(Options.Create(config));
        _db.InitializeSchema();
        var cipher = new AesGcmFieldCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _store = new CredentialStore(_db, cipher);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsZoomCredentials()
    {
        _store.Save(CredentialType.Zoom, new Dictionary<string, string?>
        {
            ["account_id"] = "acct-123",
            ["client_id"] = "client-abc",
            ["client_secret"] = "secret-xyz",
        });

        var loaded = _store.Load(CredentialType.Zoom);

        Assert.NotNull(loaded);
        Assert.Equal("acct-123", loaded!["account_id"]);
        Assert.Equal("client-abc", loaded["client_id"]);
        Assert.Equal("secret-xyz", loaded["client_secret"]);
    }

    [Fact]
    public void Load_ReturnsNull_WhenNothingSaved()
    {
        Assert.Null(_store.Load(CredentialType.Google));
    }

    [Fact]
    public void Exists_ReflectsSaveAndDelete()
    {
        Assert.False(_store.Exists(CredentialType.Settings));

        _store.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "abc" });
        Assert.True(_store.Exists(CredentialType.Settings));

        _store.Delete(CredentialType.Settings);
        Assert.False(_store.Exists(CredentialType.Settings));
    }

    [Fact]
    public void Save_OverwritesPreviousValues()
    {
        _store.Save(CredentialType.Settings, new Dictionary<string, string?> { ["default_zoom_user"] = "a@x.com" });
        _store.Save(CredentialType.Settings, new Dictionary<string, string?> { ["default_zoom_user"] = "b@x.com" });

        var loaded = _store.Load(CredentialType.Settings);
        Assert.Equal("b@x.com", loaded!["default_zoom_user"]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
