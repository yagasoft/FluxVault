using System.Text.Json;
using FluxVault.Abstractions.Security;

namespace FluxVault.Core.Tests;

public sealed class VaultIdentityTests
{
    [Fact]
    public void Identity_survives_serialisation_and_has_a_unique_safe_metadata_namespace()
    {
        var first = VaultId.New();
        var second = VaultId.New();
        Assert.NotEqual(first, second);
        Assert.Equal("\"" + first.Value.ToString("D") + "\"", JsonSerializer.Serialize(first));
        Assert.Equal(first, JsonSerializer.Deserialize<VaultId>(JsonSerializer.Serialize(first)));
        Assert.Equal("fv_" + first.Value.ToString("N"), first.MetadataNamespace);
        Assert.NotEqual(first.MetadataNamespace, second.MetadataNamespace);
        Assert.Matches("^fv_[0-9a-f]{32}$", first.MetadataNamespace);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("fv_123; DROP SCHEMA fluxvault")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    public void Empty_legacy_and_noncanonical_external_identities_are_refused(string value)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VaultId>(JsonSerializer.Serialize(value)));
    }

    [Fact]
    public void Default_value_cannot_select_a_metadata_namespace_or_be_written_to_the_protocol()
    {
        Assert.Throws<ArgumentException>(() => new VaultId(Guid.Empty));
        Assert.Throws<InvalidOperationException>(() => default(VaultId).MetadataNamespace);
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(default(VaultId)));
    }
}
