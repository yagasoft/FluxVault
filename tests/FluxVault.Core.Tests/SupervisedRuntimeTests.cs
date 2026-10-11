using FluxVault.Core.Service;

namespace FluxVault.Core.Tests;

public sealed class SupervisedRuntimeTests
{
    [Fact]
    public async Task First_fault_cancels_and_joins_a_sibling_before_returning_the_fault()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new IOException("owned loop fault");
        var running = SupervisedRuntime.RunAsync(async _ => { await entered.Task; throw fault; }, async ct =>
        {
            using var registration = ct.Register(() => cancelled.TrySetResult());
            entered.SetResult(); await release.Task;
        }, CancellationToken.None);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Same(fault, await Assert.ThrowsAsync<IOException>(() => running));
    }

    [Fact]
    public async Task Unexpected_successful_loop_exit_is_a_failure_and_joins_the_other_loop()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var joined = false;
        await Assert.ThrowsAsync<IOException>(() => SupervisedRuntime.RunAsync(_ => Task.CompletedTask, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { joined = true; }
        }, deadline.Token));
        Assert.True(joined);
    }
}
