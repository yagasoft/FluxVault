using FluxVault.Abstractions.Policies;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Tests;

public sealed class AutomaticCadenceTests
{
    [Fact]
    public void Persisted_due_time_for_a_hot_file_uses_the_original_event_and_survives_later_saves()
    {
        var policy = CaptureCadencePolicy.CreateDefault();
        var first = DateTimeOffset.UtcNow;
        Assert.Equal(first + policy.FastMaxHotFileDelay,
            CaptureCadenceScheduler.GetDueAtUtc(policy, ResourceProfile.Fast, first, first.AddSeconds(29), null));
    }

    [Fact]
    public void Minimum_same_file_interval_takes_precedence_over_hot_deadline()
    {
        var policy = CaptureCadencePolicy.CreateDefault();
        var first = DateTimeOffset.UtcNow;
        var lastAttempt = first.AddSeconds(28);
        var due = CaptureCadenceScheduler.GetDueAtUtc(policy, ResourceProfile.Fast, first, first.AddSeconds(29), lastAttempt);
        Assert.Equal(lastAttempt + policy.MinimumSameFileCaptureInterval, due);
        Assert.False(CaptureCadenceScheduler.Evaluate(policy, ResourceProfile.Fast, first, first.AddSeconds(29), lastAttempt, due.AddTicks(-1)).ShouldCapture);
        Assert.True(CaptureCadenceScheduler.Evaluate(policy, ResourceProfile.Fast, first, first.AddSeconds(29), lastAttempt, due).ShouldCapture);
    }
}
