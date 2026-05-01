namespace FluxVault.Abstractions.Configuration;

public enum DirectCloudProvider
{
    AzureBlob = 0,
    S3Compatible = 1,
    Dropbox = 2,
    GoogleDrive = 3,
    OneDrive = 4
}

public sealed record DirectCloudConfiguration(
    bool IsEnabled,
    IReadOnlyList<DirectCloudAdapterConfiguration> Adapters = null!,
    bool BlockOnMeteredNetwork = true,
    long? BandwidthLimitBytesPerSecond = null)
{
    public static DirectCloudConfiguration CreateDefault()
    {
        return new DirectCloudConfiguration(IsEnabled: false, Adapters: []);
    }

    public DirectCloudConfiguration Normalise()
    {
        return this with
        {
            Adapters = (Adapters ?? [])
                .Where(adapter => !string.IsNullOrWhiteSpace(adapter.Id))
                .Select(adapter => adapter.Normalise())
                .ToArray(),
            BandwidthLimitBytesPerSecond = BandwidthLimitBytesPerSecond is > 0
                ? BandwidthLimitBytesPerSecond
                : null
        };
    }
}

public sealed record DirectCloudAdapterConfiguration(
    string Id,
    DirectCloudProvider Provider,
    string DisplayName,
    string? Endpoint,
    string? ContainerOrBucket,
    string RootPrefix,
    string CredentialReference,
    bool IsEnabled = false)
{
    public DirectCloudAdapterConfiguration Normalise()
    {
        var displayName = string.IsNullOrWhiteSpace(DisplayName)
            ? Provider.ToString()
            : DisplayName.Trim();
        var rootPrefix = string.IsNullOrWhiteSpace(RootPrefix)
            ? "fluxvault/repository"
            : RootPrefix.Trim().TrimStart('/', '\\');
        return this with
        {
            Id = Id.Trim(),
            DisplayName = displayName,
            Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? null : Endpoint.Trim(),
            ContainerOrBucket = string.IsNullOrWhiteSpace(ContainerOrBucket) ? null : ContainerOrBucket.Trim(),
            RootPrefix = rootPrefix,
            CredentialReference = CredentialReference.Trim()
        };
    }
}
