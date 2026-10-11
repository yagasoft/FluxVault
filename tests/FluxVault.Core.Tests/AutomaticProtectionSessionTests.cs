using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class AutomaticProtectionSessionTests
{
    [Fact]
    public async Task Authority_loss_refuses_a_new_source_action_and_cancels_the_generation()
    {
        var current = true;
        var raw = new OrdinaryCaller();
        await using var session = new AutomaticProtectionSession(raw, 7, "00000000:00000042", _ => ValueTask.FromResult(current));
        Assert.Equal(31, await session.Caller.RunAsCallerAsync(() => Task.FromResult(31)));
        current = false;
        var executed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Caller.RunAsCallerAsync(() =>
        { executed = true; return Task.FromResult(42); }));
        Assert.False(executed);
        Assert.True(session.AuthorityEnded.IsCancellationRequested);
    }

    [Fact]
    public async Task Disposal_joins_a_source_reader_before_releasing_its_token()
    {
        var raw = new OrdinaryCaller();
        var session = new AutomaticProtectionSession(raw, 7, "00000000:00000042", _ => ValueTask.FromResult(true));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = session.Caller.RunAsCallerAsync(async () => { entered.SetResult(); await release.Task; return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
            Assert.False(raw.Disposed);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Caller.RunAsCallerAsync(() => Task.FromResult(2)));
        }
        finally { release.TrySetResult(); await reading; await disposal; }
        Assert.True(raw.Disposed);
    }

    [Fact]
    public void Elevated_or_non_impersonatable_context_cannot_become_automatic_authority()
    {
        Assert.Throws<UnauthorizedAccessException>(() => new AutomaticProtectionSession(new OrdinaryCaller(true), 7, "luid", _ => ValueTask.FromResult(true)));
        Assert.Throws<UnauthorizedAccessException>(() => new AutomaticProtectionSession(new OrdinaryCaller(impersonation: false), 7, "luid", _ => ValueTask.FromResult(true)));
    }

    private sealed class OrdinaryCaller(bool elevated = false, bool impersonation = true) : FluxVaultCallerContext
    {
        internal bool Disposed;
        public override string UserSid => "S-1-5-21-1-2-3-1001";
        public override IReadOnlySet<string> EnabledGroupSids { get; } = new HashSet<string>();
        public override bool IsElevated => elevated;
        public override bool ImpersonationPermitted => impersonation;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => action();
        public override void Dispose() => Disposed = true;
    }
}
