using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Retention;

namespace FluxVault.Core.Tests;

public sealed class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly RetentionPolicy Policy = RetentionPolicy.CreateDefault();

    [Fact]
    public void Keeps_all_versions_inside_dense_recent_window()
    {
        var versions = Enumerable.Range(0, 30)
            .Select(index => Version("v" + index, @"D:\work\a.txt", Now.AddMinutes(-index * 30)))
            .ToArray();

        var decisions = RetentionPlanner.Decide(versions, Policy, Now);

        Assert.All(decisions, decision => Assert.True(decision.Keep));
    }

    [Fact]
    public void Keeps_one_version_per_hour_in_hourly_window()
    {
        var versions = Enumerable.Range(0, 3)
            .SelectMany(hour => Enumerable.Range(0, 3)
                .Select(slot => Version($"h{hour}-{slot}", @"D:\work\a.txt", Now.AddDays(-2).AddHours(-hour).AddMinutes(slot))))
            .ToArray();

        var decisions = RetentionPlanner.Decide(versions, Policy with { MinimumVersionsPerFile = 1 }, Now);

        Assert.Equal(3, decisions.Count(decision => decision.Keep));
        Assert.Contains(decisions.Where(decision => decision.Keep), decision => decision.Reason.Contains("hour", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Keeps_one_version_per_day_in_daily_window()
    {
        var versions = Enumerable.Range(0, 3)
            .SelectMany(day => Enumerable.Range(0, 3)
                .Select(slot => Version($"d{day}-{slot}", @"D:\work\a.txt", Now.AddDays(-40 - day).AddHours(-slot))))
            .ToArray();

        var decisions = RetentionPlanner.Decide(versions, Policy with { MinimumVersionsPerFile = 1 }, Now);

        Assert.Equal(3, decisions.Count(decision => decision.Keep));
        Assert.Contains(decisions.Where(decision => decision.Keep), decision => decision.Reason.Contains("daily", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Keeps_minimum_latest_versions_per_source_file()
    {
        var versions = Enumerable.Range(0, 25)
            .Select(index => Version("v" + index, @"D:\work\a.txt", Now.AddDays(-400 - index)))
            .ToArray();

        var decisions = RetentionPlanner.Decide(versions, Policy with { MinimumVersionsPerFile = 20 }, Now);

        Assert.Equal(20, decisions.Count(decision => decision.Keep));
        Assert.True(decisions.Single(decision => decision.VersionId == "v0").Keep);
    }

    [Fact]
    public void Keeps_latest_version_for_every_source_file()
    {
        var versions = new[]
        {
            Version("a-new", @"D:\work\a.txt", Now.AddDays(-400)),
            Version("a-old", @"D:\work\a.txt", Now.AddDays(-401)),
            Version("b-new", @"D:\work\b.txt", Now.AddDays(-400)),
            Version("b-old", @"D:\work\b.txt", Now.AddDays(-401))
        };

        var decisions = RetentionPlanner.Decide(versions, Policy with { MinimumVersionsPerFile = 0 }, Now);

        Assert.True(decisions.Single(decision => decision.VersionId == "a-new").Keep);
        Assert.True(decisions.Single(decision => decision.VersionId == "b-new").Keep);
    }

    private static RepositoryVersionSummary Version(string id, string sourcePath, DateTimeOffset capturedAtUtc)
    {
        return new RepositoryVersionSummary(
            id,
            sourcePath,
            capturedAtUtc,
            CaptureConsistency.BestEffort,
            LogicalLength: 100,
            ChunkCount: 1);
    }
}
