namespace FluxVault.Core.Security;

/// <summary>An owned effective caller token and its verified authorisation attributes.</summary>
public abstract class FluxVaultCallerContext : IDisposable
{
    public abstract string UserSid { get; }
    public abstract IReadOnlySet<string> EnabledGroupSids { get; }
    public abstract bool IsElevated { get; }
    public abstract bool ImpersonationPermitted { get; }
    public abstract Task<T> RunAsCallerAsync<T>(Func<Task<T>> action);
    public abstract void Dispose();
}
