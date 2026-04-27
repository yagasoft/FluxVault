namespace FluxVault.Windows.Capture;

public sealed record CapturePipelinePlan(
    ChangeDetectionSource DurableChangeSource,
    IReadOnlyList<ChangeDetectionSource> LatencyHints,
    OpenFileReadStrategy OpenFileReadStrategy);

public enum ChangeDetectionSource
{
    DirectoryNotifications = 0,
    UsnJournal = 1
}

public enum OpenFileReadStrategy
{
    DirectReadWhenStable = 0,
    VssSnapshotWhenNeeded = 1
}
