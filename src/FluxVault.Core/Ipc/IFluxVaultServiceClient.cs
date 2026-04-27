using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Ipc;

public interface IFluxVaultServiceClient
{
    Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default);
}
