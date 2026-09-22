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
