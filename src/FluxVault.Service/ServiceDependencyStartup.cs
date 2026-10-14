using Npgsql;

namespace FluxVault.Service;

/// <summary>Internal service lifecycle policy, independent of protected-folder preferences.</summary>
public sealed record ServiceStartupPolicy
{
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}

public static class ServiceDependencyStartup
{
    public static async Task<T> OpenAsync<T>(Func<CancellationToken, Task<T>> open, Action<Exception> waiting,
        ServiceStartupPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.RetryDelay <= TimeSpan.Zero || policy.RetryDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(policy));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await open(cancellationToken).ConfigureAwait(false); }
            catch (NpgsqlException exception) when (!cancellationToken.IsCancellationRequested && IsTemporaryAvailabilityFailure(exception))
            {
                // Open must dispose its partial generation before this point. Never
                // retry a running owner or bypass the next full identity verification.
                waiting(exception);
                await Task.Delay(policy.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTemporaryAvailabilityFailure(NpgsqlException exception) => exception switch
    {
        // These server responses mean startup/recovery/shutdown or connection capacity.
        PostgresException postgres => postgres.SqlState is "57P01" or "57P02" or "57P03" or "53300",
        // Npgsql transport/timeouts are retryable. SQL authentication, schema,
        // permissions and integrity errors are handled by the stricter branch above.
        _ => exception.IsTransient
    };
}
