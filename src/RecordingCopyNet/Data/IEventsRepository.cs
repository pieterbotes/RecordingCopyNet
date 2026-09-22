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
