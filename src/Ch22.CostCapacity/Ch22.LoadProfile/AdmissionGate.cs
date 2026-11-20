namespace Ch22.LoadProfile;

/// <summary>
/// Admission control in front of the model: a fixed number of slots and a
/// bounded FIFO queue. When both are full the caller gets null at once (a
/// busy response), instead of joining a queue that grows without bound and
/// turns every wait into a timeout.
/// </summary>
public sealed class AdmissionGate(int slots, int queueCapacity)
{
    readonly Lock _lock = new();
    readonly Queue<TaskCompletionSource<Lease>> _waiting = new();
    int _inFlight;

    public int MaxQueueDepth { get; private set; }

    public Task<Lease?> TryEnterAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_inFlight < slots)
            {
                _inFlight++;
                return Task.FromResult<Lease?>(new Lease(this));
            }
            if (_waiting.Count >= queueCapacity) return Task.FromResult<Lease?>(null);   // busy
            TaskCompletionSource<Lease> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(waiter);
            MaxQueueDepth = Math.Max(MaxQueueDepth, _waiting.Count);
            ct.Register(() => waiter.TrySetCanceled(ct));
            return WaitAsync(waiter);
        }
    }

    static async Task<Lease?> WaitAsync(TaskCompletionSource<Lease> waiter) => await waiter.Task;

    void Release()
    {
        lock (_lock)
        {
            // Hand the slot straight to the oldest live waiter; a canceled one is skipped.
            while (_waiting.TryDequeue(out TaskCompletionSource<Lease>? next))
                if (next.TrySetResult(new Lease(this))) return;
            _inFlight--;
        }
    }

    public sealed class Lease(AdmissionGate gate) : IDisposable
    {
        int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
