namespace FluxVault.Core.Ipc;

/// <summary>Returns an authenticated, connected local pipe before request data is sent.</summary>
public interface IFluxVaultPipeClientFactory
{
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);
}
