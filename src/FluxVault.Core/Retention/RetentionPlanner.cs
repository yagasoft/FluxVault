using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Retention;

public static class RetentionPlanner
{
    public static IReadOnlyList<RepositoryVersionRetentionDecision> Decide(
        IReadOnlyList<RepositoryVersionSummary> versions,
        RetentionPolicy policy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsEnabled)
        {
            return versions
                .Select(version => Decision(version, keep: true, "Retention disabled"))
                .ToArray();
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in versions.GroupBy(version => NormaliseSourcePath(version.SourcePath), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(version => version.CapturedAtUtc)
                .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
                .ToArray();

            KeepMinimumLatest(ordered, policy, kept, reasons);
            KeepDenseRecent(ordered, policy, nowUtc, kept, reasons);
            KeepHourly(ordered, policy, nowUtc, kept, reasons);
            KeepDaily(ordered, policy, nowUtc, kept, reasons);
        }

        return versions
            .OrderByDescending(version => version.CapturedAtUtc)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .Select(version =>
            {
                var keep = kept.Contains(version.VersionId);
                return Decision(version, keep, keep ? reasons[version.VersionId] : "Pruned by retention policy");
            })
            .ToArray();
    }

    private static void KeepMinimumLatest(
        IReadOnlyList<RepositoryVersionSummary> ordered,
        RetentionPolicy policy,
        ISet<string> kept,
        IDictionary<string, string> reasons)
    {
        var minimum = Math.Max(1, policy.MinimumVersionsPerFile);
        foreach (var version in ordered.Take(minimum))
        {
            Keep(version, "Minimum latest versions", kept, reasons);
        }
    }

    private static void KeepDenseRecent(
        IEnumerable<RepositoryVersionSummary> ordered,
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        ISet<string> kept,
        IDictionary<string, string> reasons)
    {
        foreach (var version in ordered.Where(version => nowUtc - version.CapturedAtUtc <= policy.KeepAllFor))
        {
            Keep(version, "Dense recent window", kept, reasons);
        }
    }

    private static void KeepHourly(
        IEnumerable<RepositoryVersionSummary> ordered,
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        ISet<string> kept,
        IDictionary<string, string> reasons)
    {
        foreach (var version in ordered
                     .Where(version =>
                     {
                         var age = nowUtc - version.CapturedAtUtc;
                         return age > policy.KeepAllFor && age <= policy.KeepHourlyFor;
                     })
                     .GroupBy(version => new DateTimeOffset(
                         version.CapturedAtUtc.Year,
                         version.CapturedAtUtc.Month,
                         version.CapturedAtUtc.Day,
                         version.CapturedAtUtc.Hour,
                         0,
                         0,
                         TimeSpan.Zero))
                     .Select(group => group.OrderByDescending(version => version.CapturedAtUtc).First()))
        {
            Keep(version, "Hourly retention window", kept, reasons);
        }
    }

    private static void KeepDaily(
        IEnumerable<RepositoryVersionSummary> ordered,
        RetentionPolicy policy,
        DateTimeOffset nowUtc,
        ISet<string> kept,
        IDictionary<string, string> reasons)
    {
        foreach (var version in ordered
                     .Where(version =>
                     {
                         var age = nowUtc - version.CapturedAtUtc;
                         return age > policy.KeepHourlyFor && age <= policy.KeepDailyFor;
                     })
                     .GroupBy(version => DateOnly.FromDateTime(version.CapturedAtUtc.UtcDateTime))
                     .Select(group => group.OrderByDescending(version => version.CapturedAtUtc).First()))
        {
            Keep(version, "Daily retention window", kept, reasons);
        }
    }

    private static void Keep(
        RepositoryVersionSummary version,
        string reason,
        ISet<string> kept,
        IDictionary<string, string> reasons)
    {
        if (kept.Add(version.VersionId))
        {
            reasons[version.VersionId] = reason;
        }
    }

    private static RepositoryVersionRetentionDecision Decision(
        RepositoryVersionSummary version,
        bool keep,
        string reason)
    {
        return new RepositoryVersionRetentionDecision(
            version.VersionId,
            version.SourcePath,
            version.CapturedAtUtc,
            keep,
            reason,
            version.LogicalLength,
            version.ChunkCount);
    }

    private static string NormaliseSourcePath(string sourcePath)
    {
        return Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
