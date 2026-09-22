# RecordingCopy .NET Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild RecordingCopy (Zoom → Google Drive recording transfer app) as an ASP.NET Core 8 application with full feature parity, in a new repo at `C:\Coding\RecordingCopyNet`.

**Architecture:** ASP.NET Core 8 Web API (MVC Controllers) hosting a singleton `BackgroundService` Zoom WebSocket listener, Dapper + SQLite for persistence, AES-GCM field-level encryption for credentials, the official Google.Apis.Drive.v3 SDK for Drive operations, and the existing `public/` static HTML/JS ported into `wwwroot/` unchanged.

**Tech Stack:** .NET 8 LTS, ASP.NET Core MVC, Dapper, Microsoft.Data.Sqlite, Google.Apis.Drive.v3, Google.Apis.Auth, System.Net.WebSockets.ClientWebSocket, xUnit.

**Spec:** `C:\Coding\RecordingCopyNet\docs\superpowers\specs\2026-09-21-recordingcopy-dotnet-port-design.md`

## Global Constraints

- .NET 8 LTS only (spec §2).
- MVC Controllers, not Minimal APIs (spec §2).
- Reuse `public/*.html` and `public/js/*.js` in `wwwroot/` essentially unchanged — the API's JSON shapes must match the Node app's **exactly**, including its casing inconsistencies (spec §2, §13). This is a hard constraint threaded through every controller task below — see the endpoint casing table in Task 17.
- Dapper + raw SQL over `Microsoft.Data.Sqlite`, no EF Core (spec §2).
- Credential encryption: AES-GCM with a locally-generated key file at `data/.key`, not DPAPI, not ASP.NET Core Data Protection (spec §2, §8).
- Windows Service hosting via `Microsoft.Extensions.Hosting.WindowsServices`, but the same binary must also run as a plain console app for local dev (spec §2, §16).
- No new features beyond the Node app; no schema changes beyond what's needed to express the same fields in C# (spec §3).
- Every API-facing model/response type must use explicit `[JsonPropertyName]` attributes matching the *exact* casing the Node endpoint returns — never rely on ASP.NET Core's default camelCase policy, because the ported frontend JS expects specific casing per endpoint (some snake_case DB rows, some camelCase service results). Global JSON naming policy is set to `null` (no automatic transform) so every field's casing is explicit and deliberate.
- Package versions: do not guess specific NuGet version numbers. Each task that adds a package uses `dotnet add package <Name>` with no version pin, then records the resolved version that lands in the `.csproj` in that task's commit — this satisfies spec §17's open risk about pinning.

---

## Task 1: Solution scaffolding, AppConfig, minimal host

**Files:**
- Create: `RecordingCopyNet.sln`
- Create: `src/RecordingCopyNet/RecordingCopyNet.csproj`
- Create: `src/RecordingCopyNet/Program.cs`
- Create: `src/RecordingCopyNet/Config/AppConfig.cs`
- Create: `src/RecordingCopyNet/appsettings.json`
- Create: `src/RecordingCopyNet/wwwroot/.gitkeep`
- Create: `tests/RecordingCopyNet.Tests/RecordingCopyNet.Tests.csproj`
- Test: `tests/RecordingCopyNet.Tests/Config/AppConfigTests.cs`
- Create: `.gitignore`

**Interfaces:**
- Produces: `AppConfig` class (bound from `appsettings.json` section `"AppConfig"`) with properties `int Port`, `string DataDir`, `string DbPath`, `string TempDir`, `string ZoomAuthUrl`, `string ZoomApiBase`, `int DefaultDateRangeDays`. Every later task that needs config reads `IOptions<AppConfig>`.

- [ ] **Step 1: Create the solution and project structure**

```bash
cd "C:\Coding\RecordingCopyNet"
dotnet new sln -n RecordingCopyNet
dotnet new webapi -o src/RecordingCopyNet -controllers --use-program-main false
dotnet new xunit -o tests/RecordingCopyNet.Tests
dotnet sln add src/RecordingCopyNet/RecordingCopyNet.csproj
dotnet sln add tests/RecordingCopyNet.Tests/RecordingCopyNet.Tests.csproj
cd tests/RecordingCopyNet.Tests
dotnet add reference ../../src/RecordingCopyNet/RecordingCopyNet.csproj
cd "C:\Coding\RecordingCopyNet"
```

Delete the template's `WeatherForecast.cs` and `Controllers/WeatherForecastController.cs` if `dotnet new webapi` generated them — this app defines its own controllers from Task 17 onward.

- [ ] **Step 2: Write `.gitignore`**

```
bin/
obj/
data/
*.user
.vs/
```

- [ ] **Step 3: Write `src/RecordingCopyNet/Config/AppConfig.cs`**

```csharp
namespace RecordingCopyNet.Config;

public class AppConfig
{
    public int Port { get; set; } = 3900;
    public string DataDir { get; set; } = "data";
    public string DbPath { get; set; } = "data/app.db";
    public string TempDir { get; set; } = "data/temp";
    public string ZoomAuthUrl { get; set; } = "https://zoom.us/oauth/token";
    public string ZoomApiBase { get; set; } = "https://api.zoom.us/v2";
    public int DefaultDateRangeDays { get; set; } = 30;

    /// Resolves DataDir/DbPath/TempDir to absolute paths rooted at baseDir.
    /// Node resolves these with path.join(__dirname, ...); we do the same
    /// relative to the app's base directory so the app works regardless of cwd.
    public void ResolvePaths(string baseDir)
    {
        DataDir = Path.GetFullPath(Path.Combine(baseDir, DataDir));
        DbPath = Path.GetFullPath(Path.Combine(baseDir, DbPath));
        TempDir = Path.GetFullPath(Path.Combine(baseDir, TempDir));
    }
}
```

- [ ] **Step 4: Write `src/RecordingCopyNet/appsettings.json`**

```json
{
  "AppConfig": {
    "Port": 3900,
    "DataDir": "data",
    "DbPath": "data/app.db",
    "TempDir": "data/temp",
    "ZoomAuthUrl": "https://zoom.us/oauth/token",
    "ZoomApiBase": "https://api.zoom.us/v2",
    "DefaultDateRangeDays": 30
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

- [ ] **Step 5: Write `src/RecordingCopyNet/Program.cs`**

```csharp
using RecordingCopyNet.Config;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));
builder.Services.PostConfigure<AppConfig>(cfg => cfg.ResolvePaths(AppContext.BaseDirectory));

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var port = context.Configuration.GetSection("AppConfig")["Port"];
    options.ListenAnyIP(int.Parse(port ?? "3900"));
});

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory-style integration tests in later tasks.
public partial class Program { }
```

- [ ] **Step 6: Write the failing test for `AppConfig`**

`tests/RecordingCopyNet.Tests/Config/AppConfigTests.cs`

```csharp
using RecordingCopyNet.Config;
using Xunit;

namespace RecordingCopyNet.Tests.Config;

public class AppConfigTests
{
    [Fact]
    public void DefaultsMatchNodeConfigJs()
    {
        var config = new AppConfig();

        Assert.Equal(3900, config.Port);
        Assert.Equal("https://zoom.us/oauth/token", config.ZoomAuthUrl);
        Assert.Equal("https://api.zoom.us/v2", config.ZoomApiBase);
        Assert.Equal(30, config.DefaultDateRangeDays);
    }

