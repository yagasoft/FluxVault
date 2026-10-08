using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class InstallationConfigurationTests
{
    [Fact]
    public void Protected_installation_round_trip_retains_exact_binding_and_creator()
    {
        var installation = Create();
        var loaded = FluxVaultInstallation.Parse(JsonSerializer.SerializeToUtf8Bytes(installation));
        Assert.Equal(installation, loaded);
        Assert.Equal(installation.Binding.Id.MetadataNamespace, loaded.Binding.MetadataNamespace);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("schema")]
    [InlineData("endpoint")]
    [InlineData("creator")]
    public void Untrusted_or_ambiguous_bootstrap_is_rejected(string defect)
    {
        var installation = Create();
        var payload = JsonSerializer.SerializeToUtf8Bytes(defect switch
        {
            "schema" => installation with { SchemaVersion = 2 },
            "endpoint" => installation with { Endpoint = installation.Endpoint with { Database = "another" } },
            "creator" => installation with { CreatorSid = "S-1-5-18" },
            _ => installation
        });
        if (defect is "unknown" or "duplicate")
        {
            var json = System.Text.Encoding.UTF8.GetString(payload);
            var prefix = defect == "unknown" ? "\"Unexpected\":true," : "\"SchemaVersion\":1,";
            payload = System.Text.Encoding.UTF8.GetBytes("{" + prefix + json[1..]);
        }
        if (defect == "oversized") payload = new byte[FluxVaultInstallation.MaximumBytes + 1];
        Assert.ThrowsAny<Exception>(() => FluxVaultInstallation.Parse(payload));
    }

    private static FluxVaultInstallation Create()
    {
        var endpoint = new VaultCatalogueEndpoint(Guid.NewGuid(), "localhost", 5432, "fluxvault", "fluxvault_service");
        var metadata = MetadataStoreConfiguration.CreateDefault(@"C:\ProgramData\FluxVault") with
            { DatabaseName = endpoint.Database, Username = endpoint.ServiceRole };
        return new(endpoint, new(VaultId.New(), @"C:\ProgramData\FluxVault\repository", @"C:\ProgramData\FluxVault\state", metadata),
            "S-1-5-21-100-200-300-1001");
    }
}
