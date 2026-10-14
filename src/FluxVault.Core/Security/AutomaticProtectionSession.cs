namespace FluxVault.Core.Security;

public interface IAutomaticProtectionSessionProvider
{
    ValueTask<AutomaticProtectionSession?> AcquireAsync(string creatorSid, CancellationToken cancellationToken);
}

/// <summary>Owns one ordinary logon generation and joins users before releasing its token.</summary>
public sealed class AutomaticProtectionSession : IAsyncDisposable
{
    private readonly FluxVaultCallerContext owned;
    private readonly Func<CancellationToken, ValueTask<bool>> isCurrent;
    private readonly CancellationTokenSource ended = new();
    private readonly Lock gate = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int users;
    private bool stopping;
    private Task? disposal;

    public AutomaticProtectionSession(FluxVaultCallerContext caller, int sessionId, string authenticationId,
        Func<CancellationToken, ValueTask<bool>> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(isCurrent);
        if (caller.IsElevated || !caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid) ||
            sessionId < 0 || string.IsNullOrWhiteSpace(authenticationId))
            throw new UnauthorizedAccessException("An ordinary, verified creator logon is required for automatic source access.");
        owned = caller; this.isCurrent = isCurrent; SessionId = sessionId; AuthenticationId = authenticationId;
        AuthorityEnded = ended.Token;
        Caller = new GuardedCaller(this);
    }
    public FluxVaultCallerContext Caller { get; }
    public int SessionId { get; }
    public string AuthenticationId { get; }
    public CancellationToken AuthorityEnded { get; }
    public async ValueTask VerifyAsync(CancellationToken ct = default)
    {
        Enter();
        try { await VerifyCoreAsync(ct).ConfigureAwait(false); }
        finally { Exit(); }
    }

    private async ValueTask VerifyCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (AuthorityEnded.IsCancellationRequested || !await isCurrent(ct).ConfigureAwait(false))
                throw new UnauthorizedAccessException("The creator logon ended or was replaced. Automatic work remains pending.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { ended.Cancel(); throw; }
    }

    private void Enter()
    {
        lock (gate)
        {
            if (stopping || AuthorityEnded.IsCancellationRequested)
                throw new UnauthorizedAccessException("The creator logon generation is no longer available.");
            users++;
        }
    }
    private void Exit() { lock (gate) { users--; if (stopping && users == 0) drained.TrySetResult(); } }
    private async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        Enter();
        try
        {
            await VerifyCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return await owned.RunAsCallerAsync(action).ConfigureAwait(false);
        }
        finally { Exit(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            stopping = true;
            if (users == 0) drained.TrySetResult();
            return new(disposal ??= DisposeOwnedAsync());
        }
    }
    private async Task DisposeOwnedAsync()
    {
        ended.Cancel();
        await drained.Task.ConfigureAwait(false);
        owned.Dispose(); ended.Dispose();
    }
    private sealed class GuardedCaller(AutomaticProtectionSession session) : FluxVaultCallerContext
    {
        public override string UserSid => session.owned.UserSid;
        public override IReadOnlySet<string> EnabledGroupSids => session.owned.EnabledGroupSids;
        public override bool IsElevated => false;
        public override bool ImpersonationPermitted => !session.AuthorityEnded.IsCancellationRequested;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => session.RunAsync(action);
        // Only the session owner may release the retained token, after joining its users.
        public override void Dispose() { }
    }
}
