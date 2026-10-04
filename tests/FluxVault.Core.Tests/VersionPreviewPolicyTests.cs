using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Configuration;

namespace FluxVault.Core.Tests;

public sealed class VersionPreviewPolicyTests
{
    [Theory]
    [InlineData(null, 2)]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(11, 11)]
    [InlineData(900, 365)]
    public async Task Real_store_defaults_and_round_trips_bounded_preview_retention(int? days, int expected)
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.RootPath, "config.json");
        var store = new FileFluxVaultConfigurationStore(path, workspace.RootPath);
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { VersionPreview = days is { } value ? new(value) : null! };
        await store.SaveAsync(configuration);
        var reopened = await new FileFluxVaultConfigurationStore(path, workspace.RootPath).LoadAsync();
        Assert.NotNull(reopened.VersionPreview);
        Assert.Equal(expected, reopened.VersionPreview.RetentionDays);
    }
}
