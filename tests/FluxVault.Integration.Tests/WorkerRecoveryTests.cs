using FluxVault.Service;
using FluxVault.Windows.Capture;
using Microsoft.Extensions.Logging;

namespace FluxVault.Integration.Tests;

public sealed class WorkerRecoveryTests
{
    [Fact]
    public async Task Worker_logs_and_rethrows_fatal_runtime_failure_for_service_recovery()
    {
        var logger = new CapturingLogger<Worker>();
        var failure = new InvalidOperationException("runtime failed");
        var worker = new Worker(
            logger,
            new CapturePipelinePlan(
                ChangeDetectionSource.UsnJournal,
                [ChangeDetectionSource.DirectoryNotifications],
                OpenFileReadStrategy.VssSnapshotWhenNeeded),
            new FailingRuntime(failure));

        await worker.StartAsync(CancellationToken.None);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask ?? Task.CompletedTask);

        Assert.Same(failure, thrown);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Critical
                && entry.Message.Contains("internal background task failed", StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(entry.Exception, failure));
    }

    private sealed class FailingRuntime(Exception failure) : IFluxVaultServiceRuntime
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            return Task.FromException(failure);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
