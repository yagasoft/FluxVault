using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.App.Tests;

public sealed partial class ProtectionSaveContractTests
{
    [Theory]
    [InlineData(AutomaticProtectionState.WaitingForChanges, "Waiting for saved changes.","Waiting for changes")]
    [InlineData(AutomaticProtectionState.Blocked, "Creator session unavailable.","Blocked")]
    [InlineData(AutomaticProtectionState.Failed, "Database unavailable; pending work retained.","Failed")]
    [InlineData(AutomaticProtectionState.OutcomeUncertain, "Interrupted publication needs reconciliation.","Outcome uncertain")]
    [InlineData(AutomaticProtectionState.Pending, "Three saved changes are pending.","Pending")]
    public async Task Dashboard_shows_the_actual_automatic_state_and_reason(AutomaticProtectionState state, string reason,string label)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var client = new StoreClient(fixture.Store) { AutomaticProtection = new(new(true), state, reason) };
        var model = CreateViewModel(client);
        await model.RefreshAsync();
        Assert.Contains(label, model.ProtectionControlStatus);
        Assert.Contains(reason, model.ProtectionControlStatus);
        Assert.DoesNotContain("Automatic capture is not available", model.ProtectionControlStatus);
        Assert.Contains("while the creator is signed in", model.CaptureStrategy);
    }
}
