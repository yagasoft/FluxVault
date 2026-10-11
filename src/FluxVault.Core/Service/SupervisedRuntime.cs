namespace FluxVault.Core.Service;

/// <summary>The service lifetime includes both loops and every cancellation join.</summary>
public static class SupervisedRuntime
{
    public static async Task RunAsync(Func<CancellationToken, Task> first, Func<CancellationToken, Task> second, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run also contains synchronous startup faults so the sibling is always owned/joined.
        var tasks = new[] { Task.Run(() => first(stopping.Token), CancellationToken.None),
            Task.Run(() => second(stopping.Token), CancellationToken.None) };
        var ended = await Task.WhenAny(tasks).ConfigureAwait(false);
        var unexpectedExit = !ct.IsCancellationRequested && ended.IsCompletedSuccessfully;
        stopping.Cancel();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch
        {
            var fault = tasks.FirstOrDefault(task => task.IsFaulted);
            if (fault is not null) await fault.ConfigureAwait(false);
            if (!ct.IsCancellationRequested && !unexpectedExit) throw;
        }
        if (unexpectedExit) throw new IOException("An owned service loop ended unexpectedly; its siblings have been cancelled and joined.");
    }
}
