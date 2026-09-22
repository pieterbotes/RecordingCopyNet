using RecordingCopyNet.Services.Zoom;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class TransferQueueTests
{
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met within timeout");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Enqueue_RunsUpToLimitConcurrently_ThenQueuesTheRest()
    {
        var queue = new TransferQueue(limit: 2);
        var gate = new TaskCompletionSource();
        var started = new List<int>();
        var completions = new TaskCompletionSource[4];
        for (var i = 0; i < 4; i++) completions[i] = new TaskCompletionSource();

        for (var i = 0; i < 4; i++)
        {
            var index = i;
            queue.Enqueue(async () =>
            {
                lock (started) started.Add(index);
                await completions[index].Task;
            });
        }

        await Task.Delay(50); // let the first batch start

        var info = queue.GetQueueInfo();
        Assert.Equal(2, info.Active);
        Assert.Equal(2, info.Queued);
        Assert.Equal(2, started.Count);

        completions[started[0]].SetResult();
        completions[started[1]].SetResult();
        await WaitUntil(() => { lock (started) return started.Count == 4; });

        Assert.Equal(4, started.Count); // the queued two have now started

        foreach (var tcs in completions) if (!tcs.Task.IsCompleted) tcs.SetResult();
        await WaitUntil(() =>
        {
            var info = queue.GetQueueInfo();
            return info.Active == 0 && info.Queued == 0;
        });

        var finalInfo = queue.GetQueueInfo();
        Assert.Equal(0, finalInfo.Active);
        Assert.Equal(0, finalInfo.Queued);
    }

    [Fact]
    public async Task Enqueue_ContinuesDraining_EvenWhenATaskThrows()
    {
        var queue = new TransferQueue(limit: 1);
        var secondRan = new TaskCompletionSource();

        queue.Enqueue(() => throw new InvalidOperationException("boom"));
        queue.Enqueue(() => { secondRan.SetResult(); return Task.CompletedTask; });

        var completed = await Task.WhenAny(secondRan.Task, Task.Delay(1000));
        Assert.Same(secondRan.Task, completed);
    }
}
