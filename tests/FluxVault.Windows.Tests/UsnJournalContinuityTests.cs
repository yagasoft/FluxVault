using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Windows.ChangeTracking;

namespace FluxVault.Windows.Tests;

public sealed class UsnJournalContinuityTests
{
    [Fact]
    public void Missing_checkpoint_requires_full_scan_to_seed_baseline()
    {
        var state = new UsnJournalState(@"D:\", 42, 100, 900);

        var result = UsnJournalContinuity.Evaluate("docs", state, checkpoint: null, reasonMask: 1);

        Assert.True(result.RequiresFullScan);
        Assert.Equal("docs", result.Checkpoint.WatchedFolderId);
        Assert.Equal((long)900, result.Checkpoint.NextUsn);
    }

    [Fact]
    public void Journal_id_change_requires_full_scan()
    {
        var state = new UsnJournalState(@"D:\", 43, 100, 900);
        var checkpoint = new UsnJournalCheckpoint("docs", @"D:\", 42, 500, 1, DateTimeOffset.UtcNow);

        var result = UsnJournalContinuity.Evaluate("docs", state, checkpoint, reasonMask: 1);

        Assert.True(result.RequiresFullScan);
        Assert.Contains("changed", result.FallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((ulong)43, result.Checkpoint.JournalId);
    }

    [Fact]
    public void Checkpoint_before_first_usn_requires_full_scan()
    {
        var state = new UsnJournalState(@"D:\", 42, 600, 900);
        var checkpoint = new UsnJournalCheckpoint("docs", @"D:\", 42, 500, 1, DateTimeOffset.UtcNow);

        var result = UsnJournalContinuity.Evaluate("docs", state, checkpoint, reasonMask: 1);

        Assert.True(result.RequiresFullScan);
        Assert.Contains("wrapped", result.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valid_checkpoint_can_read_incrementally()
    {
        var state = new UsnJournalState(@"D:\", 42, 100, 900);
        var checkpoint = new UsnJournalCheckpoint("docs", @"D:\", 42, 500, 1, DateTimeOffset.UtcNow);

        var result = UsnJournalContinuity.Evaluate("docs", state, checkpoint, reasonMask: 1);

        Assert.False(result.RequiresFullScan);
        Assert.Equal((long)500, result.StartUsn);
        Assert.Equal((long)900, result.Checkpoint.NextUsn);
    }
}
