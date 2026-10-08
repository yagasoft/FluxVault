using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

/// <summary>Supplies the installation envelope for older presentation-only fixtures.</summary>
/// <remarks>Security and real-store save tests provide their own explicit envelopes.</remarks>
internal sealed class BoundTestServiceClient(IFluxVaultServiceClient inner) : IFluxVaultServiceClient
{
    private readonly VaultId identity = VaultId.New();
    private long revision = 1;

    public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        var response = await inner.SendAsync(request, cancellationToken);
        if (!response.Success) return response;
        if (request.Command == FluxVaultIpcCommand.SaveConfiguration) revision++;
        return response with { VaultId = response.VaultId ?? identity, VaultRevision = response.VaultRevision ?? revision,
            OperationId = response.OperationId ?? request.OperationId };
    }
}
