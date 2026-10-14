using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public static class CaptureCadenceScheduler
{
    public static DateTimeOffset GetDueAtUtc(CaptureCadencePolicy policy, ResourceProfile profile,
        DateTimeOffset firstEventUtc, DateTimeOffset latestEventUtc, DateTimeOffset? lastCaptureAttemptUtc)
    {
        var quiet = latestEventUtc + policy.GetDebounce(profile);
        var hot = firstEventUtc + policy.GetMaxHotFileDelay(profile);
        var due = quiet < hot ? quiet : hot;
        return lastCaptureAttemptUtc is { } last && last + policy.MinimumSameFileCaptureInterval > due
            ? last + policy.MinimumSameFileCaptureInterval : due;
    }

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
