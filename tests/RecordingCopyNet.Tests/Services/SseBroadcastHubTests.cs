using RecordingCopyNet.Services;
using Xunit;

namespace RecordingCopyNet.Tests.Services;

public class SseBroadcastHubTests
{
    [Fact]
    public async Task BroadcastAsync_DeliversFormattedFrameToSubscribers()
    {
        var hub = new SseBroadcastHub();
        string? received = null;
        hub.Subscribe(frame => { received = frame; return Task.CompletedTask; });

        await hub.BroadcastAsync("append", new { msg = "hello" });

        Assert.Equal("event: append\ndata: {\"msg\":\"hello\"}\n\n", received);
    }

    [Fact]
    public async Task BroadcastAsync_DoesNothing_WhenNoSubscribers()
    {
        var hub = new SseBroadcastHub();
        await hub.BroadcastAsync("append", new { msg = "hello" }); // must not throw
    }

    [Fact]
    public void Subscribe_IncrementsAndDisposeDecrementsSubscriberCount()
    {
        var hub = new SseBroadcastHub();
        Assert.Equal(0, hub.SubscriberCount);

        var subscription = hub.Subscribe(_ => Task.CompletedTask);
        Assert.Equal(1, hub.SubscriberCount);

        subscription.Dispose();
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task BroadcastAsync_RemovesSubscriberWhoseWriterThrows()
    {
        var hub = new SseBroadcastHub();
        hub.Subscribe(_ => throw new IOException("client vanished"));

        await hub.BroadcastAsync("append", new { });

        Assert.Equal(0, hub.SubscriberCount);
    }
}
