using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Security;

namespace FluxVault.Core.Ipc;

public interface IAuthenticatedFluxVaultRequestHandler
{
    Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request,
        CancellationToken cancellationToken = default);
}
