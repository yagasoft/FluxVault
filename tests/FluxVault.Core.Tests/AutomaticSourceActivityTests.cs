using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class AutomaticSourceActivityTests
{
    [Fact]
    public async Task Mutation_cancels_and_joins_discovery_before_acknowledgement_and_holds_new_admission()
    {
        var activity = new AutomaticSourceActivity();
        using var running = activity.TryBegin(CancellationToken.None)!;
        var suspension = activity.SuspendAsync(CancellationToken.None).AsTask();
        try
        {
            Assert.True(running.Token.IsCancellationRequested);
            Assert.False(suspension.IsCompleted);
            Assert.Null(activity.TryBegin(CancellationToken.None));
        }
        finally { running.Dispose(); }
        await using (await suspension.WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Null(activity.TryBegin(CancellationToken.None));
        using var next = activity.TryBegin(CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Overlapping_mutations_keep_discovery_suspended_until_both_finish()
    {
        var activity = new AutomaticSourceActivity();
        await using var first = await activity.SuspendAsync(CancellationToken.None);
        var second = await activity.SuspendAsync(CancellationToken.None);
        await second.DisposeAsync();
        Assert.Null(activity.TryBegin(CancellationToken.None));
        await first.DisposeAsync();
        using var next = activity.TryBegin(CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Cancelled_mutation_does_not_claim_a_reader_was_joined()
    {
        var activity = new AutomaticSourceActivity();
        using var running = activity.TryBegin(CancellationToken.None)!;
        using var cancelled = new CancellationTokenSource();
        var suspension = activity.SuspendAsync(cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspension);
        Assert.Null(activity.TryBegin(CancellationToken.None));
        running.Dispose();
        using var next = activity.TryBegin(CancellationToken.None);
        Assert.NotNull(next);
    }
}
