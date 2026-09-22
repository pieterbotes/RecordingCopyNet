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
