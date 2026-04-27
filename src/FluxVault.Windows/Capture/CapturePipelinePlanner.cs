namespace FluxVault.Windows.Capture;

public static class CapturePipelinePlanner
{
    public static CapturePipelinePlan CreateDefault()
    {
        return new CapturePipelinePlan(
            DurableChangeSource: ChangeDetectionSource.UsnJournal,
            LatencyHints: [ChangeDetectionSource.DirectoryNotifications],
            OpenFileReadStrategy: OpenFileReadStrategy.VssSnapshotWhenNeeded);
    }
}
