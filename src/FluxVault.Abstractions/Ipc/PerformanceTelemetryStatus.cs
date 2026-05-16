namespace FluxVault.Abstractions.Ipc;

public sealed record PerformanceTelemetryStatus(
    DateTimeOffset CollectedAtUtc,
    ProcessResourceRuntimeStatus Process,
    ThreadPoolRuntimeStatus ThreadPool,
    IpcRuntimeStatus Ipc,
    LogRuntimeStatus Logs,
    IReadOnlyList<ServiceLoopRuntimeStatus> Loops,
    IReadOnlyList<BackgroundWorkRuntimeStatus> BackgroundWork,
    IReadOnlyList<PerformanceTelemetrySample> Samples);

public sealed record ProcessResourceRuntimeStatus(
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long GcHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    int HandleCount,
    int LogicalProcessorCount = 1,
    double CpuCoreEquivalent = 0);

public sealed record ThreadPoolRuntimeStatus(
    int AvailableWorkerThreads,
    int MaxWorkerThreads,
    int AvailableCompletionPortThreads,
    int MaxCompletionPortThreads,
    long PendingWorkItemCount);

public sealed record IpcRuntimeStatus(
    long TotalRequests,
    long ActiveRequests,
    long FailedRequests,
    string LastCommand,
    DateTimeOffset? LastRequestUtc);

public sealed record LogRuntimeStatus(
    bool IsFileLoggingEnabled,
    string LogDirectory,
    string EffectiveLevel,
    string? CurrentFilePath,
    long CurrentFileBytes,
    int RetainedFileCount,
    long DroppedMessageCount)
{
    public static LogRuntimeStatus Disabled(string logDirectory)
    {
        return new LogRuntimeStatus(
            IsFileLoggingEnabled: false,
            LogDirectory: logDirectory,
            EffectiveLevel: "None",
            CurrentFilePath: null,
            CurrentFileBytes: 0,
            RetainedFileCount: 0,
            DroppedMessageCount: 0);
    }
}

public sealed record ServiceLoopRuntimeStatus(
    string Name,
    string State,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    string Detail,
    long IterationCount);

public sealed record BackgroundWorkRuntimeStatus(
    string Name,
    string State,
    int ActiveCount,
    int PendingCount,
    string Detail);

public sealed record PerformanceTelemetrySample(
    DateTimeOffset TimestampUtc,
    double CpuPercent,
    long WorkingSetBytes,
    long GcHeapBytes,
    int ThreadCount,
    int HandleCount,
    int ActiveCaptureWorkers,
    int WatcherBacklogCount,
    long IpcTotalRequests,
    long DroppedLogMessages);
