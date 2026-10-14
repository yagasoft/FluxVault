using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using FluxVault.Abstractions.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.Core.Tests;

public sealed class AutomaticProtectionPolicyTests
{
    [Fact]
    public void Automatic_preference_has_a_distinct_mutating_command_and_protection_permission()
    {
        var command = (FluxVaultIpcCommand)40;
        Assert.True(VaultCommandPolicy.TryGet(command, out var policy));
        Assert.Equal(VaultPermission.ManageProtection, policy.Permissions);
        Assert.True(PostgreSqlVaultCatalogue.IsMutation(command));
        Assert.Equal("SetAutomaticProtection", command.ToString());
    }

    [Fact]
    public void Policy_defaults_off_and_does_not_change_predecessor_configuration_shape()
    {
        Assert.False(new AutomaticProtectionPolicy().Enabled);
        new AutomaticProtectionRuntimePolicy().Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutomaticProtectionRuntimePolicy(MaximumPendingPaths:0).Validate());
        var configuration = FluxVaultConfiguration.CreateDefault(Path.GetTempPath());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(configuration));
        Assert.False(json.RootElement.TryGetProperty("AutomaticProtection",out _));
        Assert.False(json.RootElement.TryGetProperty("AutomaticProtectionPolicy",out _));
    }

    [Fact]
    public void Omitted_automatic_field_preserves_legacy_request_fingerprint_shape()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters={new JsonStringEnumConverter()} };
        var request = FluxVaultIpcRequest.RunBackupNow();
        var json = JsonSerializer.Serialize(request, options);
        Assert.DoesNotContain("isAutomaticProtectionEnabled",json);
        var enabled = FluxVaultIpcRequest.SetAutomaticProtection(true);
        Assert.True(JsonSerializer.Deserialize<FluxVaultIpcRequest>(JsonSerializer.Serialize(enabled,options),options)!.IsAutomaticProtectionEnabled);
    }
}
