using System.Text.Json;
using FluxVault.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace FluxVault.Integration.Tests;

public sealed class DesktopObservationFixtureTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task Actual_App_startup_uses_safe_presentation_without_a_harness_override_and_joins_reads()
    {
        // Actual App startup performs its usual read-only status queries. No
        // configuration, backup or restore command is executed by this probe.
        using var fixture = new RepositoryProcessFixture();
        await using var process = fixture.Start("ui-normal-startup-observe");
        var result = await process.CompleteAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "ui-startup-observation.json")));
        var proof = output.RootElement;
        testOutput.WriteLine(proof.GetRawText());
        Assert.False(proof.GetProperty("SoftwareRendering").GetBoolean());
        Assert.Equal("SoftwareOnly", proof.GetProperty("EffectiveRenderMode").GetString());
        Assert.True(proof.GetProperty("Loaded").GetBoolean());
        Assert.True(proof.GetProperty("ContentRendered").GetBoolean());
        Assert.True(proof.GetProperty("ApplicationIdle").GetBoolean());
        Assert.True(proof.GetProperty("HasPresentationSource").GetBoolean());
        Assert.True(proof.GetProperty("HasCompositionTarget").GetBoolean());
        Assert.True(proof.GetProperty("RenderingEvents").GetInt32() > 0);
        Assert.True(proof.GetProperty("Root").GetProperty("ActualWidth").GetDouble() > 0);
        using var joined = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "ui-startup-joined.json")));
        Assert.True(joined.RootElement.GetProperty("Joined").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public async Task Actual_WPF_observation_fixture_loads_its_bounded_inventory_and_joins_after_failed_save(int count)
    {
        using var fixture = new RepositoryProcessFixture();
        await using var process = count == 0 ? fixture.Start("ui-draft-render")
            : fixture.Start("ui-draft-render", "--inventory-count", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await process.CompleteAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "ui-result.json")));
        var proof = output.RootElement;
        testOutput.WriteLine(proof.GetRawText());
        Assert.Equal(count, proof.GetProperty("InventoryCount").GetInt32());
        Assert.Equal(count, proof.GetProperty("LoadedFileCount").GetInt32());
        Assert.Equal("SoftwareOnly", proof.GetProperty("EffectiveRenderMode").GetString());
        Assert.True(proof.GetProperty("LocalAndServiceStatesVisible").GetBoolean());
        Assert.True(proof.GetProperty("StatusDoesNotOverlap").GetBoolean());
        Assert.Equal(1, proof.GetProperty("Saves").GetInt32());
        Assert.Equal(0, proof.GetProperty("Backups").GetInt32());
        if (count > 0)
        {
            Assert.InRange(proof.GetProperty("RealisedFileRows").GetInt32(), 1, count / 10);
            Assert.True(proof.GetProperty("LastFileRealised").GetBoolean());
            Assert.True(proof.GetProperty("RefreshMilliseconds").GetDouble() >= 0);
            Assert.True(proof.GetProperty("LastFileMilliseconds").GetDouble() >= 0);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("50001")]
    [InlineData("bad")]
    public async Task Actual_WPF_fixture_refuses_an_invalid_inventory_bound_before_creating_UI_state(string count)
    {
        using var fixture = new RepositoryProcessFixture();
        await using var process = fixture.Start("ui-draft-render", "--inventory-count", count);
        var result = await process.CompleteAsync();
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("inventory", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "ui-process.json")));
    }
}
