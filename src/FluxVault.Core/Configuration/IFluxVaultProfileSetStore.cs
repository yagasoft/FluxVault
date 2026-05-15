using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public interface IFluxVaultProfileSetStore
{
    Task<FluxVaultProfileSetConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(FluxVaultProfileSetConfiguration configuration, CancellationToken cancellationToken = default);
}
