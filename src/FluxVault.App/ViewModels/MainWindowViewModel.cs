using CommunityToolkit.Mvvm.ComponentModel;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;

namespace FluxVault.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public string ServiceStatus { get; init; } = "Service connection: local shell scaffold";

    public string CaptureStrategy { get; init; } =
        "Directory notifications wake the service, USN journal catch-up is the durable source of truth, " +
        "VSS is used for open files, and chunking identifies changed content from a stable snapshot.";

    public string RepositorySummary { get; init; } =
        "The repository stores immutable BLAKE3-addressed chunks and append-only manifests. " +
        "Cloud-folder mirroring uses atomic file moves so sync clients see complete artefacts.";

    public IReadOnlyList<WatchedFolderRow> WatchedFolders { get; init; } = [];

    public IReadOnlyList<string> ResourceProfiles { get; init; } = [];

    public IReadOnlyList<VersionRow> RecentVersions { get; init; } = [];

    public string DiagnosticsText { get; init; } = string.Empty;

    public static MainWindowViewModel DesignTime()
    {
        return new MainWindowViewModel
        {
            WatchedFolders =
            [
                new WatchedFolderRow(@"D:\Work\Design", ResourceProfile.Fast, CompressionPreference.Zstd, "Watching policy ready"),
                new WatchedFolderRow(@"D:\Work\Documents", ResourceProfile.Balanced, CompressionPreference.Zstd, "Cloud-folder mirror ready"),
                new WatchedFolderRow(@"D:\Work\Media", ResourceProfile.Quiet, CompressionPreference.Off, "Compression skipped by policy")
            ],
            ResourceProfiles =
            [
                "Fast: prioritises critical files and accepts higher disk and CPU use.",
                "Balanced: default adaptive behaviour for normal workstation activity.",
                "Quiet: reduces background pressure when user disruption matters more than immediacy."
            ],
            RecentVersions =
            [
                new VersionRow(@"D:\Work\Documents\brief.docx", "2026-04-27 10:30 UTC", CaptureConsistency.CrashConsistent, 24),
                new VersionRow(@"D:\Work\Design\floor.dwg", "2026-04-27 10:25 UTC", CaptureConsistency.AppConsistent, 412)
            ],
            DiagnosticsText =
                "Diagnostics are local-only in v1.\r\n" +
                "No hidden telemetry is configured.\r\n" +
                "VSS consistency must be reported per capture.\r\n" +
                "Generic changed-byte detection is not claimed for arbitrary Windows files."
        };
    }
}

public sealed record WatchedFolderRow(
    string Path,
    ResourceProfile ResourceProfile,
    CompressionPreference Compression,
    string Status);

public sealed record VersionRow(
    string SourcePath,
    string CapturedAt,
    CaptureConsistency Consistency,
    int ChunkCount);
