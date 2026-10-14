namespace FluxVault.Core.Security;

/// <summary>Coordinates source discovery/readers with acknowledged source-consent mutations.</summary>
public sealed class AutomaticSourceActivity
{
    private readonly Lock gate = new();
    private int suspensions;
    private Cycle? active;

    public Cycle? TryBegin(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
            return suspensions != 0 || active is not null ? null : active = new(this, ct);
    }

    public async ValueTask<IAsyncDisposable> SuspendAsync(CancellationToken ct)
    {
        Task joined;
        lock (gate)
        {
            suspensions++;
            // Cancellation and disposal share this lock, so a completed cycle's CTS is never reused.
            active?.Cancel();
            joined = active?.Joined ?? Task.CompletedTask;
        }
        try { await joined.WaitAsync(ct).ConfigureAwait(false); return new Lease(this); }
        catch { Release(); throw; }
    }

    private void Release() { lock (gate) suspensions--; }
    public sealed class Cycle : IDisposable
    {
        private readonly AutomaticSourceActivity owner;
        private readonly CancellationTokenSource stopping;
        private readonly TaskCompletionSource joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool disposed;
        internal Cycle(AutomaticSourceActivity owner, CancellationToken ct)
        { this.owner = owner; stopping = CancellationTokenSource.CreateLinkedTokenSource(ct); Token = stopping.Token; }
        public CancellationToken Token { get; }
        internal Task Joined => joined.Task;
        internal void Cancel() => stopping.Cancel();
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (disposed) return;
                disposed = true; owner.active = null; stopping.Dispose(); joined.TrySetResult();
            }
        }
    }
    private sealed class Lease(AutomaticSourceActivity owner) : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(); return ValueTask.CompletedTask; }
    }
}
