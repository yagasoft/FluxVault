namespace FluxVault.Abstractions.Ipc;

public enum RestoreSelectionDestinationMode
{
    Elsewhere = 0,
    Original = 1
}

public sealed record RestoreSelectionSummary(
    string SourcePath,
    bool IsDirectory,
    RestoreSelectionDestinationMode DestinationMode,
    string? DestinationPath,
    int FileCount,
    int ConflictCount,
    int RestoredCount,
    IReadOnlyList<string> FailedPaths);
