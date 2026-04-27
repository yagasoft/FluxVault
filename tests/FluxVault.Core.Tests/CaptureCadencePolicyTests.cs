using FluxVault.Abstractions.Policies;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Tests;

public sealed class CaptureCadencePolicyTests
{
    [Fact]
    public void Defaults_match_mvp_control_bundle_timing()
    {
        var policy = CaptureCadencePolicy.CreateDefault();

        Assert.Equal(TimeSpan.FromSeconds(5), policy.WatcherPollInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.PeriodicReconciliationInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDebounce(ResourceProfile.Fast));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.GetDebounce(ResourceProfile.Balanced));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetDebounce(ResourceProfile.Quiet));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetMaxHotFileDelay(ResourceProfile.Fast));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.GetMaxHotFileDelay(ResourceProfile.Balanced));
        Assert.Equal(TimeSpan.FromMinutes(10), policy.GetMaxHotFileDelay(ResourceProfile.Quiet));
        Assert.Equal(TimeSpan.FromSeconds(15), policy.MinimumSameFileCaptureInterval);
        Assert.Equal(1, policy.MaximumConcurrentCaptures);
    }

    [Fact]
    public void Hot_file_is_forced_when_quiet_window_never_arrives()
    {
        var policy = CaptureCadencePolicy.CreateDefault();
        var firstEvent = new DateTimeOffset(2026, 4, 27, 10, 0, 0, TimeSpan.Zero);
        var latestEvent = firstEvent.AddSeconds(100);
        var now = firstEvent.AddMinutes(2);

        var decision = CaptureCadenceScheduler.Evaluate(
            policy,
            ResourceProfile.Balanced,
            firstEvent,
            latestEvent,
            lastCaptureAttemptUtc: null,
            now);

        Assert.True(decision.ShouldCapture);
        Assert.True(decision.IsForcedHotFileSnapshot);
        Assert.Equal(firstEvent.AddMinutes(2), decision.NextForcedCaptureUtc);
    }

    [Fact]
    public void Recent_same_file_capture_prevents_thrash_before_minimum_interval()
    {
        var policy = CaptureCadencePolicy.CreateDefault();
        var firstEvent = new DateTimeOffset(2026, 4, 27, 10, 0, 0, TimeSpan.Zero);
        var latestEvent = firstEvent.AddSeconds(20);
        var lastCapture = firstEvent.AddSeconds(19);
        var now = firstEvent.AddSeconds(22);

        var decision = CaptureCadenceScheduler.Evaluate(
            policy,
            ResourceProfile.Fast,
            firstEvent,
            latestEvent,
            lastCapture,
            now);

        Assert.False(decision.ShouldCapture);
        Assert.Equal("Minimum same-file capture interval has not elapsed.", decision.DelayReason);
    }
}
