using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public sealed class FluxVaultProfileConfigurationStore(
    IFluxVaultProfileSetStore profileSetStore,
    string profileId) : IFluxVaultConfigurationStore
{
    public async Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return FindProfile(profileSet).Configuration;
    }

    public async Task SaveAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = FindProfile(profileSet);
        await profileSetStore.SaveAsync(profileSet with { Profiles = [profile with { IsEnabled = configuration.IsEnabled, Configuration = configuration }] }, cancellationToken).ConfigureAwait(false);
    }

    private FluxVaultProfileConfiguration FindProfile(FluxVaultProfileSetConfiguration profileSet)
    {
        return profileSet.Profiles.Count == 1 && profileSet.Profiles[0].Id == profileId ? profileSet.Profiles[0]
            : throw new InvalidDataException("The legacy file does not contain the expected single record.");
    }
}
