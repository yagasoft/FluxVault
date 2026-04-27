using FluxVault.Abstractions.ChangeTracking;

namespace FluxVault.Core.ChangeTracking;

public sealed record UsnCatchUpResult(
    bool RequiresFullScan,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<UsnJournalCheckpoint> Checkpoints,
    DurableChangeRuntimeStatus Status);
