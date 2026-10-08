namespace FluxVault.Abstractions.Configuration;

public sealed record FluxVaultProfileConfiguration(
    string Id,
    string DisplayName,
    bool IsEnabled,
    FluxVaultConfiguration Configuration)
{
    public const string DefaultProfileId = "default";

    public static FluxVaultProfileConfiguration CreateDefault(string programDataPath)
    {
        return new FluxVaultProfileConfiguration(
            DefaultProfileId,
            "Default",
            IsEnabled: true,
            FluxVaultConfiguration.CreateDefault(programDataPath));
    }
}

public sealed record FluxVaultProfileSetConfiguration(
    string ActiveProfileId,
    IReadOnlyList<FluxVaultProfileConfiguration> Profiles)
{
    public static FluxVaultProfileSetConfiguration CreateDefault(string programDataPath)
    {
        var profile = FluxVaultProfileConfiguration.CreateDefault(programDataPath);
        return new FluxVaultProfileSetConfiguration(profile.Id, [profile]);
    }

    public FluxVaultProfileConfiguration ActiveProfile =>
        Profiles.Count == 1 ? Profiles[0] : throw new InvalidDataException("Only a single legacy record is supported.");
}
