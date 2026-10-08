using System.Text.Json;
using System.Text.Json.Nodes;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Capture;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;
using FluxVault.Core.Service;

namespace FluxVault.Core.Tests;

public sealed class ExplicitProtectionStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_pause_state_is_repeatable_and_preserves_other_settings(bool paused)
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var initial = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { IsEnabled = !paused };
        await store.SaveAsync(initial);
        initial = await store.LoadAsync();
        var operations = new FluxVaultOperations(store, new NormalFileCaptureProvider());
        var node = JsonNode.Parse(FluxVaultIpcSerializer.SerializeRequest(FluxVaultIpcRequest.SetProtectionPaused()))!;
        node["isProtectionPaused"] = paused;
        var request = FluxVaultIpcSerializer.DeserializeRequest(node.ToJsonString());

        Assert.True((await operations.HandleAsync(request)).Success);
        Assert.Equal(!paused, (await store.LoadAsync()).IsEnabled);
        Assert.True((await operations.HandleAsync(request)).Success);

        var reopened = await store.LoadAsync();
        Assert.Equal(!paused, reopened.IsEnabled);
        Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(reopened));
        Assert.Equal(paused, JsonNode.Parse(FluxVaultIpcSerializer.SerializeRequest(request))!["isProtectionPaused"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_legacy_unreceipted_toggle_is_refused_without_changing_configuration()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath));
        var before = JsonSerializer.Serialize(await store.LoadAsync());
        var operations = new FluxVaultOperations(store, new NormalFileCaptureProvider());

        var response = await operations.HandleAsync(FluxVaultIpcRequest.SetProtectionPaused());

        Assert.False(response.Success);
        Assert.Equal(before, JsonSerializer.Serialize(await store.LoadAsync()));
    }
}
