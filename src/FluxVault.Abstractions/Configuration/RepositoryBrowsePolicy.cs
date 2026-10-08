namespace FluxVault.Abstractions.Configuration;

public sealed record RepositoryBrowsePolicy(int ItemsPerPage = 100)
{
    public const int MaximumItemsPerPage = 256;
    public RepositoryBrowsePolicy Normalise() => this with { ItemsPerPage = Math.Clamp(ItemsPerPage, 1, MaximumItemsPerPage) };
}
