using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public static class CaptureCadenceScheduler
{
    public static CaptureCadenceDecision Evaluate(
        CaptureCadencePolicy policy,
        ResourceProfile profile,
        DateTimeOffset firstEventUtc,
        DateTimeOffset latestEventUtc,
        DateTimeOffset? lastCaptureAttemptUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (lastCaptureAttemptUtc is { } lastCapture
            && nowUtc - lastCapture < policy.MinimumSameFileCaptureInterval)
        {
            return new CaptureCadenceDecision(
                ShouldCapture: false,
                IsForcedHotFileSnapshot: false,
                NextForcedCaptureUtc: firstEventUtc + policy.GetMaxHotFileDelay(profile),
                DelayReason: "Minimum same-file capture interval has not elapsed.");
        }

        var forcedDeadline = firstEventUtc + policy.GetMaxHotFileDelay(profile);
        if (nowUtc >= forcedDeadline)
        {
            return new CaptureCadenceDecision(
                ShouldCapture: true,
                IsForcedHotFileSnapshot: true,
                NextForcedCaptureUtc: forcedDeadline,
                DelayReason: null);
        }

        if (nowUtc - latestEventUtc >= policy.GetDebounce(profile))
        {
            return new CaptureCadenceDecision(
                ShouldCapture: true,
                IsForcedHotFileSnapshot: false,
                NextForcedCaptureUtc: forcedDeadline,
                DelayReason: null);
        }

        return new CaptureCadenceDecision(
            ShouldCapture: false,
            IsForcedHotFileSnapshot: false,
            NextForcedCaptureUtc: forcedDeadline,
            DelayReason: "Waiting for quiet window.");
    }
}

public sealed record CaptureCadenceDecision(
    bool ShouldCapture,
    bool IsForcedHotFileSnapshot,
    DateTimeOffset NextForcedCaptureUtc,
    string? DelayReason);
