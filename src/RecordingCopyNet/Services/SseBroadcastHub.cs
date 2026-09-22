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
