using RecordingCopyNet.Models;

namespace RecordingCopyNet.Data;

public interface IRequestsRepository
{
    long CreateRequest(string name, string surname, string email, string meetingId, string meetingDate, string? reason);
    void UpdateRequest(long id, RequestUpdateFields fields);
    List<RequestRecord> ListRequests(int limit = 50, int offset = 0);
    RequestRecord? GetRequest(long id);
}
