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
        var profiles = profileSet.Profiles
            .Select(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)
                ? profile with { IsEnabled = configuration.IsEnabled, Configuration = configuration }
                : profile)
            .ToArray();
        if (!profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            profiles = profiles
                .Append(new FluxVaultProfileConfiguration(profileId, profileId, configuration.IsEnabled, configuration))
                .ToArray();
        }

        await profileSetStore.SaveAsync(profileSet with { Profiles = profiles }, cancellationToken).ConfigureAwait(false);
    }

    private FluxVaultProfileConfiguration FindProfile(FluxVaultProfileSetConfiguration profileSet)
    {
        return profileSet.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"FluxVault profile was not found: {profileId}");
    }
}