    [Fact]
    public void ResolvePathsMakesDataDirAbsoluteUnderBaseDir()
    {
        var config = new AppConfig { DataDir = "data", DbPath = "data/app.db", TempDir = "data/temp" };
        var baseDir = Path.Combine(Path.GetTempPath(), "rc-net-test-base");

        config.ResolvePaths(baseDir);

        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "data")), config.DataDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "data", "app.db")), config.DbPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "data", "temp")), config.TempDir);
    }
}
```

- [ ] **Step 7: Run the test to verify it fails (project doesn't compile yet if steps done out of order — otherwise confirm pass immediately since this is scaffolding, not red/green TDD)**

Run: `dotnet test tests/RecordingCopyNet.Tests`
Expected: builds and passes once Steps 3-5 are in place — this task is infrastructure, so "red" here means "doesn't compile without AppConfig", which Step 3 already fixed. Confirm PASS.

- [ ] **Step 8: Verify the host runs and listens on the configured port**

```bash
dotnet run --project src/RecordingCopyNet &
sleep 3
curl -i http://localhost:3900/
```

Expected: a response (404 is fine — no static files yet — the point is Kestrel is listening on port 3900, not connection-refused). Stop the running process afterward.

- [ ] **Step 9: Commit**

```bash
git add .
git commit -m "Scaffold ASP.NET Core solution with AppConfig and minimal host"
```

---

## Task 2: SQLite schema (Db.cs)

**Files:**
- Create: `src/RecordingCopyNet/Data/Db.cs`
- Test: `tests/RecordingCopyNet.Tests/Data/DbTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `AppConfig` (Task 1) via `IOptions<AppConfig>`.
- Produces: `Db` class with `SqliteConnection CreateOpenConnection()` and `void InitializeSchema()`. Every repository task from here on takes `Db` in its constructor and calls `CreateOpenConnection()` per operation (SQLite connections are cheap; don't share one connection across concurrent callers).

- [ ] **Step 1: Add the SQLite package**

```bash
cd src/RecordingCopyNet
dotnet add package Microsoft.Data.Sqlite
cd "C:\Coding\RecordingCopyNet"
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Data/DbTests.cs`

```csharp
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
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter DbTests`
Expected: FAIL — `Db` does not exist yet.

- [ ] **Step 4: Write `src/RecordingCopyNet/Data/Db.cs`**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;

namespace RecordingCopyNet.Data;

public class Db
{
    private readonly string _connectionString;
    private readonly string _dataDir;

    public Db(IOptions<AppConfig> config)
    {
        var cfg = config.Value;
        _dataDir = cfg.DataDir;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = cfg.DbPath }.ToString();
    }

    public SqliteConnection CreateOpenConnection()
    {
        if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // Table shapes mirror lib/events.js, lib/requests.js, and lib/store.js's
    // CREDENTIAL_SCHEMA in the original Node app. credentials_* tables start
    // with just an id column here; CredentialStore (Task 3) adds the
    // per-field _encrypted / plain columns the same way store.js's
    // migrateSchema() does (ALTER TABLE ADD COLUMN IF NOT EXISTS-equivalent).
    public void InitializeSchema()
    {
        using var conn = CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                meeting_uuid TEXT,
                meeting_topic TEXT,
                host_email TEXT,
                received_at TEXT DEFAULT (datetime('now')),
                raw_payload TEXT,
                status TEXT DEFAULT 'received',
                skip_reason TEXT,
                transfer_started_at TEXT,
                transfer_completed_at TEXT,
                transfer_folder_name TEXT,
                transfer_folder_id TEXT,
                transfer_files_uploaded INTEGER,
                transfer_error TEXT,
                transfer_logs TEXT
            );

            CREATE TABLE IF NOT EXISTS requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                surname TEXT NOT NULL,
                email TEXT NOT NULL,
                meeting_id TEXT NOT NULL,
                meeting_date TEXT NOT NULL,
                reason TEXT,
                status TEXT DEFAULT 'pending',
                created_at TEXT DEFAULT (datetime('now')),
                transfer_error TEXT,
                transfer_folder_name TEXT,
                transfer_folder_id TEXT,
                transfer_files_uploaded INTEGER
            );

            CREATE TABLE IF NOT EXISTS credentials_zoom (id INTEGER PRIMARY KEY CHECK (id = 1));
            CREATE TABLE IF NOT EXISTS credentials_google (id INTEGER PRIMARY KEY CHECK (id = 1));
            CREATE TABLE IF NOT EXISTS credentials_settings (id INTEGER PRIMARY KEY CHECK (id = 1));
            """;
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter DbTests`
Expected: PASS

- [ ] **Step 6: Wire into DI and call schema init at startup — modify `Program.cs`**

Add after `builder.Services.PostConfigure<AppConfig>(...)`:

```csharp
builder.Services.AddSingleton<Db>();
```

Add after `var app = builder.Build();`:

```csharp
app.Services.GetRequiredService<Db>().InitializeSchema();
```

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "Add SQLite schema initialization (Db.cs)"
```

---

## Task 3: Credential encryption and store (CredentialStore)

**Files:**
- Create: `src/RecordingCopyNet/Security/IEncryptionKeyProvider.cs`
- Create: `src/RecordingCopyNet/Security/FileEncryptionKeyProvider.cs`
- Create: `src/RecordingCopyNet/Security/IFieldCipher.cs`
- Create: `src/RecordingCopyNet/Security/AesGcmFieldCipher.cs`
- Create: `src/RecordingCopyNet/Data/CredentialSchema.cs`
- Create: `src/RecordingCopyNet/Data/ICredentialStore.cs`
- Create: `src/RecordingCopyNet/Data/CredentialStore.cs`
- Test: `tests/RecordingCopyNet.Tests/Security/AesGcmFieldCipherTests.cs`
- Test: `tests/RecordingCopyNet.Tests/Data/CredentialStoreTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `Db` (Task 2), `AppConfig` (Task 1).
- Produces:
  - `enum CredentialType { Zoom, Google, Settings }`
  - `ICredentialStore` with `bool Exists(CredentialType type)`, `void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields)`, `IReadOnlyDictionary<string, string?>? Load(CredentialType type)`, `void Delete(CredentialType type)`.
  - Every later task needing credentials (ZoomAuthService, GoogleAuthService, TransferService, controllers) takes `ICredentialStore` in its constructor and reads fields by the exact names in `CredentialSchema` (e.g. `fields["client_id"]`).

- [ ] **Step 1: Write `src/RecordingCopyNet/Data/CredentialSchema.cs`**

Field names and required/encrypted flags are a direct port of `CREDENTIAL_SCHEMA` in the Node app's `lib/store.js`.

```csharp
namespace RecordingCopyNet.Data;

public enum CredentialType { Zoom, Google, Settings }

public record CredentialFieldDef(string Name, bool Required, bool Encrypted);

public static class CredentialSchema
{
    public static readonly IReadOnlyDictionary<CredentialType, IReadOnlyList<CredentialFieldDef>> Fields =
        new Dictionary<CredentialType, IReadOnlyList<CredentialFieldDef>>
        {
            [CredentialType.Zoom] = new[]
            {
                new CredentialFieldDef("account_id", true, true),
                new CredentialFieldDef("client_id", true, true),
                new CredentialFieldDef("client_secret", true, true),
            },
            [CredentialType.Google] = new[]
            {
                new CredentialFieldDef("client_email", true, true),
                new CredentialFieldDef("private_key", true, true),
                new CredentialFieldDef("project_id", false, false),
                new CredentialFieldDef("client_id", false, false),
            },
            [CredentialType.Settings] = new[]
            {
                new CredentialFieldDef("google_folder_id", false, false),
                new CredentialFieldDef("google_impersonate_email", false, false),
                new CredentialFieldDef("default_zoom_user", false, false),
                new CredentialFieldDef("transfer_all_users", false, false),
                new CredentialFieldDef("zoom_websocket_url", false, false),
            },
        };

    public static string TableName(CredentialType type) => $"credentials_{type.ToString().ToLowerInvariant()}";

    public static string ColumnName(CredentialFieldDef field) => field.Encrypted ? $"{field.Name}_encrypted" : field.Name;

    public static bool TryParse(string typeName, out CredentialType type) =>
        Enum.TryParse(typeName, ignoreCase: true, out type) && Fields.ContainsKey(type);
}
```

- [ ] **Step 2: Write the failing test for the cipher**

`tests/RecordingCopyNet.Tests/Security/AesGcmFieldCipherTests.cs`

```csharp
using System.Security.Cryptography;
using RecordingCopyNet.Security;
using Xunit;

namespace RecordingCopyNet.Tests.Security;

public class AesGcmFieldCipherTests
{
    [Fact]
    public void Decrypt_ReturnsOriginalPlaintext_AfterEncrypt()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = new AesGcmFieldCipher(key);

        var ciphertext = cipher.Encrypt("super-secret-value");
        var plaintext = cipher.Decrypt(ciphertext);

        Assert.Equal("super-secret-value", plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachCall()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = new AesGcmFieldCipher(key);

        var a = cipher.Encrypt("same-value");
        var b = cipher.Encrypt("same-value");

        Assert.NotEqual(a, b); // fresh nonce per call
    }

    [Fact]
    public void Decrypt_ThrowsOnWrongKey()
    {
        var cipher1 = new AesGcmFieldCipher(RandomNumberGenerator.GetBytes(32));
        var cipher2 = new AesGcmFieldCipher(RandomNumberGenerator.GetBytes(32));

        var ciphertext = cipher1.Encrypt("value");

        Assert.ThrowsAny<CryptographicException>(() => cipher2.Decrypt(ciphertext));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter AesGcmFieldCipherTests`
Expected: FAIL — `AesGcmFieldCipher` does not exist.

- [ ] **Step 4: Write the cipher and key provider**

`src/RecordingCopyNet/Security/IFieldCipher.cs`

```csharp
namespace RecordingCopyNet.Security;

public interface IFieldCipher
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertextBase64);
}
```

`src/RecordingCopyNet/Security/AesGcmFieldCipher.cs`

```csharp
using System.Security.Cryptography;
using System.Text;

namespace RecordingCopyNet.Security;

// Layout: base64( nonce[12] || ciphertext || tag[16] )
public class AesGcmFieldCipher : IFieldCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmFieldCipher(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes (AES-256)", nameof(key));
        _key = key;
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[NonceSize + cipherBytes.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(cipherBytes, 0, combined, NonceSize, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + cipherBytes.Length, TagSize);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string ciphertextBase64)
    {
        var combined = Convert.FromBase64String(ciphertextBase64);
        var nonce = combined.AsSpan(0, NonceSize);
        var cipherLen = combined.Length - NonceSize - TagSize;
        var cipherBytes = combined.AsSpan(NonceSize, cipherLen);
        var tag = combined.AsSpan(NonceSize + cipherLen, TagSize);

        var plainBytes = new byte[cipherLen];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
```

`src/RecordingCopyNet/Security/IEncryptionKeyProvider.cs`

```csharp
namespace RecordingCopyNet.Security;

public interface IEncryptionKeyProvider
{
    byte[] GetOrCreateKey();
}
```

`src/RecordingCopyNet/Security/FileEncryptionKeyProvider.cs`

```csharp
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;

namespace RecordingCopyNet.Security;

// Mirrors getEncryptionKey() in the Node app's lib/store.js: a random
// 32-byte key generated once and persisted as a local file. Node used
// mode 0o600; the closest functional equivalent on Windows is an ACL
// restricting the file to the current user, applied after creation.
public class FileEncryptionKeyProvider : IEncryptionKeyProvider
{
    private readonly string _keyPath;

    public FileEncryptionKeyProvider(IOptions<AppConfig> config)
    {
        _keyPath = Path.Combine(config.Value.DataDir, ".key");
    }

    public byte[] GetOrCreateKey()
    {
        var dir = Path.GetDirectoryName(_keyPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(_keyPath))
        {
            return Convert.FromHexString(File.ReadAllText(_keyPath).Trim());
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(_keyPath, Convert.ToHexString(key));
        RestrictToCurrentUser(_keyPath);
        return key;
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        security.SetAccessRuleProtection(true, false); // disable inheritance, drop inherited rules
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            identity, System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        fileInfo.SetAccessControl(security);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter AesGcmFieldCipherTests`
Expected: PASS

- [ ] **Step 6: Write the failing test for CredentialStore**

`tests/RecordingCopyNet.Tests/Data/CredentialStoreTests.cs`

```csharp
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
```

- [ ] **Step 7: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter CredentialStoreTests`
Expected: FAIL — `CredentialStore` does not exist.

- [ ] **Step 8: Write `ICredentialStore` and `CredentialStore`**

`src/RecordingCopyNet/Data/ICredentialStore.cs`

```csharp
namespace RecordingCopyNet.Data;

public interface ICredentialStore
{
    bool Exists(CredentialType type);
    void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields);
    IReadOnlyDictionary<string, string?>? Load(CredentialType type);
    void Delete(CredentialType type);
}
```

`src/RecordingCopyNet/Data/CredentialStore.cs`

```csharp
using RecordingCopyNet.Security;

namespace RecordingCopyNet.Data;

// Single-row-per-type table (id=1), upserted. Mirrors lib/store.js: field
// names/required/encrypted flags come from CredentialSchema, columns are
// migrated in (added if missing) the same way store.js's migrateSchema()
// ALTERs columns onto credentials_<type> tables.
public class CredentialStore : ICredentialStore
{
    private readonly Db _db;
    private readonly IFieldCipher _cipher;
    private readonly HashSet<CredentialType> _migrated = new();

    public CredentialStore(Db db, IFieldCipher cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    public bool Exists(CredentialType type) => Load(type) != null;

    public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields)
    {
        EnsureColumns(type);
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();

        var columns = new List<string> { "id" };
        var placeholders = new List<string> { "1" };
        using var cmd = conn.CreateCommand();

        foreach (var def in CredentialSchema.Fields[type])
        {
            if (!fields.TryGetValue(def.Name, out var value) || value is null) continue;
            var column = CredentialSchema.ColumnName(def);
            columns.Add(column);
            var paramName = $"@{column}";
            placeholders.Add(paramName);
            cmd.Parameters.AddWithValue(paramName, def.Encrypted ? _cipher.Encrypt(value) : value);
        }

        cmd.CommandText = $"INSERT OR REPLACE INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)})";
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, string?>? Load(CredentialType type)
    {
        EnsureColumns(type);
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var result = new Dictionary<string, string?>();
        foreach (var def in CredentialSchema.Fields[type])
        {
            var column = CredentialSchema.ColumnName(def);
            var ordinal = reader.GetOrdinal(column);
            if (reader.IsDBNull(ordinal)) { result[def.Name] = null; continue; }
            var raw = reader.GetString(ordinal);
            result[def.Name] = def.Encrypted ? _cipher.Decrypt(raw) : raw;
        }
        return result;
    }

    public void Delete(CredentialType type)
    {
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = 1";
        cmd.ExecuteNonQuery();
    }

    private void EnsureColumns(CredentialType type)
    {
        if (!_migrated.Add(type)) return;
        var table = CredentialSchema.TableName(type);
        using var conn = _db.CreateOpenConnection();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(1));
        }

        foreach (var def in CredentialSchema.Fields[type])
        {
            var column = CredentialSchema.ColumnName(def);
            if (existing.Contains(column)) continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT";
            alter.ExecuteNonQuery();
        }
    }
}
```

- [ ] **Step 9: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter CredentialStoreTests`
Expected: PASS

- [ ] **Step 10: Wire into DI — modify `Program.cs`**

Add after `builder.Services.AddSingleton<Db>();`:

```csharp
builder.Services.AddSingleton<RecordingCopyNet.Security.IEncryptionKeyProvider, RecordingCopyNet.Security.FileEncryptionKeyProvider>();
builder.Services.AddSingleton<RecordingCopyNet.Security.IFieldCipher>(sp =>
    new RecordingCopyNet.Security.AesGcmFieldCipher(
        sp.GetRequiredService<RecordingCopyNet.Security.IEncryptionKeyProvider>().GetOrCreateKey()));
builder.Services.AddSingleton<ICredentialStore, CredentialStore>();
```

- [ ] **Step 11: Commit**

```bash
git add .
git commit -m "Add AES-GCM credential encryption and CredentialStore"
```

---

## Task 4: EventsRepository

**Files:**
- Create: `src/RecordingCopyNet/Models/EventRecord.cs`
- Create: `src/RecordingCopyNet/Models/EventUpdateFields.cs`
- Create: `src/RecordingCopyNet/Models/EventStats.cs`
- Create: `src/RecordingCopyNet/Data/IEventsRepository.cs`
- Create: `src/RecordingCopyNet/Data/EventsRepository.cs`
- Test: `tests/RecordingCopyNet.Tests/Data/EventsRepositoryTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `Db` (Task 2).
- Produces: `IEventsRepository` with `long LogEvent(string eventType, string? meetingUuid, string? meetingTopic, string? hostEmail, string? rawPayload)`, `void UpdateEvent(long id, EventUpdateFields fields)`, `void AppendLog(long id, string message)`, `List<EventRecord> ListEvents(int limit, int offset)`, `EventRecord? GetEvent(long id)`, `EventStats GetStats()`. Consumed by `ZoomWebSocketListener` (Task 15) and `EventsController` (Task 20).
- `EventRecord` properties use `[JsonPropertyName]` with the exact snake_case column names — the Node app serializes these DB rows to JSON unchanged, and the ported frontend JS expects the same field names.

- [ ] **Step 1: Add Dapper**

```bash
cd src/RecordingCopyNet
dotnet add package Dapper
cd "C:\Coding\RecordingCopyNet"
```

- [ ] **Step 2: Write the models**

`src/RecordingCopyNet/Models/EventRecord.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class EventRecord
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "";
    [JsonPropertyName("meeting_uuid")] public string? MeetingUuid { get; set; }
    [JsonPropertyName("meeting_topic")] public string? MeetingTopic { get; set; }
    [JsonPropertyName("host_email")] public string? HostEmail { get; set; }
    [JsonPropertyName("received_at")] public string? ReceivedAt { get; set; }
    [JsonPropertyName("raw_payload")] public string? RawPayload { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "received";
    [JsonPropertyName("skip_reason")] public string? SkipReason { get; set; }
    [JsonPropertyName("transfer_started_at")] public string? TransferStartedAt { get; set; }
    [JsonPropertyName("transfer_completed_at")] public string? TransferCompletedAt { get; set; }
    [JsonPropertyName("transfer_folder_name")] public string? TransferFolderName { get; set; }
    [JsonPropertyName("transfer_folder_id")] public string? TransferFolderId { get; set; }
    [JsonPropertyName("transfer_files_uploaded")] public int? TransferFilesUploaded { get; set; }
    [JsonPropertyName("transfer_error")] public string? TransferError { get; set; }
    [JsonPropertyName("transfer_logs")] public string? TransferLogs { get; set; }
}
```

`src/RecordingCopyNet/Models/EventUpdateFields.cs`

```csharp
namespace RecordingCopyNet.Models;

// Only non-null properties are applied — mirrors the "allowed keys present
// in fields" filter in lib/events.js's updateEvent().
public class EventUpdateFields
{
    public string? Status { get; set; }
    public string? SkipReason { get; set; }
    public string? TransferStartedAt { get; set; }
    public string? TransferCompletedAt { get; set; }
    public string? TransferFolderName { get; set; }
    public string? TransferFolderId { get; set; }
    public int? TransferFilesUploaded { get; set; }
    public string? TransferError { get; set; }
    public string? TransferLogs { get; set; }
}
```

`src/RecordingCopyNet/Models/EventStats.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record EventStats(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("skipped")] int Skipped);
```

- [ ] **Step 3: Write the failing test**

`tests/RecordingCopyNet.Tests/Data/EventsRepositoryTests.cs`

```csharp
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
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter EventsRepositoryTests`
Expected: FAIL — `EventsRepository` does not exist.

- [ ] **Step 5: Write `IEventsRepository` and `EventsRepository`**

`src/RecordingCopyNet/Data/IEventsRepository.cs`

```csharp
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Data;

public interface IEventsRepository
{
    long LogEvent(string eventType, string? meetingUuid, string? meetingTopic, string? hostEmail, string? rawPayload);
    void UpdateEvent(long id, EventUpdateFields fields);
    void AppendLog(long id, string message);
    List<EventRecord> ListEvents(int limit = 50, int offset = 0);
    EventRecord? GetEvent(long id);
    EventStats GetStats();
}
```

`src/RecordingCopyNet/Data/EventsRepository.cs`

```csharp
using System.Text.Json;
using Dapper;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Data;

public class EventsRepository : IEventsRepository
{
    private readonly Db _db;

    public EventsRepository(Db db) => _db = db;

    public long LogEvent(string eventType, string? meetingUuid, string? meetingTopic, string? hostEmail, string? rawPayload)
    {
        using var conn = _db.CreateOpenConnection();
        var sql = """
            INSERT INTO events (event_type, meeting_uuid, meeting_topic, host_email, raw_payload)
            VALUES (@eventType, @meetingUuid, @meetingTopic, @hostEmail, @rawPayload);
            SELECT last_insert_rowid();
            """;
        return conn.ExecuteScalar<long>(sql, new { eventType, meetingUuid, meetingTopic, hostEmail, rawPayload });
    }

    public void UpdateEvent(long id, EventUpdateFields fields)
    {
        var sets = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("id", id);

        void AddIfPresent(string column, object? value)
        {
            if (value is null) return;
            sets.Add($"{column} = @{column}");
            parameters.Add(column, value);
        }

        AddIfPresent("status", fields.Status);
        AddIfPresent("skip_reason", fields.SkipReason);
        AddIfPresent("transfer_started_at", fields.TransferStartedAt);
        AddIfPresent("transfer_completed_at", fields.TransferCompletedAt);
        AddIfPresent("transfer_folder_name", fields.TransferFolderName);
        AddIfPresent("transfer_folder_id", fields.TransferFolderId);
        AddIfPresent("transfer_files_uploaded", fields.TransferFilesUploaded);
        AddIfPresent("transfer_error", fields.TransferError);
        AddIfPresent("transfer_logs", fields.TransferLogs);

        if (sets.Count == 0) return;

        using var conn = _db.CreateOpenConnection();
        conn.Execute($"UPDATE events SET {string.Join(", ", sets)} WHERE id = @id", parameters);
    }

    public void AppendLog(long id, string message)
    {
        using var conn = _db.CreateOpenConnection();
        var existing = conn.ExecuteScalar<string?>("SELECT transfer_logs FROM events WHERE id = @id", new { id });

        List<string> logs;
        try { logs = existing is null ? new() : JsonSerializer.Deserialize<List<string>>(existing) ?? new(); }
        catch (JsonException) { logs = new(); }

        logs.Add(message);
        conn.Execute("UPDATE events SET transfer_logs = @logs WHERE id = @id",
            new { logs = JsonSerializer.Serialize(logs), id });
    }

    public List<EventRecord> ListEvents(int limit = 50, int offset = 0)
    {
        using var conn = _db.CreateOpenConnection();
        var sql = """
            SELECT id, event_type AS EventType, meeting_uuid AS MeetingUuid, meeting_topic AS MeetingTopic,
                   host_email AS HostEmail, received_at AS ReceivedAt, status, skip_reason AS SkipReason,
                   transfer_started_at AS TransferStartedAt, transfer_completed_at AS TransferCompletedAt,
                   transfer_folder_name AS TransferFolderName, transfer_files_uploaded AS TransferFilesUploaded,
                   transfer_error AS TransferError
            FROM events ORDER BY id DESC LIMIT @limit OFFSET @offset
            """;
        return conn.Query<EventRecord>(sql, new { limit, offset }).ToList();
    }

    public EventRecord? GetEvent(long id)
    {
        using var conn = _db.CreateOpenConnection();
        var sql = """
            SELECT id, event_type AS EventType, meeting_uuid AS MeetingUuid, meeting_topic AS MeetingTopic,
                   host_email AS HostEmail, received_at AS ReceivedAt, raw_payload AS RawPayload, status,
                   skip_reason AS SkipReason, transfer_started_at AS TransferStartedAt,
                   transfer_completed_at AS TransferCompletedAt, transfer_folder_name AS TransferFolderName,
                   transfer_folder_id AS TransferFolderId, transfer_files_uploaded AS TransferFilesUploaded,
                   transfer_error AS TransferError, transfer_logs AS TransferLogs
            FROM events WHERE id = @id
            """;
        return conn.QuerySingleOrDefault<EventRecord>(sql, new { id });
    }

    public EventStats GetStats()
    {
        using var conn = _db.CreateOpenConnection();
        var sql = """
            SELECT
              COUNT(*) as Total,
              SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) as Completed,
              SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END) as Failed,
              SUM(CASE WHEN status = 'skipped' THEN 1 ELSE 0 END) as Skipped
            FROM events
            """;
        var row = conn.QuerySingle<(int Total, int? Completed, int? Failed, int? Skipped)>(sql);
        return new EventStats(row.Total, row.Completed ?? 0, row.Failed ?? 0, row.Skipped ?? 0);
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter EventsRepositoryTests`
Expected: PASS

- [ ] **Step 7: Wire into DI — modify `Program.cs`**

Add after the `ICredentialStore` registration:

```csharp
builder.Services.AddSingleton<IEventsRepository, EventsRepository>();
```

- [ ] **Step 8: Commit**

```bash
git add .
git commit -m "Add EventsRepository"
```

---

## Task 5: RequestsRepository

**Files:**
- Create: `src/RecordingCopyNet/Models/RequestRecord.cs`
- Create: `src/RecordingCopyNet/Models/RequestUpdateFields.cs`
- Create: `src/RecordingCopyNet/Data/IRequestsRepository.cs`
- Create: `src/RecordingCopyNet/Data/RequestsRepository.cs`
- Test: `tests/RecordingCopyNet.Tests/Data/RequestsRepositoryTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `Db` (Task 2).
- Produces: `IRequestsRepository` with `long CreateRequest(string name, string surname, string email, string meetingId, string meetingDate, string? reason)`, `void UpdateRequest(long id, RequestUpdateFields fields)`, `List<RequestRecord> ListRequests(int limit, int offset)`, `RequestRecord? GetRequest(long id)`. Consumed by `RequestsController` (Task 21).

- [ ] **Step 1: Write the models**

`src/RecordingCopyNet/Models/RequestRecord.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class RequestRecord
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("surname")] public string Surname { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("meeting_id")] public string MeetingId { get; set; } = "";
    [JsonPropertyName("meeting_date")] public string MeetingDate { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("transfer_error")] public string? TransferError { get; set; }
    [JsonPropertyName("transfer_folder_name")] public string? TransferFolderName { get; set; }
    [JsonPropertyName("transfer_folder_id")] public string? TransferFolderId { get; set; }
    [JsonPropertyName("transfer_files_uploaded")] public int? TransferFilesUploaded { get; set; }
}
```

`src/RecordingCopyNet/Models/RequestUpdateFields.cs`

```csharp
namespace RecordingCopyNet.Models;

public class RequestUpdateFields
{
    public string? Status { get; set; }
    public string? TransferError { get; set; }
    public string? TransferFolderName { get; set; }
    public string? TransferFolderId { get; set; }
    public int? TransferFilesUploaded { get; set; }
}
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Data/RequestsRepositoryTests.cs`

```csharp
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
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter RequestsRepositoryTests`
Expected: FAIL — `RequestsRepository` does not exist.

- [ ] **Step 4: Write `IRequestsRepository` and `RequestsRepository`**

`src/RecordingCopyNet/Data/IRequestsRepository.cs`

```csharp
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Data;

public interface IRequestsRepository
{
    long CreateRequest(string name, string surname, string email, string meetingId, string meetingDate, string? reason);
    void UpdateRequest(long id, RequestUpdateFields fields);
    List<RequestRecord> ListRequests(int limit = 50, int offset = 0);
    RequestRecord? GetRequest(long id);
}
```

`src/RecordingCopyNet/Data/RequestsRepository.cs`

```csharp
using Dapper;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Data;

public class RequestsRepository : IRequestsRepository
{
    private readonly Db _db;

    public RequestsRepository(Db db) => _db = db;

    public long CreateRequest(string name, string surname, string email, string meetingId, string meetingDate, string? reason)
    {
        using var conn = _db.CreateOpenConnection();
        var sql = """
            INSERT INTO requests (name, surname, email, meeting_id, meeting_date, reason)
            VALUES (@name, @surname, @email, @meetingId, @meetingDate, @reason);
            SELECT last_insert_rowid();
            """;
        return conn.ExecuteScalar<long>(sql, new { name, surname, email, meetingId, meetingDate, reason });
    }

    public void UpdateRequest(long id, RequestUpdateFields fields)
    {
        var sets = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("id", id);

        void AddIfPresent(string column, object? value)
        {
            if (value is null) return;
            sets.Add($"{column} = @{column}");
            parameters.Add(column, value);
        }

        AddIfPresent("status", fields.Status);
        AddIfPresent("transfer_error", fields.TransferError);
        AddIfPresent("transfer_folder_name", fields.TransferFolderName);
        AddIfPresent("transfer_folder_id", fields.TransferFolderId);
        AddIfPresent("transfer_files_uploaded", fields.TransferFilesUploaded);

        if (sets.Count == 0) return;

        using var conn = _db.CreateOpenConnection();
        conn.Execute($"UPDATE requests SET {string.Join(", ", sets)} WHERE id = @id", parameters);
    }

    public List<RequestRecord> ListRequests(int limit = 50, int offset = 0)
    {
        using var conn = _db.CreateOpenConnection();
        var sql = """
            SELECT id, name, surname, email, meeting_id AS MeetingId, meeting_date AS MeetingDate, reason, status,
                   created_at AS CreatedAt, transfer_error AS TransferError, transfer_folder_name AS TransferFolderName,
                   transfer_folder_id AS TransferFolderId, transfer_files_uploaded AS TransferFilesUploaded
            FROM requests ORDER BY id DESC LIMIT @limit OFFSET @offset
            """;
        return conn.Query<RequestRecord>(sql, new { limit, offset }).ToList();
    }

    public RequestRecord? GetRequest(long id)
    {
        using var conn = _db.CreateOpenConnection();
        var sql = """
            SELECT id, name, surname, email, meeting_id AS MeetingId, meeting_date AS MeetingDate, reason, status,
                   created_at AS CreatedAt, transfer_error AS TransferError, transfer_folder_name AS TransferFolderName,
                   transfer_folder_id AS TransferFolderId, transfer_files_uploaded AS TransferFilesUploaded
            FROM requests WHERE id = @id
            """;
        return conn.QuerySingleOrDefault<RequestRecord>(sql, new { id });
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter RequestsRepositoryTests`
Expected: PASS

- [ ] **Step 6: Wire into DI — modify `Program.cs`**

Add after the `IEventsRepository` registration:

```csharp
builder.Services.AddSingleton<IRequestsRepository, RequestsRepository>();
```

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "Add RequestsRepository"
```

---

## Task 6: ZoomAuthService

**Files:**
- Create: `src/RecordingCopyNet/Services/Zoom/IZoomAuthService.cs`
- Create: `src/RecordingCopyNet/Services/Zoom/ZoomAuthService.cs`
- Create: `tests/RecordingCopyNet.Tests/TestHelpers/FakeHttpMessageHandler.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/Zoom/ZoomAuthServiceTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `ICredentialStore` (Task 3), `AppConfig` (Task 1).
- Produces: `IZoomAuthService` with `Task<string> GetAccessTokenAsync(CancellationToken ct = default)` and `void ClearTokenCache()`. Consumed by `ZoomRecordingsService`, `ZoomDownloadService` (this task's siblings), `ZoomWebSocketListener` (Task 15), and `CredentialsController` (Task 17, to clear the cache when Zoom creds are updated).
- `FakeHttpMessageHandler` (test-only) is reused by Tasks 7 and 8's tests — it takes a `Func<HttpRequestMessage, HttpResponseMessage>` and returns it from `SendAsync`.

- [ ] **Step 1: Write the shared test helper**

`tests/RecordingCopyNet.Tests/TestHelpers/FakeHttpMessageHandler.cs`

```csharp
namespace RecordingCopyNet.Tests.TestHelpers;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_responder(request));
    }
}
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/Zoom/ZoomAuthServiceTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using RecordingCopyNet.Services.Zoom;
using RecordingCopyNet.Tests.TestHelpers;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomAuthServiceTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? ZoomFields;
        public bool Exists(CredentialType type) => type == CredentialType.Zoom && ZoomFields != null;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) =>
            type == CredentialType.Zoom ? ZoomFields : null;
        public void Delete(CredentialType type) { }
    }

    private static ZoomAuthService BuildService(FakeHttpMessageHandler handler, FakeCredentialStore store)
    {
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomAuthUrl = "https://zoom.us/oauth/token" });
        return new ZoomAuthService(client, store, config);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReturnsTokenFromResponse()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "secret" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "tok-123", expires_in = 3600 })
        });
        var service = BuildService(handler, store);

        var token = await service.GetAccessTokenAsync();

        Assert.Equal("tok-123", token);
        Assert.Single(handler.Requests);
        Assert.Equal("Basic", handler.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachesTokenAcrossCalls()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "secret" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "tok-123", expires_in = 3600 })
        });
        var service = BuildService(handler, store);

        await service.GetAccessTokenAsync();
        await service.GetAccessTokenAsync();

        Assert.Single(handler.Requests); // second call served from cache
    }

    [Fact]
    public async Task ClearTokenCache_ForcesRefetchOnNextCall()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "secret" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "tok-123", expires_in = 3600 })
        });
        var service = BuildService(handler, store);
        await service.GetAccessTokenAsync();

        service.ClearTokenCache();
        await service.GetAccessTokenAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWithBodyOnFailure()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "wrong" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid_client")
        });
        var service = BuildService(handler, store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAccessTokenAsync());
        Assert.Contains("invalid_client", ex.Message);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenNoCredentialsConfigured()
    {
        var store = new FakeCredentialStore { ZoomFields = null };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        var service = BuildService(handler, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAccessTokenAsync());
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomAuthServiceTests`
Expected: FAIL — `ZoomAuthService` does not exist.

- [ ] **Step 4: Write `IZoomAuthService` and `ZoomAuthService`**

`src/RecordingCopyNet/Services/Zoom/IZoomAuthService.cs`

```csharp
namespace RecordingCopyNet.Services.Zoom;

public interface IZoomAuthService
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
    void ClearTokenCache();
}
```

`src/RecordingCopyNet/Services/Zoom/ZoomAuthService.cs`

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/auth.js: Server-to-Server OAuth (account_credentials
// grant), in-memory token cache with a 60s expiry safety margin.
public class ZoomAuthService : IZoomAuthService
{
    private readonly HttpClient _http;
    private readonly ICredentialStore _store;
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public ZoomAuthService(HttpClient http, ICredentialStore store, IOptions<AppConfig> config)
    {
        _http = http;
        _store = store;
        _config = config.Value;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_token != null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60))
            return _token;

        await _lock.WaitAsync(ct);
        try
        {
            if (_token != null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60))
                return _token;

            var creds = _store.Load(CredentialType.Zoom)
                ?? throw new InvalidOperationException("Zoom credentials not configured");

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds["client_id"]}:{creds["client_secret"]}"));

            using var request = new HttpRequestMessage(HttpMethod.Post, _config.ZoomAuthUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "account_credentials",
                    ["account_id"] = creds["account_id"] ?? "",
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Zoom auth failed ({(int)response.StatusCode}): {body}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
            _token = json.GetProperty("access_token").GetString();
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32());
            return _token!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void ClearTokenCache()
    {
        _token = null;
        _expiresAt = DateTimeOffset.MinValue;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomAuthServiceTests`
Expected: PASS

- [ ] **Step 6: Wire into DI — modify `Program.cs`**

Add after the `IRequestsRepository` registration. `ZoomAuthService` caches `_token` as instance state — it **must** be a true singleton, the same way `lib/zoom/auth.js`'s `_token` is a module-level variable shared by every caller. `builder.Services.AddHttpClient<TInterface, TImplementation>()` registers the typed client as **transient** (a fresh instance per resolution), which would silently reset the token cache on every injection — so register the `HttpClient` via the named-client factory instead and build the singleton explicitly:

```csharp
builder.Services.AddHttpClient("ZoomAuth");
builder.Services.AddSingleton<RecordingCopyNet.Services.Zoom.IZoomAuthService>(sp =>
    new RecordingCopyNet.Services.Zoom.ZoomAuthService(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("ZoomAuth"),
        sp.GetRequiredService<ICredentialStore>(),
        sp.GetRequiredService<IOptions<AppConfig>>()));
```

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "Add ZoomAuthService"
```

---

## Task 7: ZoomRecordingsService

**Files:**
- Create: `src/RecordingCopyNet/Models/ZoomRecordingFile.cs`
- Create: `src/RecordingCopyNet/Models/ZoomMeetingRecordings.cs`
- Create: `src/RecordingCopyNet/Services/Zoom/IZoomRecordingsService.cs`
- Create: `src/RecordingCopyNet/Services/Zoom/ZoomRecordingsService.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/Zoom/ZoomRecordingsServiceTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `IZoomAuthService` (Task 6), `AppConfig` (Task 1).
- Produces: `IZoomRecordingsService` with `Task<JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct)`, `Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct)`, `Task<string?> GetUserEmailAsync(string userId, CancellationToken ct)`. Consumed by `TransferService` (Task 10) and `ZoomController` (Task 18). `ListRecordingsAsync` returns the raw Zoom JSON array untouched (matches Node's straight passthrough of `data.meetings` to the browse UI), while `GetMeetingRecordingsAsync` returns a typed model since `TransferService` needs specific fields.

- [ ] **Step 1: Write the models**

`src/RecordingCopyNet/Models/ZoomRecordingFile.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class ZoomRecordingFile
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("file_type")] public string? FileType { get; set; }
    [JsonPropertyName("file_extension")] public string? FileExtension { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("recording_start")] public string? RecordingStart { get; set; }
    [JsonPropertyName("recording_end")] public string? RecordingEnd { get; set; }
}
```

`src/RecordingCopyNet/Models/ZoomMeetingRecordings.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class ZoomMeetingRecordings
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
    [JsonPropertyName("start_time")] public string? StartTime { get; set; }
    [JsonPropertyName("duration")] public int? Duration { get; set; }
    [JsonPropertyName("total_size")] public long? TotalSize { get; set; }
    [JsonPropertyName("recording_files")] public List<ZoomRecordingFile> RecordingFiles { get; set; } = new();
}
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/Zoom/ZoomRecordingsServiceTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Services.Zoom;
using RecordingCopyNet.Tests.TestHelpers;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomRecordingsServiceTests
{
    private class FakeZoomAuthService : IZoomAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok-abc");
        public void ClearTokenCache() { }
    }

    [Fact]
    public async Task ListRecordingsAsync_ReturnsMeetingsArrayFromResponse_AndSendsBearerToken()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { meetings = new[] { new { id = "m1", topic = "Standup" } } })
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var meetings = await service.ListRecordingsAsync("user@x.com", null, null);

        Assert.Equal(1, meetings.GetArrayLength());
        Assert.Equal("m1", meetings[0].GetProperty("id").GetString());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("tok-abc", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ListRecordingsAsync_ReturnsEmptyArray_WhenNoMeetingsKey()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { })
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var meetings = await service.ListRecordingsAsync("user@x.com", null, null);

        Assert.Equal(0, meetings.GetArrayLength());
    }

    [Fact]
    public async Task GetMeetingRecordingsAsync_ParsesRecordingFiles()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = 123,
                uuid = "abc==",
                topic = "Standup",
                start_time = "2026-09-01T10:00:00Z",
                recording_files = new[] { new { id = "f1", file_type = "MP4", download_url = "https://x/y" } }
            })
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var meeting = await service.GetMeetingRecordingsAsync("abc==");

        Assert.Equal("Standup", meeting.Topic);
        Assert.Single(meeting.RecordingFiles);
        Assert.Equal("MP4", meeting.RecordingFiles[0].FileType);
    }

    [Fact]
    public async Task GetMeetingRecordingsAsync_ThrowsWithBodyOnFailure()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("meeting not found")
        });
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMeetingRecordingsAsync("bad-id"));
        Assert.Contains("meeting not found", ex.Message);
    }

    [Fact]
    public async Task GetUserEmailAsync_ReturnsNull_OnFailure()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomApiBase = "https://api.zoom.us/v2" });
        var service = new ZoomRecordingsService(client, new FakeZoomAuthService(), config);

        var email = await service.GetUserEmailAsync("user-id");

        Assert.Null(email);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomRecordingsServiceTests`
Expected: FAIL — `ZoomRecordingsService` does not exist.

- [ ] **Step 4: Write `IZoomRecordingsService` and `ZoomRecordingsService`**

`src/RecordingCopyNet/Services/Zoom/IZoomRecordingsService.cs`

```csharp
using System.Text.Json;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services.Zoom;

public interface IZoomRecordingsService
{
    Task<JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default);
    Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default);
    Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default);
}
```

`src/RecordingCopyNet/Services/Zoom/ZoomRecordingsService.cs`

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/recordings.js.
public class ZoomRecordingsService : IZoomRecordingsService
{
    private readonly HttpClient _http;
    private readonly IZoomAuthService _auth;
    private readonly AppConfig _config;

    public ZoomRecordingsService(HttpClient http, IZoomAuthService auth, IOptions<AppConfig> config)
    {
        _http = http;
        _auth = auth;
        _config = config.Value;
    }

    public async Task<JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString("");
        query["page_size"] = "300";
        if (!string.IsNullOrEmpty(from)) query["from"] = from;
        if (!string.IsNullOrEmpty(to)) query["to"] = to;

        var url = $"{_config.ZoomApiBase}/users/{Uri.EscapeDataString(userId)}/recordings?{query}";
        var json = await SendAuthorizedAsync(url, ct);

        return json.TryGetProperty("meetings", out var meetings) ? meetings : JsonDocument.Parse("[]").RootElement;
    }

    public async Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default)
    {
        var url = $"{_config.ZoomApiBase}/meetings/{Uri.EscapeDataString(meetingId)}/recordings";
        var json = await SendAuthorizedAsync(url, ct);
        return json.Deserialize<ZoomMeetingRecordings>() ?? new ZoomMeetingRecordings();
    }

    public async Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default)
    {
        var url = $"{_config.ZoomApiBase}/users/{Uri.EscapeDataString(userId)}";
        var token = await _auth.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
        return json.TryGetProperty("email", out var email) ? email.GetString() : null;
    }

    private async Task<JsonElement> SendAuthorizedAsync(string url, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Zoom API error ({(int)response.StatusCode}): {body}");
        }
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomRecordingsServiceTests`
Expected: PASS

- [ ] **Step 6: Wire into DI — modify `Program.cs`**

Add after the `IZoomAuthService` registration:

```csharp
builder.Services.AddHttpClient<RecordingCopyNet.Services.Zoom.IZoomRecordingsService, RecordingCopyNet.Services.Zoom.ZoomRecordingsService>();
```

This one is fine as the default `AddHttpClient<TInterface,TImplementation>` transient/scoped registration — unlike `ZoomAuthService`, `ZoomRecordingsService` holds no mutable instance state to preserve across calls.

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "Add ZoomRecordingsService"
```

---

## Task 8: ZoomDownloadService

**Files:**
- Create: `src/RecordingCopyNet/Services/Zoom/IZoomDownloadService.cs`
- Create: `src/RecordingCopyNet/Services/Zoom/ZoomDownloadService.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/Zoom/ZoomDownloadServiceTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `IZoomAuthService` (Task 6), `AppConfig` (Task 1).
- Produces: `IZoomDownloadService` with `Task DownloadRecordingFileAsync(string downloadUrl, string destPath, CancellationToken ct)` and `string GetTempDir(string meetingId)`. Consumed by `TransferService` (Task 10).

- [ ] **Step 1: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/Zoom/ZoomDownloadServiceTests.cs`

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Services.Zoom;
using RecordingCopyNet.Tests.TestHelpers;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomDownloadServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rc-net-dl-{Guid.NewGuid():N}");

    private class FakeZoomAuthService : IZoomAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok-abc");
        public void ClearTokenCache() { }
    }

    [Fact]
    public async Task DownloadRecordingFileAsync_WritesResponseBodyToDestPath_AndAppendsTokenToUrl()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
        });
        var client = new HttpClient(handler);
        var service = new ZoomDownloadService(client, new FakeZoomAuthService(), Options.Create(new AppConfig { TempDir = _tempDir }));
        var destPath = Path.Combine(_tempDir, "file.mp4");

        await service.DownloadRecordingFileAsync("https://zoom.us/rec/download/abc", destPath);

        Assert.True(File.Exists(destPath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destPath));
        Assert.Contains("access_token=tok-abc", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadRecordingFileAsync_ThrowsOnFailureStatus()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new HttpClient(handler);
        var service = new ZoomDownloadService(client, new FakeZoomAuthService(), Options.Create(new AppConfig { TempDir = _tempDir }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadRecordingFileAsync("https://zoom.us/rec/download/abc", Path.Combine(_tempDir, "f.mp4")));
    }

    [Fact]
    public void GetTempDir_JoinsMeetingIdUnderConfiguredTempDir()
    {
        var service = new ZoomDownloadService(new HttpClient(), new FakeZoomAuthService(), Options.Create(new AppConfig { TempDir = _tempDir }));

        var dir = service.GetTempDir("meeting-123");

        Assert.Equal(Path.Combine(_tempDir, "meeting-123"), dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomDownloadServiceTests`
Expected: FAIL — `ZoomDownloadService` does not exist.

- [ ] **Step 3: Write `IZoomDownloadService` and `ZoomDownloadService`**

`src/RecordingCopyNet/Services/Zoom/IZoomDownloadService.cs`

```csharp
namespace RecordingCopyNet.Services.Zoom;

public interface IZoomDownloadService
{
    Task DownloadRecordingFileAsync(string downloadUrl, string destPath, CancellationToken ct = default);
    string GetTempDir(string meetingId);
}
```

`src/RecordingCopyNet/Services/Zoom/ZoomDownloadService.cs`

```csharp
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/download.js. Streams the response body straight
// to disk rather than buffering — recording files can be gigabytes.
public class ZoomDownloadService : IZoomDownloadService
{
    private readonly HttpClient _http;
    private readonly IZoomAuthService _auth;
    private readonly AppConfig _config;

    public ZoomDownloadService(HttpClient http, IZoomAuthService auth, IOptions<AppConfig> config)
    {
        _http = http;
        _auth = auth;
        _config = config.Value;
    }

    public async Task DownloadRecordingFileAsync(string downloadUrl, string destPath, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        var url = $"{downloadUrl}?access_token={token}";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Download failed ({(int)response.StatusCode}): {downloadUrl}");

        var dir = Path.GetDirectoryName(destPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        await using var sourceStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(destPath);
        await sourceStream.CopyToAsync(fileStream, ct);
    }

    public string GetTempDir(string meetingId) => Path.Combine(_config.TempDir, meetingId);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomDownloadServiceTests`
Expected: PASS

- [ ] **Step 5: Wire into DI — modify `Program.cs`**

Add after the `IZoomRecordingsService` registration:

```csharp
builder.Services.AddHttpClient<RecordingCopyNet.Services.Zoom.IZoomDownloadService, RecordingCopyNet.Services.Zoom.ZoomDownloadService>();
```

- [ ] **Step 6: Commit**

```bash
git add .
git commit -m "Add ZoomDownloadService"
```

---

## Task 9: Google Drive integration (GoogleAuthService + GoogleDriveService)

**Files:**
- Create: `src/RecordingCopyNet/Models/DriveModels.cs`
- Create: `src/RecordingCopyNet/Services/Google/IGoogleAuthService.cs`
- Create: `src/RecordingCopyNet/Services/Google/GoogleAuthService.cs`
- Create: `src/RecordingCopyNet/Services/Google/IGoogleDriveService.cs`
- Create: `src/RecordingCopyNet/Services/Google/GoogleDriveService.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `ICredentialStore` (Task 3).
- Produces: `IGoogleAuthService` with `DriveService GetDriveService()` and `void ClearDriveClient()`; `IGoogleDriveService` with `Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct)`, `Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct)`, `Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct)`, `Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct)`, `Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct)`. Consumed by `TransferService` (Task 10) and `GoogleController` (Task 19).

**Testing note (deliberate, not a gap):** `GoogleAuthService` and `GoogleDriveService` are thin adapters over the official `Google.Apis.Drive.v3` SDK. Faking the SDK's internal HTTP transport correctly would mean guessing at `Google.Apis.Http` plumbing this plan's author cannot verify compiles without running it against the real package — a bad trade for a wrapper with almost no logic of its own. Per spec §15, these two classes get **no automated unit test**; their correctness is verified by the manual end-to-end smoke test in Task 25, and the logic that's actually worth unit testing (folder naming, MIME mapping, the shared-drive/impersonation quota guard) lives in `TransferService` (Task 10), which is fully unit tested against a fake `IGoogleDriveService`.

- [ ] **Step 1: Add the Google API packages**

```bash
cd src/RecordingCopyNet
dotnet add package Google.Apis.Drive.v3
dotnet add package Google.Apis.Auth
cd "C:\Coding\RecordingCopyNet"
```

- [ ] **Step 2: Write the Drive response models**

`src/RecordingCopyNet/Models/DriveModels.cs`

```csharp
namespace RecordingCopyNet.Models;

public record DriveFolderInfo(string Id, string Name, string? DriveId);
public record DriveFileInfo(string Id, string Name, long? Size);
public record DriveInfo(string Id, string Name);
```

- [ ] **Step 3: Write `IGoogleAuthService` and `GoogleAuthService`**

`src/RecordingCopyNet/Services/Google/IGoogleAuthService.cs`

```csharp
using Google.Apis.Drive.v3;

namespace RecordingCopyNet.Services.Google;

public interface IGoogleAuthService
{
    DriveService GetDriveService();
    void ClearDriveClient();
}
```

`src/RecordingCopyNet/Services/Google/GoogleAuthService.cs`

```csharp
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using RecordingCopyNet.Data;

namespace RecordingCopyNet.Services.Google;

// Direct port of lib/google/auth.js: a JWT service-account credential,
// optionally impersonating a user via domain-wide delegation (the
// "subject" claim), cached until credentials/settings change.
public class GoogleAuthService : IGoogleAuthService
{
    private readonly ICredentialStore _store;
    private DriveService? _client;

    public GoogleAuthService(ICredentialStore store) => _store = store;

    public DriveService GetDriveService()
    {
        if (_client != null) return _client;

        var creds = _store.Load(CredentialType.Google)
            ?? throw new InvalidOperationException("Google credentials not configured");
        var settings = _store.Load(CredentialType.Settings);

        var initializer = new ServiceAccountCredential.Initializer(creds["client_email"])
        {
            Scopes = new[] { DriveService.Scope.Drive },
            User = settings != null && settings.TryGetValue("google_impersonate_email", out var impersonate) ? impersonate : null,
        }.FromPrivateKey(creds["private_key"]);

        var credential = new ServiceAccountCredential(initializer);

        _client = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
        });
        return _client;
    }

    public void ClearDriveClient()
    {
        _client?.Dispose();
        _client = null;
    }
}
```

- [ ] **Step 4: Write `IGoogleDriveService` and `GoogleDriveService`**

`src/RecordingCopyNet/Services/Google/IGoogleDriveService.cs`

```csharp
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services.Google;

public interface IGoogleDriveService
{
    Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default);
    Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default);
    Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default);
    Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default);
    Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default);
}
```

`src/RecordingCopyNet/Services/Google/GoogleDriveService.cs`

```csharp
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using RecordingCopyNet.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace RecordingCopyNet.Services.Google;

// Direct port of lib/google/drive.js.
public class GoogleDriveService : IGoogleDriveService
{
    private readonly IGoogleAuthService _auth;

    public GoogleDriveService(IGoogleAuthService auth) => _auth = auth;

    public async Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var request = drive.Files.Get(folderId);
        request.Fields = "id, name, driveId, parents, spaces";
        request.SupportsAllDrives = true;
        var file = await request.ExecuteAsync(ct);
        return new DriveFolderInfo(file.Id, file.Name, file.DriveId);
    }

    public async Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var body = new DriveFile
        {
            Name = name,
            MimeType = "application/vnd.google-apps.folder",
            Parents = new List<string> { parentFolderId },
        };
        var request = drive.Files.Create(body);
        request.Fields = "id, name, driveId";
        request.SupportsAllDrives = true;
        var file = await request.ExecuteAsync(ct);
        return new DriveFolderInfo(file.Id, file.Name, file.DriveId);
    }

    public async Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var body = new DriveFile { Name = fileName, Parents = new List<string> { folderId } };

        await using var stream = File.OpenRead(filePath);
        var request = drive.Files.Create(body, stream, mimeType);
        request.Fields = "id, name, size";
        request.SupportsAllDrives = true;

        var progress = await request.UploadAsync(ct);
        if (progress.Status != UploadStatus.Completed)
            throw new InvalidOperationException($"Upload of {fileName} failed: {progress.Exception?.Message}");

        var file = request.ResponseBody;
        return new DriveFileInfo(file.Id, file.Name, file.Size);
    }

    public async Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var request = drive.Drives.List();
        request.PageSize = 50;
        request.Fields = "drives(id, name)";
        var result = await request.ExecuteAsync(ct);
        return (result.Drives ?? new List<Google.Apis.Drive.v3.Data.Drive>())
            .Select(d => new DriveInfo(d.Id, d.Name)).ToList();
    }

    public async Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default)
    {
        var drive = _auth.GetDriveService();
        var request = drive.Files.List();
        request.Q = $"'{folderId}' in parents and trashed = false";
        request.Fields = "files(id, name, mimeType)";
        request.PageSize = 10;
        request.IncludeItemsFromAllDrives = true;
        request.SupportsAllDrives = true;
        var result = await request.ExecuteAsync(ct);
        return (result.Files ?? new List<DriveFile>())
            .Select(f => new DriveFileInfo(f.Id, f.Name, f.Size)).ToList();
    }
}
```

- [ ] **Step 5: Build to catch any SDK API mismatches**

Run: `dotnet build src/RecordingCopyNet`
Expected: builds cleanly. If any `Google.Apis.Drive.v3` member name in Step 4 doesn't match the resolved package version (SDK surface does shift between versions), fix the call site here — this is exactly the kind of mismatch the "no automated test" note above anticipates, so a clean build is this task's real acceptance bar, not a green test run.

- [ ] **Step 6: Wire into DI — modify `Program.cs`**

Add after the `IZoomDownloadService` registration:

```csharp
builder.Services.AddSingleton<RecordingCopyNet.Services.Google.IGoogleAuthService, RecordingCopyNet.Services.Google.GoogleAuthService>();
builder.Services.AddSingleton<RecordingCopyNet.Services.Google.IGoogleDriveService, RecordingCopyNet.Services.Google.GoogleDriveService>();
```

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "Add Google Drive integration (GoogleAuthService, GoogleDriveService)"
```

---

## Task 10: TransferService

**Files:**
- Create: `src/RecordingCopyNet/Models/TransferResult.cs`
- Create: `src/RecordingCopyNet/Services/ITransferService.cs`
- Create: `src/RecordingCopyNet/Services/TransferService.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/TransferServiceTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `IZoomRecordingsService`, `IZoomDownloadService` (Tasks 7-8), `IGoogleDriveService` (Task 9), `ICredentialStore` (Task 3).
- Produces: `ITransferService` with `Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct)`. Consumed by `ZoomWebSocketListener` (Task 15), `TransferController` (Task 20), `RequestsController` (Task 21).

- [ ] **Step 1: Write the result model**

`src/RecordingCopyNet/Models/TransferResult.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record TransferResult(
    [property: JsonPropertyName("folderName")] string FolderName,
    [property: JsonPropertyName("folderId")] string? FolderId,
    [property: JsonPropertyName("filesUploaded")] int FilesUploaded);
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/TransferServiceTests.cs`

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services;

public class TransferServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly CredentialStore _credentialStore;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rc-net-transfer-{Guid.NewGuid():N}");

    private class FakeRecordingsService : IZoomRecordingsService
    {
        public ZoomMeetingRecordings? Meeting;
        public Task<System.Text.Json.JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default)
            => Task.FromResult(Meeting!);
        public Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private class FakeDownloadService : IZoomDownloadService
    {
        public List<string> DownloadedTo = new();
        public Task DownloadRecordingFileAsync(string downloadUrl, string destPath, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllBytes(destPath, new byte[] { 9, 9 });
            DownloadedTo.Add(destPath);
            return Task.CompletedTask;
        }
        public string GetTempDir(string meetingId) => Path.Combine(Path.GetTempPath(), "rc-net-transfer-work", meetingId);
    }

    private class FakeDriveService : IGoogleDriveService
    {
        public DriveFolderInfo VerifyResult = new("parent-id", "Parent", "shared-drive-id");
        public List<string> UploadedNames = new();

        public Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default) => Task.FromResult(VerifyResult);
        public Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default) =>
            Task.FromResult(new DriveFolderInfo("new-folder-id", name, "shared-drive-id"));
        public Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default)
        {
            UploadedNames.Add(fileName);
            return Task.FromResult(new DriveFileInfo("file-id", fileName, 100));
        }
        public Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default) => Task.FromResult(new List<DriveInfo>());
        public Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default) => Task.FromResult(new List<DriveFileInfo>());
    }

    public TransferServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"rc-net-{Guid.NewGuid():N}.db");
        var config = new AppConfig { DbPath = _dbPath, DataDir = Path.GetTempPath(), TempDir = _tempDir };
        _db = new Db(Options.Create(config));
        _db.InitializeSchema();
        var cipher = new RecordingCopyNet.Security.AesGcmFieldCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _credentialStore = new CredentialStore(_db, cipher);
    }

    private static ZoomMeetingRecordings SampleMeeting() => new()
    {
        Id = 123,
        Uuid = "uuid-abc",
        Topic = "Team Standup",
        StartTime = "2026-09-01T10:00:00Z",
        RecordingFiles = new List<ZoomRecordingFile>
        {
            new() { Id = "f1", FileType = "MP4", FileExtension = "MP4", DownloadUrl = "https://zoom/f1", FileSize = 100 },
            new() { Id = "f2", FileType = "CHAT", FileExtension = "TXT", DownloadUrl = "https://zoom/f2", FileSize = 10 },
        },
    };

    [Fact]
    public async Task TransferMeetingAsync_HappyPath_UploadsMetadataPlusEachFile()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var download = new FakeDownloadService();
        var drive = new FakeDriveService();
        var service = new TransferService(recordings, download, drive, _credentialStore);

        var result = await service.TransferMeetingAsync("uuid-abc", null);

        Assert.Equal("new-folder-id", result.FolderId);
        Assert.Equal(3, result.FilesUploaded); // metadata.json + 2 recording files
        Assert.Contains("metadata.json", drive.UploadedNames);
        Assert.Contains(drive.UploadedNames, n => n.StartsWith("MP4_f1"));
        Assert.Contains(drive.UploadedNames, n => n.StartsWith("CHAT_f2"));
    }

    [Fact]
    public async Task TransferMeetingAsync_ThrowsWhenGoogleFolderIdNotConfigured()
    {
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var service = new TransferService(recordings, new FakeDownloadService(), new FakeDriveService(), _credentialStore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferMeetingAsync("uuid-abc", null));
        Assert.Contains("Google Drive folder ID not configured", ex.Message);
    }

    [Fact]
    public async Task TransferMeetingAsync_ThrowsWhenNoRecordingFiles()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = new ZoomMeetingRecordings { Uuid = "uuid-abc", RecordingFiles = new() } };
        var service = new TransferService(recordings, new FakeDownloadService(), new FakeDriveService(), _credentialStore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferMeetingAsync("uuid-abc", null));
        Assert.Contains("No recording files found", ex.Message);
    }

    [Fact]
    public async Task TransferMeetingAsync_ThrowsWhenNoSharedDriveAndNoImpersonation()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var drive = new FakeDriveService { VerifyResult = new DriveFolderInfo("parent-id", "Parent", null) }; // not on a shared drive
        var service = new TransferService(recordings, new FakeDownloadService(), drive, _credentialStore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferMeetingAsync("uuid-abc", null));
        Assert.Contains("NOT on a Shared Drive", ex.Message);
    }

    [Fact]
    public async Task TransferMeetingAsync_AllowsNonSharedDrive_WhenImpersonationConfigured()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?>
        {
            ["google_folder_id"] = "parent-id",
            ["google_impersonate_email"] = "svc@example.com",
        });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var drive = new FakeDriveService { VerifyResult = new DriveFolderInfo("parent-id", "Parent", null) };
        var service = new TransferService(recordings, new FakeDownloadService(), drive, _credentialStore);

        var result = await service.TransferMeetingAsync("uuid-abc", null);

        Assert.Equal(3, result.FilesUploaded);
    }

    [Fact]
    public async Task TransferMeetingAsync_SanitizesFolderName()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var meeting = SampleMeeting();
        meeting.Topic = "Q&A: Sprint/Review? <2026>";
        var recordings = new FakeRecordingsService { Meeting = meeting };
        var drive = new FakeDriveService();
        var service = new TransferService(recordings, new FakeDownloadService(), drive, _credentialStore);

        var result = await service.TransferMeetingAsync("uuid-abc", null);

        Assert.DoesNotContain("<", result.FolderName);
        Assert.DoesNotContain("/", result.FolderName);
        Assert.DoesNotContain(":", result.FolderName);
        Assert.StartsWith("2026-09-01", result.FolderName);
    }

    [Fact]
    public async Task TransferMeetingAsync_ReportsProgressMessages()
    {
        _credentialStore.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "parent-id" });
        var recordings = new FakeRecordingsService { Meeting = SampleMeeting() };
        var service = new TransferService(recordings, new FakeDownloadService(), new FakeDriveService(), _credentialStore);
        var messages = new List<string>();

        await service.TransferMeetingAsync("uuid-abc", messages.Add);

        Assert.Contains(messages, m => m.Contains("Fetching meeting recording details"));
        Assert.Contains(messages, m => m.StartsWith("Transfer complete!"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter TransferServiceTests`
Expected: FAIL — `TransferService` does not exist.

- [ ] **Step 4: Write `ITransferService` and `TransferService`**

`src/RecordingCopyNet/Services/ITransferService.cs`

```csharp
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services;

public interface ITransferService
{
    Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default);
}
```

`src/RecordingCopyNet/Services/TransferService.cs`

```csharp
using System.Text.Json;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;

namespace RecordingCopyNet.Services;

// Direct port of lib/transfer.js — see spec §11 for the numbered sequence
// this follows.
public class TransferService : ITransferService
{
    private static readonly Dictionary<string, string> MimeMap = new()
    {
        ["MP4"] = "video/mp4",
        ["M4A"] = "audio/mp4",
        ["CHAT"] = "text/plain",
        ["TRANSCRIPT"] = "text/vtt",
        ["TIMELINE"] = "application/json",
        ["CC"] = "text/vtt",
        ["CSV"] = "text/csv",
    };

    private readonly IZoomRecordingsService _recordings;
    private readonly IZoomDownloadService _download;
    private readonly IGoogleDriveService _drive;
    private readonly ICredentialStore _credentialStore;

    public TransferService(IZoomRecordingsService recordings, IZoomDownloadService download,
        IGoogleDriveService drive, ICredentialStore credentialStore)
    {
        _recordings = recordings;
        _download = download;
        _drive = drive;
        _credentialStore = credentialStore;
    }

    public async Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default)
    {
        void Log(string message) => onProgress?.Invoke(message);

        var settings = _credentialStore.Load(CredentialType.Settings);
        if (settings == null || !settings.TryGetValue("google_folder_id", out var googleFolderId) || string.IsNullOrEmpty(googleFolderId))
            throw new InvalidOperationException("Google Drive folder ID not configured in settings");

        Log("Fetching meeting recording details from Zoom...");
        var meeting = await _recordings.GetMeetingRecordingsAsync(meetingId, ct);

        var files = meeting.RecordingFiles;
        if (files.Count == 0)
            throw new InvalidOperationException("No recording files found for this meeting");

        Log("Verifying target folder storage access...");
        var parentInfo = await _drive.VerifySharedDriveAsync(googleFolderId, ct);
        var hasSharedDrive = !string.IsNullOrEmpty(parentInfo.DriveId);
        var impersonateEmail = settings.TryGetValue("google_impersonate_email", out var imp) ? imp : null;
        var hasImpersonation = !string.IsNullOrEmpty(impersonateEmail);

        if (!hasSharedDrive && !hasImpersonation)
        {
            throw new InvalidOperationException(
                "Target folder is NOT on a Shared Drive and no impersonation email is configured. " +
                "Service account uploads will fail due to 0 storage quota. " +
                "Either use a Shared Drive folder or configure Domain-Wide Delegation in Settings.");
        }

        if (hasSharedDrive) Log($"Target folder \"{parentInfo.Name}\" on Shared Drive: {parentInfo.DriveId}");
        if (hasImpersonation) Log($"Impersonating {impersonateEmail} for storage quota");

        var startDate = meeting.StartTime != null && meeting.StartTime.Length >= 10 ? meeting.StartTime[..10] : "unknown-date";
        var topic = string.IsNullOrEmpty(meeting.Topic) ? "Untitled Meeting" : meeting.Topic;
        var folderName = SanitizeFolderName($"{startDate} - {topic}");

        var tempDir = _download.GetTempDir(meetingId);
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

        var metadata = new
        {
            meeting_id = meeting.Id?.ToString() ?? meeting.Uuid,
            topic = meeting.Topic,
            start_time = meeting.StartTime,
            duration = meeting.Duration,
            total_size = meeting.TotalSize,
            recording_count = files.Count,
            files = files.Select(f => new
            {
                id = f.Id,
                file_type = f.FileType,
                file_extension = f.FileExtension,
                file_size = f.FileSize,
                recording_start = f.RecordingStart,
                recording_end = f.RecordingEnd,
                status = f.Status,
            }),
        };

        var metadataPath = Path.Combine(tempDir, "metadata.json");
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), ct);
        Log("Saved metadata.json");

        var downloadedFiles = new List<(string Path, string Name, string? Type)>();
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.DownloadUrl)) continue;

            var ext = (file.FileExtension ?? file.FileType ?? "bin").ToLowerInvariant();
            var fileName = $"{file.FileType ?? "recording"}_{file.Id}.{ext}";
            var destPath = Path.Combine(tempDir, fileName);

            Log($"Downloading {fileName} ({FormatSize(file.FileSize)})...");
            await _download.DownloadRecordingFileAsync(file.DownloadUrl, destPath, ct);
            downloadedFiles.Add((destPath, fileName, file.FileType));
            Log($"Downloaded {fileName}");
        }

        Log($"Creating Drive folder: {folderName}");
        var folder = await _drive.CreateFolderAsync(folderName, googleFolderId, ct);
        Log($"Created folder: {folder.Name} (id: {folder.Id}, driveId: {folder.DriveId ?? "none"})");

        Log("Uploading metadata.json...");
        await _drive.UploadFileAsync(metadataPath, "metadata.json", folder.Id, "application/json", ct);
        Log("Uploaded metadata.json");

        foreach (var file in downloadedFiles)
        {
            var mime = file.Type != null && MimeMap.TryGetValue(file.Type, out var m) ? m : "application/octet-stream";
            Log($"Uploading {file.Name}...");
            await _drive.UploadFileAsync(file.Path, file.Name, folder.Id, mime, ct);
            Log($"Uploaded {file.Name}");
        }

        Log("Cleaning up temp files...");
        Directory.Delete(tempDir, recursive: true);
        var totalUploaded = downloadedFiles.Count + 1;
        Log($"Transfer complete! {totalUploaded} files uploaded to \"{folderName}\"");

        return new TransferResult(folderName, folder.Id, totalUploaded);
    }

    private static string SanitizeFolderName(string name)
    {
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in name)
            sanitized.Append("<>:\"/\\|?*".Contains(c) ? '_' : c);
        var result = sanitized.ToString();
        return result.Length > 200 ? result[..200] : result;
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null or 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes.Value;
        var i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter TransferServiceTests`
Expected: PASS

- [ ] **Step 6: Wire into DI — modify `Program.cs`**

Add after the Google service registrations:

```csharp
builder.Services.AddSingleton<RecordingCopyNet.Services.ITransferService, RecordingCopyNet.Services.TransferService>();
```

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "Add TransferService"
```

---

## Task 11: ZoomWsMessageRouter (pure message classification)

**Files:**
- Create: `src/RecordingCopyNet/Services/Zoom/ZoomWsMessageRouter.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/Zoom/ZoomWsMessageRouterTests.cs`

**Interfaces:**
- Consumes: nothing (pure function over a raw JSON string).
- Produces: `enum ZoomWsMessageKind { Heartbeat, BuildConnectionSuccess, BuildConnectionFailure, RecordingCompleted, Ignored, ParseError }`; `record ZoomWsRoutedMessage(ZoomWsMessageKind Kind, string? EventName, JsonElement? Payload, string? RawExcerpt)`; `class ZoomWsMessageRouter` with `ZoomWsRoutedMessage Route(string rawJson)`. Consumed by `ZoomWebSocketListener` (Task 15) to decide what to do with each incoming WebSocket text frame, isolated from the actual socket I/O so it's fully unit testable.

This class carries the single trickiest piece of the whole Zoom integration — the spec's §9 rundown of `docs/zoom-websocket-integration.md` §6 and §15.11: Zoom wraps real events as `{"module":"message","content":"<json string>"}` (double-encoded — `content` must be `JSON.parse`'d again), and a `build_connection` reply must be branched on its `success` field, not on whether `content` is present, because a failed auth can still arrive as `{"module":"build_connection","content":"Invalid Token","success":false}` over an apparently-healthy socket.

- [ ] **Step 1: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/Zoom/ZoomWsMessageRouterTests.cs`

```csharp
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomWsMessageRouterTests
{
    private readonly ZoomWsMessageRouter _router = new();

    [Fact]
    public void Route_RecognizesHeartbeatAck()
    {
        var result = _router.Route("""{"module":"heartbeat"}""");
        Assert.Equal(ZoomWsMessageKind.Heartbeat, result.Kind);
    }

    [Fact]
    public void Route_RecognizesBuildConnectionSuccess()
    {
        var result = _router.Route("""{"module":"build_connection","success":true,"content":"ok"}""");
        Assert.Equal(ZoomWsMessageKind.BuildConnectionSuccess, result.Kind);
    }

    [Fact]
    public void Route_RecognizesBuildConnectionFailure_EvenWithContentPresent()
    {
        // Regression case from spec §9 / docs §15.11: a failed handshake can
        // still carry a non-empty "content" field. Must branch on `success`.
        var result = _router.Route("""{"module":"build_connection","success":false,"content":"Invalid Token"}""");
        Assert.Equal(ZoomWsMessageKind.BuildConnectionFailure, result.Kind);
    }

    [Fact]
    public void Route_UnwrapsDoubleEncodedRecordingCompletedEvent()
    {
        var raw = """
            {"module":"message","content":"{\"event\":\"recording.completed\",\"payload\":{\"object\":{\"uuid\":\"abc123\",\"topic\":\"Team Standup\",\"host_email\":\"user@example.com\"}}}"}
            """;

        var result = _router.Route(raw.Trim());

        Assert.Equal(ZoomWsMessageKind.RecordingCompleted, result.Kind);
        Assert.Equal("recording.completed", result.EventName);
        Assert.NotNull(result.Payload);
        Assert.Equal("abc123", result.Payload!.Value.GetProperty("payload").GetProperty("object").GetProperty("uuid").GetString());
    }

    [Fact]
    public void Route_IgnoresNonRecordingCompletedEvents()
    {
        var raw = """{"module":"message","content":"{\"event\":\"meeting.started\",\"payload\":{}}"}""";
        var result = _router.Route(raw);
        Assert.Equal(ZoomWsMessageKind.Ignored, result.Kind);
        Assert.Equal("meeting.started", result.EventName);
    }

    [Fact]
    public void Route_ReturnsParseError_OnInvalidTopLevelJson()
    {
        var result = _router.Route("not json at all");
        Assert.Equal(ZoomWsMessageKind.ParseError, result.Kind);
    }

    [Fact]
    public void Route_ReturnsParseError_OnMalformedDoubleEncodedContent()
    {
        var result = _router.Route("""{"module":"message","content":"{not valid json"}""");
        Assert.Equal(ZoomWsMessageKind.ParseError, result.Kind);
    }

    [Fact]
    public void Route_ReturnsIgnored_ForUnknownTopLevelModule()
    {
        var result = _router.Route("""{"module":"something_else"}""");
        Assert.Equal(ZoomWsMessageKind.Ignored, result.Kind);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomWsMessageRouterTests`
Expected: FAIL — types don't exist yet.

- [ ] **Step 3: Write `ZoomWsMessageRouter`**

`src/RecordingCopyNet/Services/Zoom/ZoomWsMessageRouter.cs`

```csharp
using System.Text.Json;

namespace RecordingCopyNet.Services.Zoom;

public enum ZoomWsMessageKind { Heartbeat, BuildConnectionSuccess, BuildConnectionFailure, RecordingCompleted, Ignored, ParseError }

public record ZoomWsRoutedMessage(ZoomWsMessageKind Kind, string? EventName, JsonElement? Payload, string? RawExcerpt);

public class ZoomWsMessageRouter
{
    public ZoomWsRoutedMessage Route(string rawJson)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(rawJson);
        }
        catch (JsonException)
        {
            return new ZoomWsRoutedMessage(ZoomWsMessageKind.ParseError, null, null, Excerpt(rawJson));
        }

        var module = root.TryGetProperty("module", out var moduleEl) ? moduleEl.GetString() : null;

        if (module == "heartbeat")
            return new ZoomWsRoutedMessage(ZoomWsMessageKind.Heartbeat, null, null, null);

        if (module == "build_connection")
        {
            var success = root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.True;
            return new ZoomWsRoutedMessage(
                success ? ZoomWsMessageKind.BuildConnectionSuccess : ZoomWsMessageKind.BuildConnectionFailure,
                null, root, Excerpt(rawJson));
        }

        if (module == "message" && root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            JsonElement inner;
            try
            {
                inner = JsonSerializer.Deserialize<JsonElement>(contentEl.GetString()!);
            }
            catch (JsonException)
            {
                return new ZoomWsRoutedMessage(ZoomWsMessageKind.ParseError, null, null, Excerpt(contentEl.GetString() ?? ""));
            }

            var eventName = inner.TryGetProperty("event", out var eventEl) ? eventEl.GetString() : null;
            return eventName == "recording.completed"
                ? new ZoomWsRoutedMessage(ZoomWsMessageKind.RecordingCompleted, eventName, inner, null)
                : new ZoomWsRoutedMessage(ZoomWsMessageKind.Ignored, eventName, inner, null);
        }

        return new ZoomWsRoutedMessage(ZoomWsMessageKind.Ignored, module, root, Excerpt(rawJson));
    }

    private static string Excerpt(string raw) => raw.Length > 500 ? raw[..500] : raw;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomWsMessageRouterTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add .
git commit -m "Add ZoomWsMessageRouter"
```

---

## Task 12: EventDedupTracker

**Files:**
- Create: `src/RecordingCopyNet/Services/Zoom/EventDedupTracker.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/Zoom/EventDedupTrackerTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `TimeProvider` (from `Microsoft.Extensions.Time.Testing`-style DI — `TimeProvider.System` in production, `FakeTimeProvider` in tests).
- Produces: `class EventDedupTracker` with `bool TryMarkProcessed(string uuid)` (returns `false` if already processed within the TTL) and `void Forget(string uuid)` (called after a failed transfer so it can be retried). Consumed by `ZoomWebSocketListener` (Task 15).

- [ ] **Step 1: Add the test time-provider package**

```bash
cd tests/RecordingCopyNet.Tests
dotnet add package Microsoft.Extensions.TimeProvider.Testing
cd "C:\Coding\RecordingCopyNet"
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/Zoom/EventDedupTrackerTests.cs`

```csharp
using Microsoft.Extensions.Time.Testing;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class EventDedupTrackerTests
{
    [Fact]
    public void TryMarkProcessed_ReturnsTrue_OnFirstOccurrence()
    {
        var tracker = new EventDedupTracker(new FakeTimeProvider(), TimeSpan.FromHours(1));
        Assert.True(tracker.TryMarkProcessed("uuid-1"));
    }

    [Fact]
    public void TryMarkProcessed_ReturnsFalse_OnDuplicateWithinTtl()
    {
        var tracker = new EventDedupTracker(new FakeTimeProvider(), TimeSpan.FromHours(1));
        tracker.TryMarkProcessed("uuid-1");
        Assert.False(tracker.TryMarkProcessed("uuid-1"));
    }

    [Fact]
    public void TryMarkProcessed_ReturnsTrue_AfterTtlExpires()
    {
        var time = new FakeTimeProvider();
        var tracker = new EventDedupTracker(time, TimeSpan.FromHours(1));
        tracker.TryMarkProcessed("uuid-1");

        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        Assert.True(tracker.TryMarkProcessed("uuid-1"));
    }

    [Fact]
    public void Forget_AllowsImmediateRetryRegardlessOfTtl()
    {
        var tracker = new EventDedupTracker(new FakeTimeProvider(), TimeSpan.FromHours(1));
        tracker.TryMarkProcessed("uuid-1");

        tracker.Forget("uuid-1");

        Assert.True(tracker.TryMarkProcessed("uuid-1"));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter EventDedupTrackerTests`
Expected: FAIL — `EventDedupTracker` does not exist.

- [ ] **Step 4: Write `EventDedupTracker`**

`src/RecordingCopyNet/Services/Zoom/EventDedupTracker.cs`

```csharp
using System.Collections.Concurrent;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of the `_processed` Map + cleanProcessed() in lib/zoom/websocket.js.
public class EventDedupTracker
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processed = new();

    public EventDedupTracker(TimeProvider time, TimeSpan? ttl = null)
    {
        _time = time;
        _ttl = ttl ?? TimeSpan.FromHours(1);
    }

    public bool TryMarkProcessed(string uuid)
    {
        CleanExpired();
        var now = _time.GetUtcNow();
        return _processed.TryAdd(uuid, now);
    }

    public void Forget(string uuid) => _processed.TryRemove(uuid, out _);

    private void CleanExpired()
    {
        var cutoff = _time.GetUtcNow() - _ttl;
        foreach (var (uuid, timestamp) in _processed)
        {
            if (timestamp < cutoff) _processed.TryRemove(uuid, out _);
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter EventDedupTrackerTests`
Expected: PASS

- [ ] **Step 6: Wire into DI — modify `Program.cs`**

Add after the `ITransferService` registration:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RecordingCopyNet.Services.Zoom.EventDedupTracker>();
```

- [ ] **Step 7: Commit**

```bash
git add .
git commit -m "Add EventDedupTracker"
```

---

## Task 13: TransferQueue (bounded-concurrency transfer queue)

**Files:**
- Create: `src/RecordingCopyNet/Services/Zoom/TransferQueue.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/Zoom/TransferQueueTests.cs`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `class TransferQueue` with `(int Active, int Queued, int Limit) GetQueueInfo()` and `void Enqueue(Func<Task> task)`. Consumed by `ZoomWebSocketListener` (Task 15). Default concurrency limit is 3, matching `MAX_CONCURRENT_TRANSFERS` in `lib/zoom/websocket.js`.

- [ ] **Step 1: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/Zoom/TransferQueueTests.cs`

```csharp
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class TransferQueueTests
{
    [Fact]
    public async Task Enqueue_RunsUpToLimitConcurrently_ThenQueuesTheRest()
    {
        var queue = new TransferQueue(limit: 2);
        var gate = new TaskCompletionSource();
        var started = new List<int>();
        var completions = new TaskCompletionSource[4];
        for (var i = 0; i < 4; i++) completions[i] = new TaskCompletionSource();

        for (var i = 0; i < 4; i++)
        {
            var index = i;
            queue.Enqueue(async () =>
            {
                lock (started) started.Add(index);
                await completions[index].Task;
            });
        }

        await Task.Delay(50); // let the first batch start

        var info = queue.GetQueueInfo();
        Assert.Equal(2, info.Active);
        Assert.Equal(2, info.Queued);
        Assert.Equal(2, started.Count);

        completions[started[0]].SetResult();
        completions[started[1]].SetResult();
        await Task.Delay(50);

        Assert.Equal(4, started.Count); // the queued two have now started

        foreach (var tcs in completions) if (!tcs.Task.IsCompleted) tcs.SetResult();
        await Task.Delay(50);

        var finalInfo = queue.GetQueueInfo();
        Assert.Equal(0, finalInfo.Active);
        Assert.Equal(0, finalInfo.Queued);
    }

    [Fact]
    public async Task Enqueue_ContinuesDraining_EvenWhenATaskThrows()
    {
        var queue = new TransferQueue(limit: 1);
        var secondRan = new TaskCompletionSource();

        queue.Enqueue(() => throw new InvalidOperationException("boom"));
        queue.Enqueue(() => { secondRan.SetResult(); return Task.CompletedTask; });

        var completed = await Task.WhenAny(secondRan.Task, Task.Delay(1000));
        Assert.Same(secondRan.Task, completed);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter TransferQueueTests`
Expected: FAIL — `TransferQueue` does not exist.

- [ ] **Step 3: Write `TransferQueue`**

`src/RecordingCopyNet/Services/Zoom/TransferQueue.cs`

```csharp
namespace RecordingCopyNet.Services.Zoom;

// Direct port of enqueueTransfer/drainTransferQueue in lib/zoom/websocket.js.
// A task that throws synchronously or whose returned Task faults must not
// stop the queue from draining — mirrors the Node comment: "task() must
// never reject — it handles its own errors," enforced defensively here too.
public class TransferQueue
{
    private readonly int _limit;
    private readonly object _gate = new();
    private int _active;
    private readonly Queue<Func<Task>> _queue = new();

    public TransferQueue(int limit = 3) => _limit = limit;

    public (int Active, int Queued, int Limit) GetQueueInfo()
    {
        lock (_gate) return (_active, _queue.Count, _limit);
    }

    public void Enqueue(Func<Task> task)
    {
        lock (_gate) _queue.Enqueue(task);
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            Func<Task>? task;
            lock (_gate)
            {
                if (_active >= _limit || _queue.Count == 0) return;
                task = _queue.Dequeue();
                _active++;
            }

            RunOne(task);
        }
    }

    private void RunOne(Func<Task> task)
    {
        Task runTask;
        try { runTask = task(); }
        catch { runTask = Task.CompletedTask; }

        runTask.ContinueWith(_ =>
        {
            lock (_gate) _active--;
            Drain();
        }, TaskScheduler.Default);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter TransferQueueTests`
Expected: PASS

- [ ] **Step 5: Wire into DI — modify `Program.cs`**

Add after the `EventDedupTracker` registration:

```csharp
builder.Services.AddSingleton<RecordingCopyNet.Services.Zoom.TransferQueue>();
```

- [ ] **Step 6: Commit**

```bash
git add .
git commit -m "Add TransferQueue"
```

---

## Task 14: SseBroadcastHub

**Files:**
- Create: `src/RecordingCopyNet/Services/SseBroadcastHub.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/SseBroadcastHubTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `class SseBroadcastHub` with `IDisposable Subscribe(Func<string, Task> writer)`, `Task BroadcastAsync(string eventName, object payload)`, `int SubscriberCount`. A generic pub/sub primitive — not Zoom-specific — instantiated directly by `ZoomWebSocketListener` (Task 15), which owns the actual debug-log ring buffer and current-state payload shape (that's listener-specific business state, not this hub's concern).

- [ ] **Step 1: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/SseBroadcastHubTests.cs`

```csharp
using RecordingCopyNet.Services;
using Xunit;

namespace RecordingCopyNet.Tests.Services;

public class SseBroadcastHubTests
{
    [Fact]
    public async Task BroadcastAsync_DeliversFormattedFrameToSubscribers()
    {
        var hub = new SseBroadcastHub();
        string? received = null;
        hub.Subscribe(frame => { received = frame; return Task.CompletedTask; });

        await hub.BroadcastAsync("append", new { msg = "hello" });

        Assert.Equal("event: append\ndata: {\"msg\":\"hello\"}\n\n", received);
    }

    [Fact]
    public async Task BroadcastAsync_DoesNothing_WhenNoSubscribers()
    {
        var hub = new SseBroadcastHub();
        await hub.BroadcastAsync("append", new { msg = "hello" }); // must not throw
    }

    [Fact]
    public void Subscribe_IncrementsAndDisposeDecrementsSubscriberCount()
    {
        var hub = new SseBroadcastHub();
        Assert.Equal(0, hub.SubscriberCount);

        var subscription = hub.Subscribe(_ => Task.CompletedTask);
        Assert.Equal(1, hub.SubscriberCount);

        subscription.Dispose();
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task BroadcastAsync_RemovesSubscriberWhoseWriterThrows()
    {
        var hub = new SseBroadcastHub();
        hub.Subscribe(_ => throw new IOException("client vanished"));

        await hub.BroadcastAsync("append", new { });

        Assert.Equal(0, hub.SubscriberCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter SseBroadcastHubTests`
Expected: FAIL — `SseBroadcastHub` does not exist.

- [ ] **Step 3: Write `SseBroadcastHub`**

`src/RecordingCopyNet/Services/SseBroadcastHub.cs`

```csharp
using System.Collections.Concurrent;
using System.Text.Json;

namespace RecordingCopyNet.Services;

// Generic SSE pub/sub primitive. Mirrors the _sseClients Set + broadcast()
// in lib/zoom/websocket.js, minus the ring buffer and payload shape, which
// stay owned by the specific listener that uses this hub.
public class SseBroadcastHub
{
    private readonly ConcurrentDictionary<Guid, Func<string, Task>> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public IDisposable Subscribe(Func<string, Task> writer)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = writer;
        return new Subscription(() => _subscribers.TryRemove(id, out _));
    }

    public async Task BroadcastAsync(string eventName, object payload)
    {
        if (_subscribers.IsEmpty) return;
        var frame = $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload)}\n\n";

        foreach (var (id, writer) in _subscribers)
        {
            try { await writer(frame); }
            catch { _subscribers.TryRemove(id, out _); } // client vanished mid-write
        }
    }

    private class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter SseBroadcastHubTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add .
git commit -m "Add SseBroadcastHub"
```

---

## Task 15: ZoomWebSocketListener (BackgroundService glue)

**Files:**
- Create: `src/RecordingCopyNet/Models/ZoomWsDebugInfo.cs`
- Create: `src/RecordingCopyNet/Services/Zoom/IZoomWebSocketController.cs`
- Create: `src/RecordingCopyNet/Services/Zoom/ZoomWebSocketListener.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/Zoom/ZoomWebSocketListenerTests.cs`
- Modify: `src/RecordingCopyNet/RecordingCopyNet.csproj`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:**
- Consumes: `IZoomAuthService` (Task 6), `IZoomRecordingsService` (Task 7), `ITransferService` (Task 10), `ICredentialStore` (Task 3), `IEventsRepository` (Task 4), `ZoomWsMessageRouter` (Task 11), `EventDedupTracker` (Task 12), `TransferQueue` (Task 13), `SseBroadcastHub` (Task 14).
- Produces: `IZoomWebSocketController` with `string GetStatus()`, `ZoomWsDebugInfo GetDebugInfo()`, `IDisposable SubscribeDebugLog(Func<string, Task> writer)`, `int SseSubscriberCount`, `Task StartConnectionAsync()`, `Task StopConnectionAsync()`, `Task RestartConnectionAsync()`. `ZoomWebSocketListener : BackgroundService, IZoomWebSocketController` is registered once and exposed as both, consumed by `ZoomController` (Task 18).

**Naming note (a real gotcha, not a style choice):** `BackgroundService` already defines `StartAsync(CancellationToken)`/`StopAsync(CancellationToken)` as the ASP.NET Core host lifecycle hooks (called once, by the host, on app start/shutdown). The Node app's `/api/zoom/websocket/{start,stop,restart}` endpoints need to control the **WebSocket connection**, not the hosted service itself — those are two different lifecycles. Naming the connection-control methods `StartConnectionAsync` / `StopConnectionAsync` / `RestartConnectionAsync` avoids accidentally overriding (or colliding with) the base class's own members.

- [ ] **Step 1: Write the debug-info models**

`src/RecordingCopyNet/Models/ZoomWsDebugInfo.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record DebugLogEntry(
    [property: JsonPropertyName("time")] string Time,
    [property: JsonPropertyName("msg")] string Msg);

public record ZoomWsCounters(
    [property: JsonPropertyName("messages")] int Messages,
    [property: JsonPropertyName("heartbeats")] int Heartbeats,
    [property: JsonPropertyName("events")] int Events,
    [property: JsonPropertyName("errors")] int Errors);

public record ZoomWsQueueInfo(
    [property: JsonPropertyName("active")] int Active,
    [property: JsonPropertyName("queued")] int Queued,
    [property: JsonPropertyName("limit")] int Limit);

public record ZoomWsDebugInfo(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("connectedAt")] string? ConnectedAt,
    [property: JsonPropertyName("counters")] ZoomWsCounters Counters,
    [property: JsonPropertyName("transfers")] ZoomWsQueueInfo Transfers,
    [property: JsonPropertyName("log")] List<DebugLogEntry> Log);
```

- [ ] **Step 2: Write `IZoomWebSocketController`**

`src/RecordingCopyNet/Services/Zoom/IZoomWebSocketController.cs`

```csharp
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services.Zoom;

public interface IZoomWebSocketController
{
    string GetStatus();
    ZoomWsDebugInfo GetDebugInfo();
    IDisposable SubscribeDebugLog(Func<string, Task> writer);
    int SseSubscriberCount { get; }
    Task StartConnectionAsync();
    Task StopConnectionAsync();
    Task RestartConnectionAsync();
}
```

- [ ] **Step 3: Enable internal test access — modify `RecordingCopyNet.csproj`**

Add inside the existing `<Project>` element (a new `<ItemGroup>`), so `HandleRawMessageAsync` (Step 5) can stay `internal` and still be called directly from tests instead of only through a real socket:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="RecordingCopyNet.Tests" />
</ItemGroup>
```

- [ ] **Step 4: Write the failing test**

`tests/RecordingCopyNet.Tests/Services/Zoom/ZoomWebSocketListenerTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomWebSocketListenerTests
{
    private class FakeEventsRepository : IEventsRepository
    {
        public long NextId = 1;
        public Dictionary<long, EventRecord> Rows = new();

        public long LogEvent(string eventType, string? meetingUuid, string? meetingTopic, string? hostEmail, string? rawPayload)
        {
            var id = NextId++;
            Rows[id] = new EventRecord { Id = id, EventType = eventType, MeetingUuid = meetingUuid, MeetingTopic = meetingTopic, HostEmail = hostEmail, RawPayload = rawPayload, Status = "received" };
            return id;
        }

        public void UpdateEvent(long id, EventUpdateFields fields)
        {
            var row = Rows[id];
            if (fields.Status != null) row.Status = fields.Status;
            if (fields.SkipReason != null) row.SkipReason = fields.SkipReason;
            if (fields.TransferError != null) row.TransferError = fields.TransferError;
            if (fields.TransferFolderName != null) row.TransferFolderName = fields.TransferFolderName;
            if (fields.TransferFolderId != null) row.TransferFolderId = fields.TransferFolderId;
            if (fields.TransferFilesUploaded != null) row.TransferFilesUploaded = fields.TransferFilesUploaded;
            if (fields.TransferStartedAt != null) row.TransferStartedAt = fields.TransferStartedAt;
            if (fields.TransferCompletedAt != null) row.TransferCompletedAt = fields.TransferCompletedAt;
        }

        public void AppendLog(long id, string message) { }
        public List<EventRecord> ListEvents(int limit = 50, int offset = 0) => Rows.Values.ToList();
        public EventRecord? GetEvent(long id) => Rows.GetValueOrDefault(id);
        public EventStats GetStats() => new(Rows.Count, 0, 0, 0);
    }

    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? SettingsFields;
        public bool Exists(CredentialType type) => type == CredentialType.Settings && SettingsFields != null;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => type == CredentialType.Settings ? SettingsFields : null;
        public void Delete(CredentialType type) { }
    }

    private class FakeTransferService : ITransferService
    {
        public Exception? ThrowInstead;
        public List<string> CalledWithMeetingIds = new();
        public Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default)
        {
            CalledWithMeetingIds.Add(meetingId);
            if (ThrowInstead != null) return Task.FromException<TransferResult>(ThrowInstead);
            return Task.FromResult(new TransferResult("folder-name", "folder-id", 2));
        }
    }

    private class FakeZoomRecordingsService : IZoomRecordingsService
    {
        public Task<System.Text.Json.JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private class NoopZoomAuthService : IZoomAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok");
        public void ClearTokenCache() { }
    }

    private static ZoomWebSocketListener BuildListener(
        FakeEventsRepository events, FakeCredentialStore store, FakeTransferService transfer,
        FakeZoomRecordingsService? recordings = null, EventDedupTracker? dedup = null)
    {
        return new ZoomWebSocketListener(
            new NoopZoomAuthService(),
            recordings ?? new FakeZoomRecordingsService(),
            transfer,
            store,
            events,
            new ZoomWsMessageRouter(),
            dedup ?? new EventDedupTracker(TimeProvider.System),
            new TransferQueue(limit: 3),
            new SseBroadcastHub(),
            NullLogger<ZoomWebSocketListener>.Instance);
    }

    private const string RecordingCompletedTemplate =
        """{"module":"message","content":"{\"event\":\"recording.completed\",\"payload\":{\"object\":{\"uuid\":\"__UUID__\",\"topic\":\"Standup\",\"host_email\":\"__HOST__\"}}}"}""";

    [Fact]
    public async Task HandleRawMessageAsync_RecordingCompleted_RunsTransferAndMarksCompleted()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService();
        var listener = BuildListener(events, new FakeCredentialStore { SettingsFields = new() }, transfer);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-1").Replace("__HOST__", "host@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(100);

        var row = events.Rows.Values.Single();
        Assert.Equal("completed", row.Status);
        Assert.Equal(2, row.TransferFilesUploaded);
        Assert.Contains("uuid-1", transfer.CalledWithMeetingIds);
    }

    [Fact]
    public async Task HandleRawMessageAsync_DuplicateUuid_SkipsSecondOccurrence()
    {
        var events = new FakeEventsRepository();
        var listener = BuildListener(events, new FakeCredentialStore { SettingsFields = new() }, new FakeTransferService());
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-dup").Replace("__HOST__", "host@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);
        await listener.HandleRawMessageAsync(raw, CancellationToken.None);

        Assert.Equal(2, events.Rows.Count);
        Assert.Equal("skipped", events.Rows.Values.Last().Status);
        Assert.Equal("duplicate", events.Rows.Values.Last().SkipReason);
    }

    [Fact]
    public async Task HandleRawMessageAsync_FiltersByConfiguredUser_WhenHostEmailDoesNotMatch()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService();
        var store = new FakeCredentialStore { SettingsFields = new() { ["default_zoom_user"] = "me@x.com" } };
        var listener = BuildListener(events, store, transfer);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-2").Replace("__HOST__", "other@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);

        var row = events.Rows.Values.Single();
        Assert.Equal("skipped", row.Status);
        Assert.Contains("wrong user", row.SkipReason);
        Assert.Empty(transfer.CalledWithMeetingIds);
    }

    [Fact]
    public async Task HandleRawMessageAsync_AllUsersMode_AcceptsAnyHost()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService();
        var store = new FakeCredentialStore { SettingsFields = new() { ["default_zoom_user"] = "me@x.com", ["transfer_all_users"] = "true" } };
        var listener = BuildListener(events, store, transfer);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-3").Replace("__HOST__", "other@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);

        Assert.Contains("uuid-3", transfer.CalledWithMeetingIds);
    }

    [Fact]
    public async Task HandleRawMessageAsync_TransferFailure_MarksFailedAndForgetsDedupForRetry()
    {
        var events = new FakeEventsRepository();
        var transfer = new FakeTransferService { ThrowInstead = new InvalidOperationException("upload failed") };
        var dedup = new EventDedupTracker(TimeProvider.System);
        var listener = BuildListener(events, new FakeCredentialStore { SettingsFields = new() }, transfer, dedup: dedup);
        var raw = RecordingCompletedTemplate.Replace("__UUID__", "uuid-4").Replace("__HOST__", "host@x.com");

        await listener.HandleRawMessageAsync(raw, CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal("failed", events.Rows.Values.Single().Status);
        Assert.Equal("upload failed", events.Rows.Values.Single().TransferError);

        await listener.HandleRawMessageAsync(raw, CancellationToken.None); // retry after failure
        await Task.Delay(50);

        Assert.Equal(2, events.Rows.Count);
        Assert.NotEqual("skipped", events.Rows.Values.Last().Status);
    }

    [Fact]
    public async Task HandleRawMessageAsync_HeartbeatIgnoredAndParseError_UpdateCountersWithoutThrowing()
    {
        var listener = BuildListener(new FakeEventsRepository(), new FakeCredentialStore { SettingsFields = new() }, new FakeTransferService());

        await listener.HandleRawMessageAsync("""{"module":"heartbeat"}""", CancellationToken.None);
        await listener.HandleRawMessageAsync("""{"module":"message","content":"{\"event\":\"meeting.started\",\"payload\":{}}"}""", CancellationToken.None);
        await listener.HandleRawMessageAsync("not json", CancellationToken.None);

        var info = listener.GetDebugInfo();
        Assert.True(info.Counters.Heartbeats >= 1);
        Assert.True(info.Counters.Errors >= 1);
    }

    [Fact]
    public void GetStatus_StartsDisconnected()
    {
        var listener = BuildListener(new FakeEventsRepository(), new FakeCredentialStore(), new FakeTransferService());
        Assert.Equal("disconnected", listener.GetStatus());
    }

    [Fact]
    public async Task StartConnectionAsync_WithNoConfiguredUrl_StaysDisconnected()
    {
        var listener = BuildListener(new FakeEventsRepository(), new FakeCredentialStore { SettingsFields = new() }, new FakeTransferService());

        await listener.StartConnectionAsync();

        Assert.Equal("disconnected", listener.GetStatus());
    }
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomWebSocketListenerTests`
Expected: FAIL — `ZoomWebSocketListener` does not exist.

- [ ] **Step 6: Write `ZoomWebSocketListener`**

`src/RecordingCopyNet/Services/Zoom/ZoomWebSocketListener.cs`

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;

namespace RecordingCopyNet.Services.Zoom;

// Direct port of lib/zoom/websocket.js — see spec §9. Connection I/O
// (ConnectLoopAsync, TryConnectOnceAsync, ReceiveLoopAsync, heartbeat) is
// glue that's only meaningfully verified by the manual smoke test in
// Task 25; the message-handling pipeline (HandleRawMessageAsync and
// everything it calls) is `internal` specifically so the tests above can
// exercise dedup/filtering/queueing logic without a real socket.
public class ZoomWebSocketListener : BackgroundService, IZoomWebSocketController
{
    private const int MaxDebugLog = 100;
    private const int HeartbeatIntervalMs = 30_000;
    private const int ReconnectDelayMs = 5_000;

    private readonly IZoomAuthService _zoomAuth;
    private readonly IZoomRecordingsService _zoomRecordings;
    private readonly ITransferService _transferService;
    private readonly ICredentialStore _credentialStore;
    private readonly IEventsRepository _eventsRepo;
    private readonly ZoomWsMessageRouter _router;
    private readonly EventDedupTracker _dedup;
    private readonly TransferQueue _transferQueue;
    private readonly SseBroadcastHub _sseHub;
    private readonly Microsoft.Extensions.Logging.ILogger<ZoomWebSocketListener> _logger;

    private readonly object _stateLock = new();
    private readonly List<DebugLogEntry> _debugLog = new();
    private string _status = "disconnected";
    private DateTimeOffset? _connectedAt;
    private int _messages, _heartbeats, _events, _errors;
    private long _generation;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _connectionCts;

    public ZoomWebSocketListener(
        IZoomAuthService zoomAuth, IZoomRecordingsService zoomRecordings, ITransferService transferService,
        ICredentialStore credentialStore, IEventsRepository eventsRepo, ZoomWsMessageRouter router,
        EventDedupTracker dedup, TransferQueue transferQueue, SseBroadcastHub sseHub,
        Microsoft.Extensions.Logging.ILogger<ZoomWebSocketListener> logger)
    {
        _zoomAuth = zoomAuth;
        _zoomRecordings = zoomRecordings;
        _transferService = transferService;
        _credentialStore = credentialStore;
        _eventsRepo = eventsRepo;
        _router = router;
        _dedup = dedup;
        _transferQueue = transferQueue;
        _sseHub = sseHub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartConnectionAsync();
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* host shutdown */ }
        await StopConnectionAsync();
    }

    // ---- IZoomWebSocketController ----

    public string GetStatus() { lock (_stateLock) return _status; }

    public ZoomWsDebugInfo GetDebugInfo()
    {
        lock (_stateLock)
        {
            var (active, queued, limit) = _transferQueue.GetQueueInfo();
            return new ZoomWsDebugInfo(
                _status, _connectedAt?.ToString("o"),
                new ZoomWsCounters(_messages, _heartbeats, _events, _errors),
                new ZoomWsQueueInfo(active, queued, limit),
                _debugLog.TakeLast(50).ToList());
        }
    }

    public IDisposable SubscribeDebugLog(Func<string, Task> writer) => _sseHub.Subscribe(writer);

    public int SseSubscriberCount => _sseHub.SubscriberCount;

    public Task StartConnectionAsync()
    {
        var settings = _credentialStore.Load(CredentialType.Settings);
        var wsUrl = settings != null && settings.TryGetValue("zoom_websocket_url", out var url) ? url : null;
        if (string.IsNullOrEmpty(wsUrl))
        {
            Debug("No WebSocket URL configured, skipping");
            SetStatus("disconnected");
            return Task.CompletedTask;
        }

        var generation = Interlocked.Increment(ref _generation);
        _connectionCts = new CancellationTokenSource();
        _ = ConnectLoopAsync(wsUrl, generation, _connectionCts.Token); // errors are handled inside the loop
        return Task.CompletedTask;
    }

    public async Task StopConnectionAsync()
    {
        Interlocked.Increment(ref _generation); // invalidates any in-flight reconnect attempt
        _connectionCts?.Cancel();

        var ws = _ws;
        _ws = null;
        if (ws != null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
                else if (ws.State == WebSocketState.Connecting)
                    ws.Abort(); // .NET analogue of ws.terminate() for a socket still mid-handshake (spec §9, docs §10)
            }
            catch { /* best-effort teardown */ }
            finally { ws.Dispose(); }
        }

        SetStatus("disconnected");
        _connectedAt = null;
        Debug("Stopped");
    }

    public async Task RestartConnectionAsync()
    {
        await StopConnectionAsync();
        await StartConnectionAsync();
    }

    // ---- Connection loop ----

    private async Task ConnectLoopAsync(string wsUrl, long generation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Interlocked.Read(ref _generation) == generation)
        {
            await TryConnectOnceAsync(wsUrl, generation, ct);
            if (ct.IsCancellationRequested || Interlocked.Read(ref _generation) != generation) return;

            Debug("Reconnecting in 5s...");
            try { await Task.Delay(ReconnectDelayMs, ct); }
            catch (OperationCanceledException) { return; }

            if (Interlocked.Read(ref _generation) != generation) return;

            var settings = _credentialStore.Load(CredentialType.Settings);
            var currentUrl = settings != null && settings.TryGetValue("zoom_websocket_url", out var url) ? url : null;
            if (string.IsNullOrEmpty(currentUrl))
            {
                Debug("WebSocket URL removed, not reconnecting");
                SetStatus("disconnected");
                return;
            }
            wsUrl = currentUrl;
        }
    }

    private async Task TryConnectOnceAsync(string wsUrl, long generation, CancellationToken ct)
    {
        SetStatus("connecting");
        Debug("Connecting...");

        string token;
        try { token = await _zoomAuth.GetAccessTokenAsync(ct); }
        catch (Exception ex)
        {
            Debug($"Failed to get access token: {ex.Message}");
            SetStatus("disconnected");
            return;
        }

        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"{wsUrl}&access_token={token}"), ct);
        }
        catch (Exception ex)
        {
            Debug($"Error: {ex.Message}");
            SetStatus("disconnected");
            ws.Dispose();
            return;
        }

        if (Interlocked.Read(ref _generation) != generation) { ws.Dispose(); return; } // StopConnectionAsync fired mid-handshake

        _ws = ws;
        _connectedAt = DateTimeOffset.UtcNow;
        lock (_stateLock) { _messages = 0; _heartbeats = 0; _events = 0; _errors = 0; }
        SetStatus("connected");
        Debug("Connected to Zoom");

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(ws, heartbeatCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, ct);
        }
        catch (Exception ex)
        {
            lock (_stateLock) _errors++;
            Debug($"Error: {ex.Message}");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { /* heartbeat loop only throws OperationCanceledException, swallowed there */ }
        }

        Debug("Disconnected");
        SetStatus("disconnected");
        _connectedAt = null;
        if (ReferenceEquals(_ws, ws)) _ws = null;
        ws.Dispose();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var raw = Encoding.UTF8.GetString(messageStream.ToArray());
            lock (_stateLock) _messages++;
            await HandleRawMessageAsync(raw, ct);
        }
    }

    private async Task RunHeartbeatAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HeartbeatIntervalMs));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (ws.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes("""{"module":"heartbeat"}""");
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- Message handling (testable without a real socket) ----

    internal async Task HandleRawMessageAsync(string rawJson, CancellationToken ct)
    {
        var routed = _router.Route(rawJson);

        switch (routed.Kind)
        {
            case ZoomWsMessageKind.Heartbeat:
                lock (_stateLock) _heartbeats++;
                Debug("Heartbeat ack");
                return;

            case ZoomWsMessageKind.BuildConnectionSuccess:
                Debug($"build_connection: success — raw: {routed.RawExcerpt}");
                return;

            case ZoomWsMessageKind.BuildConnectionFailure:
                Debug($"build_connection: failure — raw: {routed.RawExcerpt}");
                return;

            case ZoomWsMessageKind.ParseError:
                lock (_stateLock) _errors++;
                Debug($"Failed to parse message — raw: {routed.RawExcerpt}");
                return;

            case ZoomWsMessageKind.Ignored:
                Debug($"Ignored event: {routed.EventName ?? "unknown"}");
                return;

            case ZoomWsMessageKind.RecordingCompleted:
                lock (_stateLock) _events++;
                await HandleRecordingCompletedAsync(routed.Payload!.Value, ct);
                return;
        }
    }

    private async Task HandleRecordingCompletedAsync(JsonElement msg, CancellationToken ct)
    {
        if (!msg.TryGetProperty("payload", out var payloadWrapper) || !payloadWrapper.TryGetProperty("object", out var payload))
        {
            Debug("recording.completed with no payload object");
            return;
        }

        var uuid = payload.GetProperty("uuid").GetString()!;
        var hostEmail = payload.TryGetProperty("host_email", out var hostEmailEl) ? hostEmailEl.GetString() : null;
        var topic = payload.TryGetProperty("topic", out var topicEl) ? topicEl.GetString() : null;

        Debug($"Received recording.completed for meeting \"{topic}\" (uuid: {uuid})");

        var eventId = _eventsRepo.LogEvent("recording.completed", uuid, topic, hostEmail, msg.GetRawText());

        if (!_dedup.TryMarkProcessed(uuid))
        {
            Debug($"Already processed {uuid}, skipping");
            _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "skipped", SkipReason = "duplicate" });
            return;
        }

        var settings = _credentialStore.Load(CredentialType.Settings);
        var allUsers = settings != null && settings.TryGetValue("transfer_all_users", out var au) && au == "true";
        var configuredUser = settings != null && settings.TryGetValue("default_zoom_user", out var cu) ? cu : null;

        if (allUsers)
        {
            Debug($"All-users mode: accepting recording from {hostEmail ?? "unknown host"}");
        }
        else if (!string.IsNullOrEmpty(configuredUser))
        {
            var resolvedEmail = hostEmail;
            if (string.IsNullOrEmpty(resolvedEmail) && payload.TryGetProperty("host_id", out var hostIdEl))
                resolvedEmail = await _zoomRecordings.GetUserEmailAsync(hostIdEl.GetString()!, ct);

            if (!string.IsNullOrEmpty(resolvedEmail) && !resolvedEmail.Equals(configuredUser, StringComparison.OrdinalIgnoreCase))
            {
                Debug($"Ignoring recording from {resolvedEmail} (configured user: {configuredUser})");
                _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "skipped", SkipReason = $"wrong user: {resolvedEmail}" });
                return;
            }
        }

        var meetingId = uuid;
        if (meetingId.StartsWith('/') || meetingId.Contains("//"))
            meetingId = Uri.EscapeDataString(Uri.EscapeDataString(meetingId));

        void EventLogger(string message)
        {
            Debug(message);
            _eventsRepo.AppendLog(eventId, message);
        }

        var (active, queued, limit) = _transferQueue.GetQueueInfo();
        if (active >= limit)
        {
            Debug($"Queued auto-transfer for \"{topic}\" (position {queued + 1}, {active} active)");
            _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "queued" });
            _eventsRepo.AppendLog(eventId, $"Queued — {active} transfer(s) already running, position {queued + 1} in queue");
        }

        _transferQueue.Enqueue(async () =>
        {
            Debug($"Starting auto-transfer for \"{topic}\"");
            _eventsRepo.UpdateEvent(eventId, new EventUpdateFields { Status = "transferring", TransferStartedAt = DateTimeOffset.UtcNow.ToString("o") });

            try
            {
                var result = await _transferService.TransferMeetingAsync(meetingId, EventLogger, CancellationToken.None);
                Debug($"Transfer complete: {result.FilesUploaded} files to \"{result.FolderName}\"");
                _eventsRepo.UpdateEvent(eventId, new EventUpdateFields
                {
                    Status = "completed",
                    TransferCompletedAt = DateTimeOffset.UtcNow.ToString("o"),
                    TransferFolderName = result.FolderName,
                    TransferFolderId = result.FolderId,
                    TransferFilesUploaded = result.FilesUploaded,
                });
            }
            catch (Exception ex)
            {
                Debug($"Transfer failed for {uuid}: {ex.Message}");
                _eventsRepo.UpdateEvent(eventId, new EventUpdateFields
                {
                    Status = "failed",
                    TransferCompletedAt = DateTimeOffset.UtcNow.ToString("o"),
                    TransferError = ex.Message,
                });
                _dedup.Forget(uuid); // allow a retry on the next recording.completed for this uuid
            }
        });
    }

    // ---- Status/debug plumbing ----

    private void SetStatus(string status) { lock (_stateLock) _status = status; }

    private void Debug(string message)
    {
        var entry = new DebugLogEntry(DateTimeOffset.UtcNow.ToString("o"), message);
        lock (_stateLock)
        {
            _debugLog.Add(entry);
            if (_debugLog.Count > MaxDebugLog) _debugLog.RemoveAt(0);
        }
        _logger.LogInformation("[websocket] {Message}", message);

        var (active, queued, limit) = _transferQueue.GetQueueInfo();
        _ = _sseHub.BroadcastAsync("append", new
        {
            entry,
            status = GetStatus(),
            connectedAt = _connectedAt?.ToString("o"),
            counters = new { messages = _messages, heartbeats = _heartbeats, events = _events, errors = _errors },
            transfers = new { active, queued, limit },
        });
    }
}
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomWebSocketListenerTests`
Expected: PASS

- [ ] **Step 8: Wire into DI — modify `Program.cs`**

Add after the `TransferQueue` registration. The listener is registered once and exposed both as the hosted service and as the interface controllers use, so `StartAsync`/`StopAsync` (host lifecycle) and `StartConnectionAsync`/`StopConnectionAsync` (connection control) share the same instance and state:

```csharp
builder.Services.AddSingleton<RecordingCopyNet.Services.SseBroadcastHub>();
builder.Services.AddSingleton<RecordingCopyNet.Services.Zoom.ZoomWsMessageRouter>();
builder.Services.AddSingleton<RecordingCopyNet.Services.Zoom.ZoomWebSocketListener>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecordingCopyNet.Services.Zoom.ZoomWebSocketListener>());
builder.Services.AddSingleton<RecordingCopyNet.Services.Zoom.IZoomWebSocketController>(sp =>
    sp.GetRequiredService<RecordingCopyNet.Services.Zoom.ZoomWebSocketListener>());
```

- [ ] **Step 9: Commit**

```bash
git add .
git commit -m "Add ZoomWebSocketListener"
```

---

## Task 16: SseWriter, AppVersion, CredentialsController, VersionController, SettingsController

**Files:**
- Create: `src/RecordingCopyNet/Config/AppVersion.cs`
- Create: `src/RecordingCopyNet/Services/SseWriter.cs`
- Create: `src/RecordingCopyNet/Models/ApiResponses.cs`
- Create: `src/RecordingCopyNet/Controllers/CredentialsController.cs`
- Create: `src/RecordingCopyNet/Controllers/VersionController.cs`
- Create: `src/RecordingCopyNet/Controllers/SettingsController.cs`
- Test: `tests/RecordingCopyNet.Tests/Services/SseWriterTests.cs`
- Test: `tests/RecordingCopyNet.Tests/Controllers/CredentialsControllerTests.cs`
- Test: `tests/RecordingCopyNet.Tests/Controllers/SettingsControllerTests.cs`
- Modify: `tests/RecordingCopyNet.Tests/RecordingCopyNet.Tests.csproj`

**Interfaces:**
- Consumes: `ICredentialStore` (Task 3), `IZoomAuthService` (Task 6), `Services.Google.IGoogleAuthService` (Task 9), `IZoomWebSocketController` (Task 15).
- Produces: `SseWriter` static helper (`SetHeaders`, `WriteNamedEventAsync`, `WriteDataAsync`) used by every SSE-emitting controller from here on (Tasks 17, 19, 21). Response DTOs `ConfiguredResponse`, `OkResponse`, `ErrorResponse`, `VersionResponse`, `SettingsResponse` in `Models/ApiResponses.cs`.

**JSON casing reference for this task's endpoints** (from spec §12, verified against the Node routes — see Global Constraints):

| Endpoint | Shape |
|---|---|
| `GET /api/version` | `{ "version": "0.1.0" }` |
| `GET /api/credentials/{type}` | `{ "configured": bool }` |
| `POST /api/credentials/{type}` | `{ "ok": true }` or 400 `{ "error": "..." }` |
| `DELETE /api/credentials/{type}` | `{ "ok": true }` |
| `GET /api/settings` | `{ "settings": {...raw stored fields...}, "zoomConfigured": bool, "googleConfigured": bool }` |

**Version note:** this is a new codebase in a new repo, so it starts its own version track at `0.1.0` rather than continuing the Node app's `0.7.1` — per the project's CLAUDE.md convention, this constant must be bumped with each meaningful change and is what every controller/log line displays.

- [ ] **Step 1: Add the ASP.NET Core test framework reference**

`tests/RecordingCopyNet.Tests/RecordingCopyNet.Tests.csproj` — add inside `<Project>`:

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
```

- [ ] **Step 2: Write `AppVersion`**

`src/RecordingCopyNet/Config/AppVersion.cs`

```csharp
namespace RecordingCopyNet.Config;

public static class AppVersion
{
    public const string Current = "0.1.0";
}
```

- [ ] **Step 3: Write the response DTOs**

`src/RecordingCopyNet/Models/ApiResponses.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record ConfiguredResponse([property: JsonPropertyName("configured")] bool Configured);
public record OkResponse([property: JsonPropertyName("ok")] bool Ok);
public record ErrorResponse([property: JsonPropertyName("error")] string Error);
public record VersionResponse([property: JsonPropertyName("version")] string Version);

public record SettingsResponse(
    [property: JsonPropertyName("settings")] IReadOnlyDictionary<string, string?> Settings,
    [property: JsonPropertyName("zoomConfigured")] bool ZoomConfigured,
    [property: JsonPropertyName("googleConfigured")] bool GoogleConfigured);
```

- [ ] **Step 4: Write the failing test for `SseWriter`**

`tests/RecordingCopyNet.Tests/Services/SseWriterTests.cs`

```csharp
using Microsoft.AspNetCore.Http;
using RecordingCopyNet.Services;
using Xunit;

namespace RecordingCopyNet.Tests.Services;

public class SseWriterTests
{
    [Fact]
    public async Task WriteNamedEventAsync_WritesEventAndDataLines()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await SseWriter.WriteNamedEventAsync(context.Response, "snapshot", new { status = "connected" }, CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.Equal("event: snapshot\ndata: {\"status\":\"connected\"}\n\n", text);
    }

    [Fact]
    public async Task WriteDataAsync_WritesUnnamedDataFrame()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await SseWriter.WriteDataAsync(context.Response, new { type = "progress", message = "hi" }, CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.Equal("data: {\"type\":\"progress\",\"message\":\"hi\"}\n\n", text);
    }

    [Fact]
    public void SetHeaders_SetsSseHeadersIncludingProxyBufferingOptOut()
    {
        var context = new DefaultHttpContext();

        SseWriter.SetHeaders(context.Response);

        Assert.Equal("text/event-stream", context.Response.Headers.ContentType.ToString());
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"].ToString());
    }
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter SseWriterTests`
Expected: FAIL — `SseWriter` does not exist.

- [ ] **Step 6: Write `SseWriter`**

`src/RecordingCopyNet/Services/SseWriter.cs`

```csharp
using System.Text.Json;

namespace RecordingCopyNet.Services;

public static class SseWriter
{
    public static void SetHeaders(Microsoft.AspNetCore.Http.HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // stop reverse proxies from buffering the stream
    }

    public static async Task WriteNamedEventAsync(Microsoft.AspNetCore.Http.HttpResponse response, string eventName, object data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteDataAsync(Microsoft.AspNetCore.Http.HttpResponse response, object data, CancellationToken ct)
    {
        await response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteCommentAsync(Microsoft.AspNetCore.Http.HttpResponse response, string comment, CancellationToken ct)
    {
        await response.WriteAsync($": {comment}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter SseWriterTests`
Expected: PASS

- [ ] **Step 8: Write `VersionController`**

`src/RecordingCopyNet/Controllers/VersionController.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Config;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/version")]
public class VersionController : ControllerBase
{
    [HttpGet]
    public ActionResult<VersionResponse> Get() => new VersionResponse(AppVersion.Current);
}
```

- [ ] **Step 9: Write the failing test for `CredentialsController`**

`tests/RecordingCopyNet.Tests/Controllers/CredentialsControllerTests.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class CredentialsControllerTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<CredentialType, Dictionary<string, string?>> Saved = new();
        public HashSet<CredentialType> Deleted = new();
        public bool Exists(CredentialType type) => Saved.ContainsKey(type);
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) => Saved[type] = new(fields);
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => Saved.GetValueOrDefault(type);
        public void Delete(CredentialType type) { Saved.Remove(type); Deleted.Add(type); }
    }

    private class FakeZoomAuthService : IZoomAuthService
    {
        public bool Cleared;
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok");
        public void ClearTokenCache() => Cleared = true;
    }

    private class FakeGoogleAuthService : IGoogleAuthService
    {
        public bool Cleared;
        public Google.Apis.Drive.v3.DriveService GetDriveService() => throw new NotImplementedException();
        public void ClearDriveClient() => Cleared = true;
    }

    private class FakeWsController : IZoomWebSocketController
    {
        public bool Stopped, Started;
        public string GetStatus() => "disconnected";
        public Models.ZoomWsDebugInfo GetDebugInfo() => throw new NotImplementedException();
        public IDisposable SubscribeDebugLog(Func<string, Task> writer) => throw new NotImplementedException();
        public int SseSubscriberCount => 0;
        public Task StartConnectionAsync() { Started = true; return Task.CompletedTask; }
        public Task StopConnectionAsync() { Stopped = true; return Task.CompletedTask; }
        public Task RestartConnectionAsync() => Task.CompletedTask;
    }

    private static (CredentialsController, FakeCredentialStore, FakeZoomAuthService, FakeGoogleAuthService, FakeWsController) Build()
    {
        var store = new FakeCredentialStore();
        var zoomAuth = new FakeZoomAuthService();
        var googleAuth = new FakeGoogleAuthService();
        var ws = new FakeWsController();
        return (new CredentialsController(store, zoomAuth, googleAuth, ws), store, zoomAuth, googleAuth, ws);
    }

    [Fact]
    public void GetConfigured_ReturnsBadRequest_ForUnknownType()
    {
        var (controller, _, _, _, _) = Build();
        var result = controller.GetConfigured("bogus");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void GetConfigured_ReturnsFalse_WhenNothingSaved()
    {
        var (controller, _, _, _, _) = Build();
        var result = controller.GetConfigured("zoom");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(((ConfiguredResponse)ok.Value!).Configured);
    }

    [Fact]
    public void Save_PersistsFields_AndClearsZoomTokenCache()
    {
        var (controller, store, zoomAuth, _, _) = Build();
        var result = controller.Save("zoom", new Dictionary<string, string?> { ["client_id"] = "abc" });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("abc", store.Saved[CredentialType.Zoom]["client_id"]);
        Assert.True(zoomAuth.Cleared);
    }

    [Fact]
    public void Save_OnSettings_ClearsGoogleClientAndRestartsWebSocket()
    {
        var (controller, _, _, googleAuth, ws) = Build();

        controller.Save("settings", new Dictionary<string, string?> { ["zoom_websocket_url"] = "wss://x" });

        Assert.True(googleAuth.Cleared);
        Assert.True(ws.Stopped);
        Assert.True(ws.Started);
    }

    [Fact]
    public void Delete_RemovesCredentialAndClearsCaches()
    {
        var (controller, store, zoomAuth, _, _) = Build();
        store.Save(CredentialType.Zoom, new Dictionary<string, string?> { ["client_id"] = "abc" });

        var result = controller.Delete("zoom");

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(store.Exists(CredentialType.Zoom));
        Assert.True(zoomAuth.Cleared);
    }
}
```

- [ ] **Step 10: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter CredentialsControllerTests`
Expected: FAIL — `CredentialsController` does not exist.

- [ ] **Step 11: Write `CredentialsController`**

`src/RecordingCopyNet/Controllers/CredentialsController.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/credentials")]
public class CredentialsController : ControllerBase
{
    private readonly ICredentialStore _store;
    private readonly IZoomAuthService _zoomAuth;
    private readonly IGoogleAuthService _googleAuth;
    private readonly IZoomWebSocketController _wsController;

    public CredentialsController(ICredentialStore store, IZoomAuthService zoomAuth, IGoogleAuthService googleAuth, IZoomWebSocketController wsController)
    {
        _store = store;
        _zoomAuth = zoomAuth;
        _googleAuth = googleAuth;
        _wsController = wsController;
    }

    [HttpGet("{type}")]
    public ActionResult<ConfiguredResponse> GetConfigured(string type)
    {
        if (!CredentialSchema.TryParse(type, out var parsed))
            return BadRequest(new ErrorResponse($"Unknown credential type: {type}"));

        return new ConfiguredResponse(_store.Exists(parsed));
    }

    [HttpPost("{type}")]
    public ActionResult<OkResponse> Save(string type, [FromBody] Dictionary<string, string?> body)
    {
        if (!CredentialSchema.TryParse(type, out var parsed))
            return BadRequest(new ErrorResponse($"Unknown credential type: {type}"));

        try
        {
            _store.Save(parsed, body);

            if (parsed == CredentialType.Zoom) _zoomAuth.ClearTokenCache();
            if (parsed == CredentialType.Google) _googleAuth.ClearDriveClient();
            if (parsed == CredentialType.Settings)
            {
                _googleAuth.ClearDriveClient();
                _wsController.StopConnectionAsync().GetAwaiter().GetResult();
                _wsController.StartConnectionAsync().GetAwaiter().GetResult();
            }

            return new OkResponse(true);
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }

    [HttpDelete("{type}")]
    public ActionResult<OkResponse> Delete(string type)
    {
        if (!CredentialSchema.TryParse(type, out var parsed))
            return BadRequest(new ErrorResponse($"Unknown credential type: {type}"));

        _store.Delete(parsed);
        if (parsed == CredentialType.Zoom) _zoomAuth.ClearTokenCache();
        if (parsed == CredentialType.Google) _googleAuth.ClearDriveClient();
        if (parsed == CredentialType.Settings) _googleAuth.ClearDriveClient();

        return new OkResponse(true);
    }
}
```

Note: `Save`'s settings branch calls `.GetAwaiter().GetResult()` deliberately, not `await` — the controller action stays synchronous (matching every other branch), and the Node route itself doesn't await its `stopWebSocket(); startWebSocket();` calls either (fire-and-forget). If Task 25's manual smoke test shows this blocking the request noticeably, switch `Save` to `async Task<ActionResult<OkResponse>>` and `await` both calls instead — call this out during that task rather than guessing now.

- [ ] **Step 12: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter CredentialsControllerTests`
Expected: PASS

- [ ] **Step 13: Write the failing test for `SettingsController`**

`tests/RecordingCopyNet.Tests/Controllers/SettingsControllerTests.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class SettingsControllerTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<CredentialType, Dictionary<string, string?>> Saved = new();
        public bool Exists(CredentialType type) => Saved.ContainsKey(type);
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) => Saved[type] = new(fields);
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => Saved.GetValueOrDefault(type);
        public void Delete(CredentialType type) => Saved.Remove(type);
    }

    [Fact]
    public void Get_ReturnsEmptySettingsAndFalseFlags_WhenNothingConfigured()
    {
        var controller = new SettingsController(new FakeCredentialStore());

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = (SettingsResponse)ok.Value!;
        Assert.Empty(body.Settings);
        Assert.False(body.ZoomConfigured);
        Assert.False(body.GoogleConfigured);
    }

    [Fact]
    public void Get_ReturnsStoredSettingsAndConfiguredFlags()
    {
        var store = new FakeCredentialStore();
        store.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "abc" });
        store.Save(CredentialType.Zoom, new Dictionary<string, string?> { ["client_id"] = "x" });
        var controller = new SettingsController(store);

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = (SettingsResponse)ok.Value!;
        Assert.Equal("abc", body.Settings["google_folder_id"]);
        Assert.True(body.ZoomConfigured);
        Assert.False(body.GoogleConfigured);
    }
}
```

- [ ] **Step 14: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter SettingsControllerTests`
Expected: FAIL — `SettingsController` does not exist.

- [ ] **Step 15: Write `SettingsController`**

`src/RecordingCopyNet/Controllers/SettingsController.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ICredentialStore _store;

    public SettingsController(ICredentialStore store) => _store = store;

    [HttpGet]
    public ActionResult<SettingsResponse> Get()
    {
        var settings = _store.Load(CredentialType.Settings) ?? new Dictionary<string, string?>();
        return new SettingsResponse(settings, _store.Exists(CredentialType.Zoom), _store.Exists(CredentialType.Google));
    }
}
```

- [ ] **Step 16: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter SettingsControllerTests`
Expected: PASS

- [ ] **Step 17: Commit**

```bash
git add .
git commit -m "Add SseWriter, VersionController, CredentialsController, SettingsController"
```

---

## Task 17: ZoomController

**Files:**
- Modify: `src/RecordingCopyNet/Models/ApiResponses.cs`
- Create: `src/RecordingCopyNet/Controllers/ZoomController.cs`
- Test: `tests/RecordingCopyNet.Tests/Controllers/ZoomControllerTests.cs`

**Interfaces:**
- Consumes: `IZoomAuthService` (Task 6), `IZoomRecordingsService` (Task 7), `IZoomWebSocketController` (Task 15), `ICredentialStore` (Task 3).
- Produces: all seven `/api/zoom/*` routes from spec §12.

**JSON casing reference** (verified against the Node routes):

| Endpoint | Shape |
|---|---|
| `POST /api/zoom/test` | `{ ok:true, message }` or 400 `{ ok:false, error }` |
| `GET /api/zoom/recordings` | `{ meetings: [...] }` (raw Zoom JSON, untouched) |
| `GET /api/zoom/websocket/status` | `{ status, connectedAt, counters, transfers, log, url }` |
| `GET /api/zoom/websocket/stream` | SSE: `event: snapshot` then `event: append` frames |
| `GET /api/zoom/websocket/subscribers` | `{ subscribers }` |
| `POST /api/zoom/websocket/stop` | bare JSON **string** — `getWsStatus()` returns the status string itself, so the Node response body is literally `"connected"`, not an object. Preserve this quirk. |
| `POST /api/zoom/websocket/start` | same bare-string quirk as `stop` |
| `POST /api/zoom/websocket/restart` | `{ status }` — **not** the bare-string form; Node wraps it here even though `start`/`stop` don't. This inconsistency is in the original app and the ported frontend JS may depend on it either way, so it's preserved exactly rather than "fixed." |

- [ ] **Step 1: Add response DTOs — modify `Models/ApiResponses.cs`**

Add to the existing file:

```csharp
public record OkTrueMessageResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message);

public record OkFalseErrorResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string Error);

public record SubscribersResponse([property: JsonPropertyName("subscribers")] int Subscribers);
public record RestartResponse([property: JsonPropertyName("status")] string Status);
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Controllers/ZoomControllerTests.cs`

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class ZoomControllerTests
{
    private class FakeZoomAuthService : IZoomAuthService
    {
        public Exception? ThrowOnGet;
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            ThrowOnGet != null ? Task.FromException<string>(ThrowOnGet) : Task.FromResult("tok");
        public void ClearTokenCache() { }
    }

    private class FakeZoomRecordingsService : IZoomRecordingsService
    {
        public Task<JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default) =>
            Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""[{"id":"m1"}]"""));
        public Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? SettingsFields;
        public bool Exists(CredentialType type) => false;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => type == CredentialType.Settings ? SettingsFields : null;
        public void Delete(CredentialType type) { }
    }

    private class TrackingWsController : IZoomWebSocketController
    {
        private readonly List<Func<string, Task>> _subscribers = new();
        public int SubscriberCount => _subscribers.Count;
        public bool StopCalled, StartCalled;
        public string Status = "disconnected";

        public string GetStatus() => Status;
        public ZoomWsDebugInfo GetDebugInfo() => new(Status, null, new ZoomWsCounters(1, 2, 3, 0), new ZoomWsQueueInfo(0, 0, 3), new());
        public IDisposable SubscribeDebugLog(Func<string, Task> writer)
        {
            _subscribers.Add(writer);
            return new Unsub(() => _subscribers.Remove(writer));
        }
        public int SseSubscriberCount => SubscriberCount;
        public Task StartConnectionAsync() { StartCalled = true; Status = "connected"; return Task.CompletedTask; }
        public Task StopConnectionAsync() { StopCalled = true; Status = "disconnected"; return Task.CompletedTask; }
        public Task RestartConnectionAsync() => Task.CompletedTask;

        private class Unsub : IDisposable
        {
            private readonly Action _action;
            public Unsub(Action action) => _action = action;
            public void Dispose() => _action();
        }
    }

    private static ZoomController Build(FakeZoomAuthService? auth = null, TrackingWsController? ws = null, FakeCredentialStore? store = null) =>
        new(auth ?? new FakeZoomAuthService(), new FakeZoomRecordingsService(), ws ?? new TrackingWsController(), store ?? new FakeCredentialStore());

    [Fact]
    public async Task Test_ReturnsOkTrue_WhenTokenFetchSucceeds()
    {
        var controller = Build();
        var result = await controller.Test();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(((OkTrueMessageResponse)ok.Value!).Ok);
    }

    [Fact]
    public async Task Test_ReturnsBadRequestWithOkFalse_WhenTokenFetchFails()
    {
        var controller = Build(new FakeZoomAuthService { ThrowOnGet = new InvalidOperationException("bad creds") });
        var result = await controller.Test();
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(((OkFalseErrorResponse)bad.Value!).Ok);
    }

    [Fact]
    public async Task GetRecordings_ReturnsBadRequest_WhenUserIdMissing()
    {
        var controller = Build();
        var result = await controller.GetRecordings(null, null, null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetRecordings_ReturnsMeetingsArray()
    {
        var controller = Build();
        var result = await controller.GetRecordings("user@x.com", null, null);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void GetWebSocketStatus_IncludesUrlFromSettings()
    {
        var store = new FakeCredentialStore { SettingsFields = new() { ["zoom_websocket_url"] = "wss://example" } };
        var controller = Build(store: store);
        var result = Assert.IsType<OkObjectResult>(controller.GetWebSocketStatus());
        Assert.NotNull(result.Value);
    }

    [Fact]
    public void GetSubscribers_ReturnsCurrentCount()
    {
        var ws = new TrackingWsController();
        ws.SubscribeDebugLog(_ => Task.CompletedTask);
        var controller = Build(ws: ws);

        var result = Assert.IsType<OkObjectResult>(controller.GetSubscribers());
        Assert.Equal(1, ((SubscribersResponse)result.Value!).Subscribers);
    }

    [Fact]
    public async Task Stop_ReturnsBareStatusString()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var result = await controller.Stop();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("disconnected", ok.Value);
        Assert.True(ws.StopCalled);
    }

    [Fact]
    public async Task Start_StopsThenStarts_AndReturnsBareStatusString()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var result = await controller.Start();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("connected", ok.Value);
        Assert.True(ws.StopCalled);
        Assert.True(ws.StartCalled);
    }

    [Fact]
    public async Task Restart_ReturnsWrappedStatusObject()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var result = Assert.IsType<OkObjectResult>(await controller.Restart());
        Assert.Equal("connected", ((RestartResponse)result.Value!).Status);
    }

    [Fact]
    public async Task Stream_WritesSnapshotFirst_AndUnsubscribesWhenClientDisconnects()
    {
        var ws = new TrackingWsController();
        var controller = Build(ws: ws);
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var cts = new CancellationTokenSource();

        var streamTask = controller.Stream(cts.Token);
        await Task.Delay(50);
        Assert.Equal(1, ws.SubscriberCount);

        cts.Cancel();
        await streamTask;

        Assert.Equal(0, ws.SubscriberCount);
        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        Assert.StartsWith("event: snapshot\n", text);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomControllerTests`
Expected: FAIL — `ZoomController` does not exist.

- [ ] **Step 4: Write `ZoomController`**

`src/RecordingCopyNet/Controllers/ZoomController.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services;
using RecordingCopyNet.Services.Zoom;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/zoom")]
public class ZoomController : ControllerBase
{
    private readonly IZoomAuthService _zoomAuth;
    private readonly IZoomRecordingsService _recordings;
    private readonly IZoomWebSocketController _ws;
    private readonly ICredentialStore _store;

    public ZoomController(IZoomAuthService zoomAuth, IZoomRecordingsService recordings, IZoomWebSocketController ws, ICredentialStore store)
    {
        _zoomAuth = zoomAuth;
        _recordings = recordings;
        _ws = ws;
        _store = store;
    }

    [HttpPost("test")]
    public async Task<ActionResult> Test()
    {
        try
        {
            await _zoomAuth.GetAccessTokenAsync();
            return Ok(new OkTrueMessageResponse(true, "Zoom connection successful"));
        }
        catch (Exception ex)
        {
            return BadRequest(new OkFalseErrorResponse(false, ex.Message));
        }
    }

    [HttpGet("recordings")]
    public async Task<ActionResult> GetRecordings([FromQuery] string? userId, [FromQuery] string? from, [FromQuery] string? to)
    {
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new ErrorResponse("userId query parameter is required"));

        try
        {
            var meetings = await _recordings.ListRecordingsAsync(userId, from, to);
            return Ok(new { meetings });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ErrorResponse(ex.Message));
        }
    }

    [HttpGet("websocket/status")]
    public ActionResult GetWebSocketStatus()
    {
        var info = _ws.GetDebugInfo();
        var settings = _store.Load(CredentialType.Settings);
        var url = settings != null && settings.TryGetValue("zoom_websocket_url", out var u) ? u : null;
        return Ok(new { status = info.Status, connectedAt = info.ConnectedAt, counters = info.Counters, transfers = info.Transfers, log = info.Log, url });
    }

    [HttpGet("websocket/stream")]
    public async Task Stream(CancellationToken ct)
    {
        SseWriter.SetHeaders(Response);

        var info = _ws.GetDebugInfo();
        var settings = _store.Load(CredentialType.Settings);
        var url = settings != null && settings.TryGetValue("zoom_websocket_url", out var u) ? u : null;
        await SseWriter.WriteNamedEventAsync(Response, "snapshot", new
        {
            status = info.Status, connectedAt = info.ConnectedAt, counters = info.Counters,
            transfers = info.Transfers, log = info.Log, url,
        }, ct);

        using var subscription = _ws.SubscribeDebugLog(async frame =>
        {
            await Response.WriteAsync(frame, ct);
            await Response.Body.FlushAsync(ct);
        });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await SseWriter.WriteCommentAsync(Response, "ping", ct);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected — `using` above unsubscribes on the way out
        }
    }

    [HttpGet("websocket/subscribers")]
    public ActionResult GetSubscribers() => Ok(new SubscribersResponse(_ws.SseSubscriberCount));

    [HttpPost("websocket/stop")]
    public async Task<ActionResult<string>> Stop()
    {
        await _ws.StopConnectionAsync();
        return Ok(_ws.GetStatus());
    }

    [HttpPost("websocket/start")]
    public async Task<ActionResult<string>> Start()
    {
        await _ws.StopConnectionAsync();
        await _ws.StartConnectionAsync();
        return Ok(_ws.GetStatus());
    }

    [HttpPost("websocket/restart")]
    public async Task<ActionResult> Restart()
    {
        await _ws.StopConnectionAsync();
        await _ws.StartConnectionAsync();
        return Ok(new RestartResponse(_ws.GetStatus()));
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter ZoomControllerTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add .
git commit -m "Add ZoomController"
```

---

## Task 18: GoogleController

**Files:**
- Modify: `src/RecordingCopyNet/Models/ApiResponses.cs`
- Create: `src/RecordingCopyNet/Controllers/GoogleController.cs`
- Test: `tests/RecordingCopyNet.Tests/Controllers/GoogleControllerTests.cs`

**Interfaces:**
- Consumes: `ICredentialStore` (Task 3), `Services.Google.IGoogleDriveService` (Task 9).
- Produces: `POST /api/google/test`, `GET /api/google/drives`.

**JSON casing reference:**

| Endpoint | Shape |
|---|---|
| `POST /api/google/test` | `{ ok, message, sharedDrive, impersonating, driveId }` (camelCase) |
| `GET /api/google/drives` | `{ ok:true, drives:[{id,name}] }` or 400 `{ ok:false, error }` |

- [ ] **Step 1: Add the response DTO — modify `Models/ApiResponses.cs`**

Add to the existing file:

```csharp
public record GoogleTestResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("sharedDrive")] bool SharedDrive,
    [property: JsonPropertyName("impersonating")] string? Impersonating,
    [property: JsonPropertyName("driveId")] string? DriveId);
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Controllers/GoogleControllerTests.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class GoogleControllerTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? SettingsFields;
        public bool Exists(CredentialType type) => false;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => type == CredentialType.Settings ? SettingsFields : null;
        public void Delete(CredentialType type) { }
    }

    private class FakeGoogleDriveService : IGoogleDriveService
    {
        public DriveFolderInfo VerifyResult = new("folder-id", "My Folder", "shared-drive-1");
        public List<DriveFileInfo> Contents = new() { new("f1", "a.txt", 10) };
        public List<DriveInfo> Drives = new() { new("d1", "Team Drive") };
        public Exception? ThrowOnListDrives;

        public Task<DriveFolderInfo> VerifySharedDriveAsync(string folderId, CancellationToken ct = default) => Task.FromResult(VerifyResult);
        public Task<DriveFolderInfo> CreateFolderAsync(string name, string parentFolderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DriveFileInfo> UploadFileAsync(string filePath, string fileName, string folderId, string mimeType, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DriveInfo>> ListSharedDrivesAsync(CancellationToken ct = default) =>
            ThrowOnListDrives != null ? Task.FromException<List<DriveInfo>>(ThrowOnListDrives) : Task.FromResult(Drives);
        public Task<List<DriveFileInfo>> ListFolderContentsAsync(string folderId, CancellationToken ct = default) => Task.FromResult(Contents);
    }

    [Fact]
    public async Task Test_ReturnsBadRequest_WhenFolderIdNotConfigured()
    {
        var controller = new GoogleController(new FakeCredentialStore(), new FakeGoogleDriveService());
        var result = await controller.Test();
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Test_ReturnsOkTrue_WhenOnSharedDrive()
    {
        var store = new FakeCredentialStore { SettingsFields = new() { ["google_folder_id"] = "folder-id" } };
        var controller = new GoogleController(store, new FakeGoogleDriveService());

        var result = Assert.IsType<OkObjectResult>(await controller.Test());
        var body = (GoogleTestResponse)result.Value!;
        Assert.True(body.Ok);
        Assert.True(body.SharedDrive);
        Assert.Equal("shared-drive-1", body.DriveId);
        Assert.Null(body.Impersonating);
    }

    [Fact]
    public async Task Test_ReturnsOkFalse_WhenNotOnSharedDriveAndNoImpersonation()
    {
        var store = new FakeCredentialStore { SettingsFields = new() { ["google_folder_id"] = "folder-id" } };
        var drive = new FakeGoogleDriveService { VerifyResult = new DriveFolderInfo("folder-id", "My Folder", null) };
        var controller = new GoogleController(store, drive);

        var result = Assert.IsType<OkObjectResult>(await controller.Test());
        Assert.False(((GoogleTestResponse)result.Value!).Ok);
    }

    [Fact]
    public async Task GetDrives_ReturnsOkTrueWithDrives()
    {
        var controller = new GoogleController(new FakeCredentialStore(), new FakeGoogleDriveService());
        var result = Assert.IsType<OkObjectResult>(await controller.GetDrives());
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task GetDrives_ReturnsBadRequest_OnFailure()
    {
        var drive = new FakeGoogleDriveService { ThrowOnListDrives = new InvalidOperationException("no access") };
        var controller = new GoogleController(new FakeCredentialStore(), drive);
        var result = await controller.GetDrives();
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter GoogleControllerTests`
Expected: FAIL — `GoogleController` does not exist.

- [ ] **Step 4: Write `GoogleController`**

`src/RecordingCopyNet/Controllers/GoogleController.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/google")]
public class GoogleController : ControllerBase
{
    private readonly ICredentialStore _store;
    private readonly IGoogleDriveService _drive;

    public GoogleController(ICredentialStore store, IGoogleDriveService drive)
    {
        _store = store;
        _drive = drive;
    }

    [HttpPost("test")]
    public async Task<ActionResult> Test()
    {
        var settings = _store.Load(CredentialType.Settings);
        if (settings == null || !settings.TryGetValue("google_folder_id", out var folderId) || string.IsNullOrEmpty(folderId))
            return BadRequest(new OkFalseErrorResponse(false, "Google Drive folder ID not configured"));

        try
        {
            var folderInfo = await _drive.VerifySharedDriveAsync(folderId);
            var files = await _drive.ListFolderContentsAsync(folderId);

            var onSharedDrive = !string.IsNullOrEmpty(folderInfo.DriveId);
            var impersonateEmail = settings.TryGetValue("google_impersonate_email", out var imp) ? imp : null;
            var hasImpersonation = !string.IsNullOrEmpty(impersonateEmail);
            var ok = onSharedDrive || hasImpersonation;

            string message;
            if (hasImpersonation && onSharedDrive)
                message = $"Connected. Impersonating {impersonateEmail} + Shared Drive {folderInfo.DriveId}. Folder \"{folderInfo.Name}\" — {files.Count} items.";
            else if (hasImpersonation)
                message = $"Connected. Impersonating {impersonateEmail}. Folder \"{folderInfo.Name}\" — {files.Count} items.";
            else if (onSharedDrive)
                message = $"Connected. Folder \"{folderInfo.Name}\" is on Shared Drive {folderInfo.DriveId}. {files.Count} items found.";
            else
                message = $"Folder \"{folderInfo.Name}\" is NOT on a Shared Drive and no impersonation is configured. Configure Domain-Wide Delegation or use a Shared Drive folder.";

            return Ok(new GoogleTestResponse(ok, message, onSharedDrive, hasImpersonation ? impersonateEmail : null, folderInfo.DriveId));
        }
        catch (Exception ex)
        {
            return BadRequest(new OkFalseErrorResponse(false, ex.Message));
        }
    }

    [HttpGet("drives")]
    public async Task<ActionResult> GetDrives()
    {
        try
        {
            var drives = await _drive.ListSharedDrivesAsync();
            return Ok(new { ok = true, drives });
        }
        catch (Exception ex)
        {
            return BadRequest(new OkFalseErrorResponse(false, ex.Message));
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter GoogleControllerTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add .
git commit -m "Add GoogleController"
```

---

## Task 19: TransferController

**Files:**
- Create: `src/RecordingCopyNet/Controllers/TransferController.cs`
- Test: `tests/RecordingCopyNet.Tests/Controllers/TransferControllerTests.cs`

**Interfaces:**
- Consumes: `ITransferService` (Task 10), `SseWriter` (Task 16).
- Produces: `POST /api/transfer/{meetingId}` — SSE frames `{type:"progress",message}`, `{type:"complete",result}`, `{type:"error",message}` (spec §12).

**A deliberate design note:** `ITransferService.TransferMeetingAsync`'s progress callback is `Action<string>` (synchronous), but writing an SSE frame is async. Firing the write without awaiting it risks two progress messages interleaving mid-write on the same response stream. Since the callback itself is synchronous, the controller blocks on `.GetAwaiter().GetResult()` inside it rather than changing `ITransferService`'s signature — each progress write completes before `TransferService` moves on to its next step, which is exactly the ordering guarantee the SSE stream needs.

- [ ] **Step 1: Write the failing test**

`tests/RecordingCopyNet.Tests/Controllers/TransferControllerTests.cs`

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter TransferControllerTests`
Expected: FAIL — `TransferController` does not exist.

- [ ] **Step 3: Write `TransferController`**

`src/RecordingCopyNet/Controllers/TransferController.cs`

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter TransferControllerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add .
git commit -m "Add TransferController"
```

---

## Task 20: EventsController

**Files:**
- Create: `src/RecordingCopyNet/Controllers/EventsController.cs`
- Test: `tests/RecordingCopyNet.Tests/Controllers/EventsControllerTests.cs`

**Interfaces:**
- Consumes: `IEventsRepository` (Task 4).
- Produces: `GET /api/events/stats`, `GET /api/events/{id}`, `GET /api/events` (spec §12). The `limit`/`offset` clamping (`min(limit, 200)`, default 50/0) happens here in the controller, not in the repository — matching how `routes/api.js` does the clamping while `lib/events.js` just takes whatever limit/offset it's given.

- [ ] **Step 1: Write the failing test**

`tests/RecordingCopyNet.Tests/Controllers/EventsControllerTests.cs`

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter EventsControllerTests`
Expected: FAIL — `EventsController` does not exist.

- [ ] **Step 3: Write `EventsController`**

`src/RecordingCopyNet/Controllers/EventsController.cs`

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter EventsControllerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add .
git commit -m "Add EventsController"
```

---

## Task 21: RequestsController

**Files:**
- Create: `src/RecordingCopyNet/Models/RequestApiModels.cs`
- Create: `src/RecordingCopyNet/Controllers/RequestsController.cs`
- Test: `tests/RecordingCopyNet.Tests/Controllers/RequestsControllerTests.cs`

**Interfaces:**
- Consumes: `IRequestsRepository` (Task 5), `ITransferService` (Task 10).
- Produces: `POST /api/requests`, `GET /api/requests`, `POST /api/requests/{id}/transfer` (spec §12).

- [ ] **Step 1: Write the request/response DTOs**

`src/RecordingCopyNet/Models/RequestApiModels.cs`

```csharp
using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public class CreateRequestBody
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("surname")] public string? Surname { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("meeting_id")] public string? MeetingId { get; set; }
    [JsonPropertyName("meeting_date")] public string? MeetingDate { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public record CreateRequestResponse([property: JsonPropertyName("id")] long Id);
```

- [ ] **Step 2: Write the failing test**

`tests/RecordingCopyNet.Tests/Controllers/RequestsControllerTests.cs`

```csharp
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
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter RequestsControllerTests`
Expected: FAIL — `RequestsController` does not exist.

- [ ] **Step 4: Write `RequestsController`**

`src/RecordingCopyNet/Controllers/RequestsController.cs`

```csharp
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
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RecordingCopyNet.Tests --filter RequestsControllerTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add .
git commit -m "Add RequestsController"
```

---

## Task 22: Port the frontend (public/ → wwwroot/)

**Files:**
- Create: `src/RecordingCopyNet/wwwroot/index.html`
- Create: `src/RecordingCopyNet/wwwroot/dashboard.html`
- Create: `src/RecordingCopyNet/wwwroot/settings.html`
- Create: `src/RecordingCopyNet/wwwroot/request.html`
- Create: `src/RecordingCopyNet/wwwroot/audit.html`
- Create: `src/RecordingCopyNet/wwwroot/js/api.js`
- Create: `src/RecordingCopyNet/wwwroot/js/app.js`
- Create: `src/RecordingCopyNet/wwwroot/js/dashboard.js`
- Create: `src/RecordingCopyNet/wwwroot/js/settings.js`
- Create: `src/RecordingCopyNet/wwwroot/js/request.js`
- Create: `src/RecordingCopyNet/wwwroot/js/audit.js`
- Create: `src/RecordingCopyNet/wwwroot/css/*` (whatever stylesheet files exist in the source repo)
- Delete: `src/RecordingCopyNet/wwwroot/.gitkeep`

**Interfaces:** none — this task moves static assets, it doesn't add C# types. The API surface it depends on (spec §12/§13) was built in Tasks 16-21; this task is what proves those controllers actually match what the frontend expects.

- [ ] **Step 1: Copy every static file from the Node app's `public/` into `wwwroot/`**

```bash
cd "C:\Coding\RecordingCopyNet"
rm src/RecordingCopyNet/wwwroot/.gitkeep
cp -r "C:\Coding\RecordingCopy\public\"* src/RecordingCopyNet/wwwroot/
```

- [ ] **Step 2: Confirm nothing in the copied JS assumes a Node-specific API path or shape**

```bash
grep -rn "fetch(" src/RecordingCopyNet/wwwroot/js/
grep -rn "EventSource(" src/RecordingCopyNet/wwwroot/js/
```

Cross-check every URL these print against the endpoint tables in Tasks 16-21 (all under `/api/...`, matching exactly). If anything doesn't match — an endpoint this plan didn't cover, or a casing mismatch — stop and reconcile it against the Node source (`C:\Coding\RecordingCopy\public\js\`) before continuing; do not edit the copied JS speculatively.

- [ ] **Step 3: Run the existing test suite to confirm the copy didn't touch anything it shouldn't have**

Run: `dotnet test tests/RecordingCopyNet.Tests`
Expected: PASS (same count as after Task 21 — this task adds no C# and should change no test outcomes)

- [ ] **Step 4: Manually verify the pages are served**

```bash
dotnet run --project src/RecordingCopyNet &
sleep 3
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:3900/
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:3900/dashboard.html
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:3900/settings.html
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:3900/request.html
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:3900/audit.html
curl -s http://localhost:3900/api/version
```

Expected: all five page requests return `200`, and `/api/version` returns `{"version":"0.1.0"}`. Stop the running process afterward.

- [ ] **Step 5: Commit**

```bash
git add .
git commit -m "Port frontend from public/ to wwwroot/"
```

---

## Task 23: Windows Service hosting

**Files:**
- Modify: `src/RecordingCopyNet/RecordingCopyNet.csproj`
- Modify: `src/RecordingCopyNet/Program.cs`

**Interfaces:** none — this task changes how the existing `WebApplication` host is *run*, not what it does.

- [ ] **Step 1: Add the Windows Service hosting package**

```bash
cd src/RecordingCopyNet
dotnet add package Microsoft.Extensions.Hosting.WindowsServices
cd "C:\Coding\RecordingCopyNet"
```

- [ ] **Step 2: Enable Windows Service hosting — modify `Program.cs`**

Add immediately after `var builder = WebApplication.CreateBuilder(args);`:

```csharp
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "RecordingCopyNet";
});
```

`UseWindowsService()` is a no-op (and harmless) when the process isn't actually running as a registered Windows Service — running `dotnet run` or the built exe directly from a console still works exactly as before. This is what lets the same binary serve both local dev and the "always-on" production mode from spec §16.

- [ ] **Step 3: Verify console mode still works**

```bash
dotnet run --project src/RecordingCopyNet &
sleep 3
curl -s http://localhost:3900/api/version
```

Expected: `{"version":"0.1.0"}`. Stop the process afterward. This confirms `UseWindowsService()` didn't change console-mode behavior.

- [ ] **Step 4: Document (do not execute) how to install it as a service**

This step only writes documentation — installing a Windows Service requires an elevated prompt and is a deployment action the plan's author will not take unilaterally. Add to `README.md` (created in Task 24) a section with:

```
To run as a Windows Service (requires an elevated PowerShell prompt):

  dotnet publish src/RecordingCopyNet -c Release -o publish
  sc.exe create RecordingCopyNet binPath= "C:\path\to\publish\RecordingCopyNet.exe"
  sc.exe start RecordingCopyNet

To remove it:

  sc.exe stop RecordingCopyNet
  sc.exe delete RecordingCopyNet
```

- [ ] **Step 5: Commit**

```bash
git add .
git commit -m "Add Windows Service hosting support"
```

---

## Task 24: README, final full-suite verification, and manual end-to-end smoke test

**Files:**
- Create: `README.md`

**Interfaces:** none — this is the closing verification task for the whole plan.

- [ ] **Step 1: Write `README.md`**

```markdown
# RecordingCopyNet

ASP.NET Core 8 port of [RecordingCopy](https://github.com/) — automates transferring
Zoom cloud recordings to Google Drive, either automatically (via a Zoom WebSocket
event subscription) or on demand (manual browse, or a public "request a transfer"
form).

Full design: `docs/superpowers/specs/2026-09-21-recordingcopy-dotnet-port-design.md`
Implementation plan: `docs/superpowers/plans/2026-09-21-recordingcopy-dotnet-port.md`

## Tech Stack

- ASP.NET Core 8 (MVC Controllers)
- Dapper + SQLite
- Google.Apis.Drive.v3 / Google.Apis.Auth
- System.Net.WebSockets.ClientWebSocket

## Getting Started

```bash
dotnet restore
dotnet test tests/RecordingCopyNet.Tests
dotnet run --project src/RecordingCopyNet
```

The dashboard is available at `http://localhost:3900` by default. Configure Zoom
OAuth2 and Google Drive API credentials on the Settings page before use.

## Running as a Windows Service

(requires an elevated PowerShell prompt)

    dotnet publish src/RecordingCopyNet -c Release -o publish
    sc.exe create RecordingCopyNet binPath= "C:\path\to\publish\RecordingCopyNet.exe"
    sc.exe start RecordingCopyNet

To remove it:

    sc.exe stop RecordingCopyNet
    sc.exe delete RecordingCopyNet

## Version

0.1.0
```

- [ ] **Step 2: Run the complete test suite one final time**

Run: `dotnet test`
Expected: every test from Tasks 1-21 passes. If anything regressed, fix it before continuing — this is the plan's acceptance gate, not a formality.

- [ ] **Step 3: Manual end-to-end smoke test (the only real verification for the untested SDK adapters from Task 9 and the socket I/O from Task 15)**

This requires real Zoom Server-to-Server OAuth credentials and a real Google service account with either a Shared Drive target or Domain-Wide Delegation configured — the same prerequisites the original Node app's README lists. Run through this checklist manually and record the result of each step:

1. `dotnet run --project src/RecordingCopyNet`, open `http://localhost:3900/settings.html`.
2. Enter Zoom credentials, click "Test Connection" — expect success (exercises `ZoomAuthService` + `ZoomController.Test`).
3. Enter Google credentials and a target folder ID, click "Test Connection" — expect success (exercises `GoogleAuthService`/`GoogleDriveService`, the classes Task 9 deliberately left unit-tested-only-by-proxy).
4. Enter a Zoom WebSocket subscription URL, save settings — expect the Settings page's live log (via `/api/zoom/websocket/stream`) to show `Connecting...` then `Connected to Zoom` within a few seconds (exercises the real `ClientWebSocket` connect/heartbeat loop from Task 15 that unit tests couldn't reach).
5. On the Dashboard, browse recordings for a known user and manually transfer one — expect the progress log to reach "Transfer complete!" and the file to appear in the target Google Drive folder.
6. Trigger (or wait for) a real `recording.completed` event from Zoom — expect a row to appear on the Dashboard's event log with status `completed`, and the recording to land in Drive without any manual action.
7. Submit the public request form (`request.html`) for a past meeting, then process it from the Dashboard — expect the same successful outcome as step 5.
8. Restart the app (`Ctrl+C`, `dotnet run` again) — expect the WebSocket to reconnect automatically on startup (Task 15's `ExecuteAsync` → `StartConnectionAsync`), and previously-saved credentials/settings to still be present (proves `CredentialStore`'s encryption round-trips across process restarts, not just within a single test run).

Record which of these 8 steps passed and which didn't; any failure here is a real bug this plan's unit tests couldn't have caught, and should be fixed with a small targeted change (plus a regression test where the failure was in testable code) rather than a broad rewrite.

- [ ] **Step 4: Commit**

```bash
git add .
git commit -m "Add README and complete manual smoke-test verification"
```

---

## Plan self-review

**Spec coverage:** every numbered section of the design spec maps to at least one task — §4/§5 (architecture, project structure) → Task 1; §7 (data model) → Tasks 2-5; §8 (encryption) → Task 3; §9 (Zoom) → Tasks 6-8, 11-15; §10 (Google) → Task 9; §11 (transfer orchestration) → Task 10; §12 (HTTP API) → Tasks 16-21; §13 (frontend) → Task 22; §14 (error handling) → threaded through every service/controller task rather than a standalone task, since it's a cross-cutting property, not a component; §15 (testing) → present in every task's own test step, plus the manual smoke test in Task 24; §16 (deployment) → Task 23; §17 (open risks) → package versions resolved without guessing (every `dotnet add package` step), the `.key` file ACL has a concrete implementation (Task 3 Step 4), and SSE buffering/keep-alive behavior is both implemented (`X-Accel-Buffering: no`, 30s comment pings in Tasks 17/19/21) and explicitly left for manual verification under real network conditions (Task 24 Step 3) since a reverse-proxy scenario can't be unit tested.

**Placeholder scan:** no task contains "TBD", "add appropriate error handling," or a description without accompanying code — every step that changes code shows the actual code, including the two intentionally-thin adapters in Task 9, which are explicit about *why* they have no unit test rather than silently missing one.

**Type consistency check performed:** `EventUpdateFields`/`RequestUpdateFields` (Task 4/5) are used with identical property names in `ZoomWebSocketListener` (Task 15) and `RequestsController` (Task 21); `ITransferService.TransferMeetingAsync(string, Action<string>?, CancellationToken)` (Task 10) is called with that exact signature from `ZoomWebSocketListener` (Task 15), `TransferController` (Task 19), and `RequestsController` (Task 21); `IZoomWebSocketController` (Task 15) methods are called with matching names from `CredentialsController` (Task 16) and `ZoomController` (Task 17); `CredentialType`/`CredentialSchema` (Task 3) are used consistently by every controller that touches credentials (Tasks 16-18).

