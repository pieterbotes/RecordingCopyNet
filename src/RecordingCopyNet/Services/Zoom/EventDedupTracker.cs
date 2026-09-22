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
