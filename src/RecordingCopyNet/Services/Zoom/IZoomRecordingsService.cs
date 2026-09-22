using System.Text.Json;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services.Zoom;

public interface IZoomRecordingsService
{
    Task<JsonElement> ListRecordingsAsync(string userId, string? from, string? to, CancellationToken ct = default);
    Task<ZoomMeetingRecordings> GetMeetingRecordingsAsync(string meetingId, CancellationToken ct = default);
    Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default);
}
