namespace FluxVault.Abstractions.Configuration;

public sealed record VersionPreviewPolicy(int RetentionDays = 2)
{
    public VersionPreviewPolicy Normalise() => this with { RetentionDays = RetentionDays <= 0 ? 2 : Math.Min(RetentionDays, 365) };
}
