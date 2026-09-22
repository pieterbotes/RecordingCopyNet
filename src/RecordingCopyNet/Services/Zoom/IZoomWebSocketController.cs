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
