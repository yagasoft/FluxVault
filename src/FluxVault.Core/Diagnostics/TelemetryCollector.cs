using System.Diagnostics;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Diagnostics;

public interface IProcessResourceSampler
{
    ProcessResourceReading Read();
}

public sealed record ProcessResourceReading(
    DateTimeOffset TimestampUtc,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long GcHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    int HandleCount,
    ThreadPoolRuntimeStatus ThreadPool);

public sealed class TelemetryCollector(
    TimeProvider timeProvider,
    IProcessResourceSampler? sampler = null,
    int? processorCount = null)
{
    private readonly Lock gate = new();
    private readonly IProcessResourceSampler sampler = sampler ?? new CurrentProcessResourceSampler(timeProvider);
    private readonly int processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    private readonly Queue<PerformanceTelemetrySample> samples = new();
    private readonly Dictionary<string, MutableLoopStatus> loops = new(StringComparer.OrdinalIgnoreCase);
    private ProcessResourceReading? previousReading;
    private ProcessResourceRuntimeStatus process = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private ThreadPoolRuntimeStatus threadPool = new(0, 0, 0, 0, 0);
    private long totalIpcRequests;
    private long activeIpcRequests;
    private long failedIpcRequests;
    private string lastIpcCommand = "None";
    private DateTimeOffset? lastIpcRequestUtc;

    public void SampleOnce(
        DiagnosticsPolicy policy,
        int activeCaptureWorkers = 0,
        int watcherBacklogCount = 0,
        long droppedLogMessages = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var reading = sampler.Read();
        lock (gate)
        {
            var cpuPercent = CalculateCpuPercent(previousReading, reading);
            var processStatus = new ProcessResourceRuntimeStatus(
                Math.Round(cpuPercent, 1),
                reading.WorkingSetBytes,
                reading.PrivateMemoryBytes,
                reading.GcHeapBytes,
                reading.Gen0Collections,
                reading.Gen1Collections,
                reading.Gen2Collections,
                reading.ThreadCount,
                reading.HandleCount);
            var sample = new PerformanceTelemetrySample(
                reading.TimestampUtc,
                processStatus.CpuPercent,
                reading.WorkingSetBytes,
                reading.GcHeapBytes,
                reading.ThreadCount,
                reading.HandleCount,
                activeCaptureWorkers,
                watcherBacklogCount,
                totalIpcRequests,
                droppedLogMessages);
            previousReading = reading;
            process = processStatus;
            threadPool = reading.ThreadPool;
            samples.Enqueue(sample);
            while (samples.Count > Math.Max(1, policy.RetainedTelemetrySampleCount))
            {
                samples.Dequeue();
            }
        }
    }

    public void RecordLoopState(string name, string state, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (!loops.TryGetValue(name, out var status))
            {
                status = new MutableLoopStatus(name);
                loops[name] = status;
            }

            status.State = state;
            status.Detail = detail;
            status.IterationCount++;
            if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
            {
                status.LastStartedUtc = now;
            }
            else
            {
                status.LastCompletedUtc = now;
            }
        }
    }

    public void RecordIpcRequestStarted(FluxVaultIpcCommand command)
    {
        lock (gate)
        {
            totalIpcRequests++;
            activeIpcRequests++;
            lastIpcCommand = command.ToString();
            lastIpcRequestUtc = timeProvider.GetUtcNow();
        }
    }

    public void RecordIpcRequestCompleted(FluxVaultIpcCommand command, TimeSpan elapsed, bool succeeded)
    {
        lock (gate)
        {
            activeIpcRequests = Math.Max(0, activeIpcRequests - 1);
            lastIpcCommand = command.ToString();
            if (!succeeded)
            {
                failedIpcRequests++;
            }
        }
    }

    public PerformanceTelemetryStatus GetSnapshot(
        LogRuntimeStatus logStatus,
        IReadOnlyList<BackgroundWorkRuntimeStatus> backgroundWork)
    {
        ArgumentNullException.ThrowIfNull(logStatus);
        lock (gate)
        {
            return new PerformanceTelemetryStatus(
                timeProvider.GetUtcNow(),
                process,
                threadPool,
                new IpcRuntimeStatus(
                    totalIpcRequests,
                    activeIpcRequests,
                    failedIpcRequests,
                    lastIpcCommand,
                    lastIpcRequestUtc),
                logStatus,
                loops.Values
                    .Select(loop => new ServiceLoopRuntimeStatus(
                        loop.Name,
                        loop.State,
                        loop.LastStartedUtc,
                        loop.LastCompletedUtc,
                        loop.Detail,
                        loop.IterationCount))
                    .OrderBy(loop => loop.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                backgroundWork,
                samples.ToArray());
        }
    }

    private double CalculateCpuPercent(ProcessResourceReading? previous, ProcessResourceReading current)
    {
        if (previous is null)
        {
            return 0;
        }

        var elapsed = current.TimestampUtc - previous.TimestampUtc;
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var processorDelta = current.TotalProcessorTime - previous.TotalProcessorTime;
        var percent = processorDelta.TotalMilliseconds / elapsed.TotalMilliseconds / processorCount * 100;
        return Math.Clamp(percent, 0, 100);
    }

    private sealed class MutableLoopStatus(string name)
    {
        public string Name { get; } = name;

        public string State { get; set; } = "Unknown";

        public DateTimeOffset? LastStartedUtc { get; set; }

        public DateTimeOffset? LastCompletedUtc { get; set; }

        public string Detail { get; set; } = string.Empty;

        public long IterationCount { get; set; }
    }
}

public sealed class CurrentProcessResourceSampler(TimeProvider timeProvider) : IProcessResourceSampler
{
    public ProcessResourceReading Read()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availablePorts);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxPorts);
        return new ProcessResourceReading(
            timeProvider.GetUtcNow(),
            process.TotalProcessorTime,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            process.Threads.Count,
            process.HandleCount,
            new ThreadPoolRuntimeStatus(
                availableWorkers,
                maxWorkers,
                availablePorts,
                maxPorts,
                ThreadPool.PendingWorkItemCount));
    }
}
