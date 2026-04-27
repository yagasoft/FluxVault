using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public interface IFluxVaultConfigurationStore
{
    Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default);
}
