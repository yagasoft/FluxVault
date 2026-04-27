using FluxVault.Windows.Capture;

namespace FluxVault.Windows.Tests;

public sealed class CapturePlanningTests
{
    [Fact]
    public void Plan_uses_usn_as_truth_and_notifications_as_latency_hint()
    {
        var plan = CapturePipelinePlanner.CreateDefault();

        Assert.Equal(ChangeDetectionSource.UsnJournal, plan.DurableChangeSource);
        Assert.Contains(ChangeDetectionSource.DirectoryNotifications, plan.LatencyHints);
        Assert.Equal(OpenFileReadStrategy.VssSnapshotWhenNeeded, plan.OpenFileReadStrategy);
    }
}
