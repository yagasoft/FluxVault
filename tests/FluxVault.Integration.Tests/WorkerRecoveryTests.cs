using FluxVault.Service;
using Microsoft.Extensions.Logging;

namespace FluxVault.Integration.Tests;

public sealed class WorkerRecoveryTests
{
    [Fact]
    public async Task Host_stop_deadline_does_not_release_a_still_running_request_lifetime()
    {
        var runtime = new HeldRuntime();
        using var worker = new Worker(new CapturingLogger<Worker>(), runtime);
        await worker.StartAsync(CancellationToken.None);
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var expiredStop = new CancellationTokenSource(); expiredStop.Cancel();
        var stop = worker.StopAsync(expiredStop.Token);
        try
        {
            await runtime.CancellationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted, "A live request must keep the worker/owned service lifetime alive after the host deadline.");
        }
        finally
        {
            runtime.Release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private sealed class HeldRuntime : IFluxVaultServiceRuntime
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => CancellationReceived.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
        }
    }
    [Fact]
    public async Task Worker_logs_and_rethrows_fatal_runtime_failure_for_service_recovery()
    {
        var logger = new CapturingLogger<Worker>();
        var failure = new InvalidOperationException("runtime failed");
        var worker = new Worker(
            logger,
            new FailingRuntime(failure));

        await worker.StartAsync(CancellationToken.None);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask ?? Task.CompletedTask);

        Assert.Same(failure, thrown);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Critical
                && entry.Message.Contains("authenticated transport failed", StringComparison.OrdinalIgnoreCase)
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
