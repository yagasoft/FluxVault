using System.Runtime.Versioning;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Security;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class AutomaticStatusProjectionTests
{
    private const string Owner = "S-1-5-21-1-2-3-1001";

    [Theory]
    [InlineData(AutomaticProtectionState.Pending)]
    [InlineData(AutomaticProtectionState.Capturing)]
    [InlineData(AutomaticProtectionState.WaitingForChanges)]
    public void Acknowledged_pause_overrides_the_previous_runtime_cycle(AutomaticProtectionState previous)
    {
        var result = Project(Saved(paused: true), new(new(true), previous, "Saved changes are pending capture."));
        Assert.Equal(AutomaticProtectionState.Paused, result.State);
        Assert.Contains("paused", result.Reason);
        Assert.Equal(1, result.PendingCount);
    }

    [Fact]
    public void Disabled_preference_is_visible_before_the_runtime_observes_it()
    {
        var result = Project(Saved(enabled: false), new(new(true), AutomaticProtectionState.WaitingForChanges, "Waiting for saved changes."));
        Assert.Equal(AutomaticProtectionState.Disabled, result.State);
        Assert.Contains("off", result.Reason);
    }

    [Fact]
    public void Invalidated_creator_consent_cannot_report_pending_capture()
    {
        var result = Project(Saved(approval: true), new(new(true), AutomaticProtectionState.Pending, "Saved changes are pending capture."));
        Assert.Equal(AutomaticProtectionState.Blocked, result.State);
        Assert.Contains("Creator approval", result.Reason);
    }

    [Theory]
    [InlineData(AutomaticProtectionState.Paused)]
    [InlineData(AutomaticProtectionState.Disabled)]
    public void Resume_does_not_reuse_a_stale_stopped_state(AutomaticProtectionState previous)
    {
        var result = Project(Saved(), new(new(true), previous, "Stopped."));
        Assert.Equal(AutomaticProtectionState.Reconciling, result.State);
    }

    [Theory]
    [InlineData(AutomaticProtectionState.Failed)]
    [InlineData(AutomaticProtectionState.OutcomeUncertain)]
    public void Preference_changes_do_not_hide_failed_or_uncertain_results(AutomaticProtectionState previous)
    {
        var result = Project(Saved(enabled: false), new(new(true), previous, "Retained result needs investigation."));
        Assert.Equal(previous, result.State);
        Assert.Contains("Retained result needs investigation.", result.Reason);
    }

    [Fact]
    public void Live_blocked_state_and_durable_mirror_warning_are_retained_for_a_non_creator()
    {
        var saved = Saved() with { MirrorPendingCount = 2, MirrorReason = "Mirror is offline." };
        var result = WindowsSingleVaultService.ProjectAutomaticStatus(saved,
            new(new(true), AutomaticProtectionState.Blocked, "Source is unavailable."), "S-1-5-21-1-2-3-1002", 7);
        Assert.Equal(AutomaticProtectionState.Blocked, result.State);
        Assert.Contains("Source is unavailable.", result.Reason);
        Assert.Contains("Mirror is offline.", result.Reason);
        Assert.False(result.CanChangePreference);
    }

    [Theory]
    [InlineData(AutomaticProtectionState.Pending, AutomaticProtectionState.Reconciling, 6)]
    [InlineData(AutomaticProtectionState.Failed, AutomaticProtectionState.Failed, 6)]
    [InlineData(AutomaticProtectionState.OutcomeUncertain, AutomaticProtectionState.OutcomeUncertain, 6)]
    [InlineData(AutomaticProtectionState.Pending, AutomaticProtectionState.Reconciling, 8)]
    [InlineData(AutomaticProtectionState.Failed, AutomaticProtectionState.Failed, 8)]
    [InlineData(AutomaticProtectionState.OutcomeUncertain, AutomaticProtectionState.OutcomeUncertain, 8)]
    public void Different_catalogue_revisions_cannot_change_the_response_controls(
        AutomaticProtectionState previous, AutomaticProtectionState expected, long responseRevision)
    {
        var runtime = new AutomaticProtectionRuntimeStatus(new(true), previous, "Retained diagnostic.", PendingCount: 2);
        var saved = Saved(enabled: false);
        var savedBefore = saved;
        var result = WindowsSingleVaultService.ProjectAutomaticStatus(saved, runtime, Owner, responseRevision);
        Assert.Equal(expected, result.State);
        Assert.Equal(runtime.Policy, result.Policy);
        Assert.Equal(runtime.PendingCount, result.PendingCount);
        Assert.Contains("refresh to confirm", result.Reason);
        Assert.False(result.CanChangePreference);
        Assert.Equal(savedBefore, saved);
        Assert.Equal(7, saved.Vault.Revision);
        if (previous != AutomaticProtectionState.Pending) Assert.Contains(runtime.Reason, result.Reason);
    }

    private static AutomaticProtectionRuntimeStatus Project(AutomaticCatalogueState saved, AutomaticProtectionRuntimeStatus runtime)
        => WindowsSingleVaultService.ProjectAutomaticStatus(saved, runtime, Owner, 7);

    private static AutomaticCatalogueState Saved(bool paused = false, bool enabled = true, bool approval = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Tests", Guid.NewGuid().ToString("N"));
        var metadata = MetadataStoreConfiguration.CreateDefault(root);
        var binding = new VaultBinding(new(Guid.NewGuid()), Path.Combine(root, "repository"), Path.Combine(root, "state"), metadata);
        var configuration = FluxVaultConfiguration.CreateDefault(root) with
            { RepositoryPath = binding.RepositoryPath, MetadataStore = metadata, IsEnabled = !paused };
        return new(new(binding, 7, "FluxVault", new(Owner, []), configuration), new(enabled), approval, "scope", 1, 0, null, null);
    }
}
