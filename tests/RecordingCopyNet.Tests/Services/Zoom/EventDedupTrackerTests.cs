using Microsoft.Extensions.Time.Testing;
using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class EventDedupTrackerTests
{
    [Fact]
    public void TryMarkProcessed_ReturnsTrue_OnFirstOccurrence()
    {
        var tracker = new EventDedupTracker(new FakeTimeProvider(), TimeSpan.FromHours(1));
        Assert.True(tracker.TryMarkProcessed("uuid-1"));
    }

    [Fact]
    public void TryMarkProcessed_ReturnsFalse_OnDuplicateWithinTtl()
    {
        var tracker = new EventDedupTracker(new FakeTimeProvider(), TimeSpan.FromHours(1));
        tracker.TryMarkProcessed("uuid-1");
        Assert.False(tracker.TryMarkProcessed("uuid-1"));
    }

    [Fact]
    public void TryMarkProcessed_ReturnsTrue_AfterTtlExpires()
    {
        var time = new FakeTimeProvider();
        var tracker = new EventDedupTracker(time, TimeSpan.FromHours(1));
        tracker.TryMarkProcessed("uuid-1");

        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        Assert.True(tracker.TryMarkProcessed("uuid-1"));
    }

    [Fact]
    public void Forget_AllowsImmediateRetryRegardlessOfTtl()
    {
        var tracker = new EventDedupTracker(new FakeTimeProvider(), TimeSpan.FromHours(1));
        tracker.TryMarkProcessed("uuid-1");

        tracker.Forget("uuid-1");

        Assert.True(tracker.TryMarkProcessed("uuid-1"));
    }
}
