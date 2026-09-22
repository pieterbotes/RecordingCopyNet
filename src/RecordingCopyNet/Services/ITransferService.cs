using RecordingCopyNet.Models;

namespace RecordingCopyNet.Services;

public interface ITransferService
{
    Task<TransferResult> TransferMeetingAsync(string meetingId, Action<string>? onProgress, CancellationToken ct = default);
}
