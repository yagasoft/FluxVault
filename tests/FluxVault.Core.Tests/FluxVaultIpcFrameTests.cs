using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Tests;

public sealed class FluxVaultIpcFrameTests
{
    [Fact]
    public async Task Known_members_exceed_the_configured_depth_and_succeed_under_the_normal_depth()
    {
        const string payload = "{\"command\":0,\"configuration\":{\"watchedFolders\":[{\"includePatterns\":[\"*.docx\"]}]}}\n";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var exception = await Assert.ThrowsAsync<JsonException>(() => FluxVaultIpcFrame.ReadAsync<FluxVaultIpcRequest>(
            source, 1024, 4, TimeSpan.FromSeconds(1), default));
        Assert.Contains("depth", exception.Message, StringComparison.OrdinalIgnoreCase);
        source.Position = 0;
        var request = await FluxVaultIpcFrame.ReadAsync<FluxVaultIpcRequest>(source, 1024, 32, TimeSpan.FromSeconds(1), default);
        Assert.Equal("*.docx", request.Configuration!.WatchedFolders[0].IncludePatterns[0]);
    }
}
