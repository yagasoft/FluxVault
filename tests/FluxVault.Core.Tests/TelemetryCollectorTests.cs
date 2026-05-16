using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Diagnostics;

namespace FluxVault.Core.Tests;

public sealed class TelemetryCollectorTests
{
    [Fact]
    public void Sample_once_calculates_cpu_percent_from_processor_delta()
    {
        var timestamp = new DateTimeOffset(2026, 5, 16, 18, 0, 0, TimeSpan.Zero);
        var sampler = new ScriptedProcessResourceSampler(
            Reading(timestamp, TimeSpan.FromSeconds(10)),
            Reading(timestamp.AddSeconds(5), TimeSpan.FromSeconds(12)));
        var collector = new TelemetryCollector(TimeProvider.System, sampler, processorCount: 4);

        collector.SampleOnce(DiagnosticsPolicy.CreateDefault(@"C:\ProgramData\FluxVault"));
        collector.RecordIpcRequestStarted(FluxVaultIpcCommand.GetStatus);
        collector.RecordIpcRequestCompleted(FluxVaultIpcCommand.GetStatus, TimeSpan.FromMilliseconds(10), succeeded: true);
        collector.SampleOnce(DiagnosticsPolicy.CreateDefault(@"C:\ProgramData\FluxVault"));

        var status = collector.GetSnapshot(LogRuntimeStatus.Disabled(@"C:\ProgramData\FluxVault\logs"), []);

        Assert.Equal(10, status.Process.CpuPercent);
        Assert.Equal(4, status.Process.LogicalProcessorCount);
        Assert.Equal(0.4, status.Process.CpuCoreEquivalent);
        Assert.Equal(1, status.Ipc.TotalRequests);
        Assert.Equal("GetStatus", status.Ipc.LastCommand);
    }

    [Fact]
    public void Sample_history_is_capped_by_policy()
    {
        var timestamp = new DateTimeOffset(2026, 5, 16, 18, 0, 0, TimeSpan.Zero);
        var sampler = new ScriptedProcessResourceSampler(
            Reading(timestamp, TimeSpan.Zero),
            Reading(timestamp.AddSeconds(1), TimeSpan.FromSeconds(1)),
            Reading(timestamp.AddSeconds(2), TimeSpan.FromSeconds(2)),
            Reading(timestamp.AddSeconds(3), TimeSpan.FromSeconds(3)));
        var collector = new TelemetryCollector(TimeProvider.System, sampler, processorCount: 1);
        var policy = DiagnosticsPolicy.CreateDefault(@"C:\ProgramData\FluxVault") with
        {
            RetainedTelemetrySampleCount = 2
        };

        collector.SampleOnce(policy);
        collector.SampleOnce(policy);
        collector.SampleOnce(policy);
        collector.SampleOnce(policy);

        var samples = collector.GetSnapshot(LogRuntimeStatus.Disabled(@"C:\ProgramData\FluxVault\logs"), []).Samples;
        Assert.Equal(2, samples.Count);
        Assert.Equal(timestamp.AddSeconds(2), samples[0].TimestampUtc);
        Assert.Equal(timestamp.AddSeconds(3), samples[1].TimestampUtc);
    }

    [Fact]
    public void Loop_status_records_latest_state_and_iteration_count()
    {
        var sampler = new ScriptedProcessResourceSampler(Reading(DateTimeOffset.UtcNow, TimeSpan.Zero));
        var collector = new TelemetryCollector(TimeProvider.System, sampler, processorCount: 1);

        collector.RecordLoopState("Protection loop", "Running", "USN catch-up");
        collector.RecordLoopState("Protection loop", "Waiting", "Delay");

        var loop = Assert.Single(collector.GetSnapshot(LogRuntimeStatus.Disabled(@"C:\ProgramData\FluxVault\logs"), []).Loops);
        Assert.Equal("Protection loop", loop.Name);
        Assert.Equal("Waiting", loop.State);
        Assert.Equal("Delay", loop.Detail);
        Assert.Equal(2, loop.IterationCount);
    }

    private static ProcessResourceReading Reading(DateTimeOffset timestamp, TimeSpan processorTime)
    {
        return new ProcessResourceReading(
            TimestampUtc: timestamp,
            TotalProcessorTime: processorTime,
            WorkingSetBytes: 100,
            PrivateMemoryBytes: 200,
            GcHeapBytes: 300,
            Gen0Collections: 1,
            Gen1Collections: 2,
            Gen2Collections: 3,
            ThreadCount: 4,
            HandleCount: 5,
            ThreadPool: new ThreadPoolRuntimeStatus(10, 20, 30, 40, 0));
    }

    private sealed class ScriptedProcessResourceSampler(params ProcessResourceReading[] readings) : IProcessResourceSampler
    {
        private readonly Queue<ProcessResourceReading> remaining = new(readings);

        public ProcessResourceReading Read()
        {
            return remaining.Count > 1 ? remaining.Dequeue() : remaining.Peek();
        }
    }
}
