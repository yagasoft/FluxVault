using FluxVault.Abstractions.ChangeTracking;

namespace FluxVault.Windows.ChangeTracking;

public static class UsnJournalContinuity
{
    public static UsnJournalContinuityResult Evaluate(
        string watchedFolderId,
        UsnJournalState state,
        UsnJournalCheckpoint? checkpoint,
        uint reasonMask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchedFolderId);
        ArgumentNullException.ThrowIfNull(state);
        var nextCheckpoint = new UsnJournalCheckpoint(
            watchedFolderId,
            state.VolumeRoot,
            state.JournalId,
            state.NextUsn,
            reasonMask,
            DateTimeOffset.UtcNow);

        if (checkpoint is null)
        {
            return new UsnJournalContinuityResult(
                RequiresFullScan: true,
                StartUsn: state.NextUsn,
                Checkpoint: nextCheckpoint,
                FallbackReason: "USN checkpoint is missing; a full scan is required to seed the baseline.");
        }

        if (checkpoint.JournalId != state.JournalId)
        {
            return new UsnJournalContinuityResult(
                RequiresFullScan: true,
                StartUsn: state.NextUsn,
                Checkpoint: nextCheckpoint,
                FallbackReason: "USN journal ID changed; a full scan is required.");
        }

        if (checkpoint.NextUsn < state.FirstUsn)
        {
            return new UsnJournalContinuityResult(
                RequiresFullScan: true,
                StartUsn: state.NextUsn,
                Checkpoint: nextCheckpoint,
                FallbackReason: "USN journal wrapped before FluxVault could catch up; a full scan is required.");
        }

        return new UsnJournalContinuityResult(
            RequiresFullScan: false,
            StartUsn: checkpoint.NextUsn,
            Checkpoint: nextCheckpoint,
            FallbackReason: null);
    }
}

public sealed record UsnJournalContinuityResult(
    bool RequiresFullScan,
    long StartUsn,
    UsnJournalCheckpoint Checkpoint,
    string? FallbackReason);
