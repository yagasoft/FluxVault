namespace FluxVault.Abstractions.Policies;

public sealed record RetentionPolicy(
    bool IsEnabled,
    TimeSpan KeepAllFor,
    TimeSpan KeepHourlyFor,
    TimeSpan KeepDailyFor,
    int MinimumVersionsPerFile)
{
    public static RetentionPolicy CreateDefault()
    {
        return new RetentionPolicy(
            IsEnabled: true,
            KeepAllFor: TimeSpan.FromHours(24),
            KeepHourlyFor: TimeSpan.FromDays(30),
            KeepDailyFor: TimeSpan.FromDays(180),
            MinimumVersionsPerFile: 20);
    }
}
