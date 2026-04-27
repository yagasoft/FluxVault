using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Ipc;

public interface IFluxVaultRequestHandler
{
    Task<FluxVaultIpcResponse> HandleAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default);
}
