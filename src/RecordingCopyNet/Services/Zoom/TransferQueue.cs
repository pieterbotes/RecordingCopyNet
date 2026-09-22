namespace RecordingCopyNet.Services.Zoom;

// Direct port of enqueueTransfer/drainTransferQueue in lib/zoom/websocket.js.
// A task that throws synchronously or whose returned Task faults must not
// stop the queue from draining — mirrors the Node comment: "task() must
// never reject — it handles its own errors," enforced defensively here too.
public class TransferQueue
{
    private readonly int _limit;
    private readonly object _gate = new();
    private int _active;
    private readonly Queue<Func<Task>> _queue = new();

    public TransferQueue(int limit = 3) => _limit = limit;

    public (int Active, int Queued, int Limit) GetQueueInfo()
    {
        lock (_gate) return (_active, _queue.Count, _limit);
    }

    public void Enqueue(Func<Task> task)
    {
        lock (_gate) _queue.Enqueue(task);
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            Func<Task>? task;
            lock (_gate)
            {
                if (_active >= _limit || _queue.Count == 0) return;
                task = _queue.Dequeue();
                _active++;
            }

            RunOne(task);
        }
    }

    private void RunOne(Func<Task> task)
    {
        Task runTask;
        try { runTask = task(); }
        catch { runTask = Task.CompletedTask; }

        runTask.ContinueWith(_ =>
        {
            lock (_gate) _active--;
            Drain();
        }, TaskScheduler.Default);
    }
}
