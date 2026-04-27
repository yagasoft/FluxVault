using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Tests;

public sealed class IpcSerializationTests
{
    [Fact]
    public void Request_serialization_preserves_command_and_payload()
    {
        var configuration = FluxVaultConfiguration.CreateDefault(@"C:\ProgramData\FluxVault");
        var request = FluxVaultIpcRequest.SaveConfiguration(configuration);

        var roundTrip = FluxVaultIpcSerializer.DeserializeRequest(FluxVaultIpcSerializer.SerializeRequest(request));

        Assert.Equal(FluxVaultIpcCommand.SaveConfiguration, roundTrip.Command);
        Assert.NotNull(roundTrip.Configuration);
        Assert.Equal(configuration.RepositoryPath, roundTrip.Configuration.RepositoryPath);
    }

    [Fact]
    public void Response_serialization_preserves_status_details()
    {
        var checkedAt = new DateTimeOffset(2026, 4, 27, 12, 1, 0, TimeSpan.Zero);
        var status = new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: FluxVaultConfiguration.CreateDefault(@"C:\ProgramData\FluxVault"),
            LastMessage: "Ready",
            LastCaptureUtc: new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero),
            WatchedFolders: [],
            RecentVersions: [],
            DurableChange: new DurableChangeRuntimeStatus(
                checkedAt,
                "USN active.",
                null,
                [new UsnJournalCheckpoint("docs", @"D:\", 42, 1000, 1, checkedAt)]));
        var response = FluxVaultIpcResponse.WithStatus(status);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.Status);
        Assert.Equal("Ready", roundTrip.Status.LastMessage);
        Assert.Equal(new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero), roundTrip.Status.LastCaptureUtc);
        Assert.NotNull(roundTrip.Status.DurableChange);
        Assert.Equal("USN active.", roundTrip.Status.DurableChange.Status);
        Assert.Equal((long)1000, Assert.Single(roundTrip.Status.DurableChange.Checkpoints).NextUsn);
    }

    [Fact]
    public void Retention_request_serialization_preserves_command()
    {
        var request = FluxVaultIpcRequest.RunRetentionNow();

        var roundTrip = FluxVaultIpcSerializer.DeserializeRequest(FluxVaultIpcSerializer.SerializeRequest(request));

        Assert.Equal(FluxVaultIpcCommand.RunRetentionNow, roundTrip.Command);
    }

    [Fact]
    public void Retention_response_serialization_preserves_result()
    {
        var result = new RepositoryRetentionResult(
            Decisions: [],
            KeptVersionCount: 42,
            PrunedVersionCount: 8,
            DeletedChunkCount: 5,
            ReclaimedBytes: 1024,
            RepositorySizeBytes: 4096,
            MirrorWarnings: ["mirror warning"]);
        var response = FluxVaultIpcResponse.WithRetentionResult(result);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.RetentionResult);
        Assert.Equal(8, roundTrip.RetentionResult.PrunedVersionCount);
        Assert.Equal(1024, roundTrip.RetentionResult.ReclaimedBytes);
        Assert.Equal("mirror warning", Assert.Single(roundTrip.RetentionResult.MirrorWarnings));
    }
}
