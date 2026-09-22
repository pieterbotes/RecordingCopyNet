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
