using System.Runtime.Versioning;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Security;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Windows.Capture;
using Npgsql;

namespace FluxVault.Windows.Security;

/// <summary>Service-lifetime executor. Cached state and the SSPI pool never retain callers.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAuthorisedVaultCommandExecutor : IAuthorisedVaultCommandExecutor, IAsyncDisposable
{
    private readonly VaultCatalogueEndpoint endpoint;
    private readonly WindowsVaultStorageGuard storage = new();
    private readonly NpgsqlDataSource source;
    private readonly Runtime runtime;
    private readonly Lock lifetime = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopping;
    private int active;
    private Task? disposal;

    public WindowsAuthorisedVaultCommandExecutor(VaultCatalogueEndpoint endpoint, VaultBinding binding)
    {
        // Reuse the protected endpoint's validation and finite pool/timeout defaults.
        ArgumentNullException.ThrowIfNull(endpoint); endpoint.Validate();
        this.endpoint = endpoint;
        ArgumentNullException.ThrowIfNull(binding); binding.Validate();
        if (binding.MetadataStore.Host != endpoint.Host || binding.MetadataStore.Port != endpoint.Port ||
            binding.MetadataStore.DatabaseName != endpoint.Database || binding.MetadataStore.Username != endpoint.ServiceRole)
            throw new ArgumentException("The installed repository must use its protected metadata endpoint.", nameof(binding));
        runtime = new(binding);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host, Port = endpoint.Port, Database = endpoint.Database, Username = endpoint.ServiceRole,
            RequireAuth = "SSPI", SearchPath = "pg_catalog", Pooling = true, MinPoolSize = 0,
            MaxPoolSize = endpoint.MaximumConnections, Timeout = endpoint.ConnectionTimeoutSeconds,
            CommandTimeout = endpoint.CommandTimeoutSeconds, IncludeErrorDetail = false
        };
        var factory = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        factory.UsePasswordProvider(_ => throw new NotSupportedException("Vault execution requires SSPI."),
            (_, _) => ValueTask.FromException<string>(new NotSupportedException("Vault execution requires SSPI.")));
        source = factory.Build();
    }

    public bool CanExecute(FluxVaultIpcRequest request) => request.Command switch
    {
        FluxVaultIpcCommand.SaveConfiguration => true,
        FluxVaultIpcCommand.SetVaultAccess or
        FluxVaultIpcCommand.GetStatus or FluxVaultIpcCommand.GetPerformance or FluxVaultIpcCommand.GetActivity or
        FluxVaultIpcCommand.ListBlockedFiles or FluxVaultIpcCommand.ListVersions or FluxVaultIpcCommand.InspectVersion or
        FluxVaultIpcCommand.RunBackupNow or FluxVaultIpcCommand.RestoreVersion or FluxVaultIpcCommand.RestoreVersionPreview => true,
        _ => false
    };

    public async Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission,
        FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        lock (lifetime) { ObjectDisposedException.ThrowIf(stopping, this); active++; }
        try
        {
            var vault = admission.Vault;
            if (vault.Binding != runtime.Binding || request.VaultId != runtime.Binding.Id || admission.IsReplay ||
                !VaultCommandPolicy.TryGet(request, vault.Configuration, out var policy) ||
                !VaultAuthorizer.IsAllowed(caller, vault.Access, policy.Permissions) ||
                PostgreSqlVaultCatalogue.IsMutation(request.Command) &&
                (admission.Receipt is not { State: VaultOperationState.Admitted } receipt || receipt.VaultId != vault.Binding.Id ||
                    receipt.ActorSid != caller.UserSid || receipt.Command != request.Command || receipt.OperationId != request.OperationId ||
                    receipt.Revision != vault.Revision ||
                    (receipt.RequiredPermissions & policy.Permissions) != policy.Permissions))
                return Failure(FluxVaultIpcErrorCode.Denied, "The vault is unavailable or you do not have permission for this command.");
            if (request.Command == FluxVaultIpcCommand.SaveConfiguration && request.PurgeRemovedSelections &&
                request.ExpectedVaultRevision != vault.Revision - 1)
                return Failure(FluxVaultIpcErrorCode.Denied, "The committed save revision could not be verified.");
            if (!CanExecute(request) || request.Command == FluxVaultIpcCommand.SetVaultAccess ||
                request.Command == FluxVaultIpcCommand.SaveConfiguration && !request.PurgeRemovedSelections)
                return Failure(FluxVaultIpcErrorCode.Unavailable, "This command requires its catalogue or provisioning flow.");
            if (request.Command is FluxVaultIpcCommand.InspectVersion or FluxVaultIpcCommand.RestoreVersion or FluxVaultIpcCommand.RestoreVersionPreview && string.IsNullOrWhiteSpace(request.VersionId) ||
                request.Command is FluxVaultIpcCommand.RestoreVersion or FluxVaultIpcCommand.RestoreVersionPreview && string.IsNullOrWhiteSpace(request.OutputPath))
                return Failure(FluxVaultIpcErrorCode.InvalidRequest, "A version and recovery destination are required.");
            var binding = vault.Binding; var configuration = vault.Configuration; var metadata = binding.MetadataStore;
            if (metadata.Host != endpoint.Host || metadata.Port != endpoint.Port || metadata.DatabaseName != endpoint.Database || metadata.Username != endpoint.ServiceRole ||
                configuration.RepositoryPath != binding.RepositoryPath || configuration.MetadataStore != metadata)
                return Failure(FluxVaultIpcErrorCode.Unavailable, "The protected vault binding could not be verified.");
            if (request.Command == FluxVaultIpcCommand.SaveConfiguration)
                return await ApplyCommittedSaveAsync(caller, vault, request, cancellationToken);
            IDisposable pins;
            try { pins = storage.Open(vault, cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { return Failure(FluxVaultIpcErrorCode.Unavailable, "Protected vault storage is unavailable. No repository command was started."); }
            using (pins)
            {
                await using var store = new PostgreSqlRepositoryMetadataStore(binding, configuration.Sync.LocalDevice.DeviceId, sharedWindowsDataSource: source);
                var repository = new FileSystemChunkRepository(binding, new FastCdcChunker(new()), new Blake3ContentHasher(), new ZstdChunkCodec(), configuration.MirrorSet, store);
                var operations = new FluxVaultOperations(new AdmittedConfiguration(configuration),
                    new WindowsCallerCaptureProvider(caller, configuration.WatchedFolders.Where(folder => folder.IsEnabled).Select(folder => folder.Path).ToArray()),
                    new FileRepositoryMaintenanceStateStore(Path.Combine(binding.StateRoot, "repository-maintenance.json")), binding.StateRoot,
                    _ => store, _ => repository, sourceAccess: new WindowsProtectionSourceAccess(caller), runtimeState: runtime.State);
                switch (request.Command)
                {
                    case FluxVaultIpcCommand.GetStatus: return FluxVaultIpcResponse.WithStatus(await operations.GetStatusAsync(request.StatusDetailLevel, cancellationToken));
                    case FluxVaultIpcCommand.GetPerformance: return FluxVaultIpcResponse.WithPerformance(await operations.GetPerformanceAsync(cancellationToken));
                    case FluxVaultIpcCommand.GetActivity: return FluxVaultIpcResponse.WithActivity(operations.GetActivity());
                    case FluxVaultIpcCommand.ListBlockedFiles: return FluxVaultIpcResponse.WithBlockedFiles(operations.ListBlockedFiles());
                    case FluxVaultIpcCommand.ListVersions: return await operations.HandleAsync(request, cancellationToken);
                    case FluxVaultIpcCommand.InspectVersion: return FluxVaultIpcResponse.WithInspection(await operations.InspectVersionAsync(request.VersionId!, cancellationToken));
                    case FluxVaultIpcCommand.RunBackupNow:
                        var backup = await operations.RunBackupNowAsync(cancellationToken);
                        return FluxVaultIpcResponse.WithBackup(backup) with { Success = backup.Success,
                            ErrorCode = backup.Success ? null : FluxVaultIpcErrorCode.Unavailable, ErrorMessage = backup.Success ? null : backup.Message };
                    case FluxVaultIpcCommand.RestoreVersion:
                        await using (var target = await WindowsCallerRecoveryTarget.CreateAsync(caller, request.OutputPath!, cancellationToken))
                            return FluxVaultIpcResponse.WithRestore(await operations.RestoreVersionAsync(request.VersionId!, target, cancellationToken));
                    case FluxVaultIpcCommand.RestoreVersionPreview:
                        try
                        {
                            await using var target = await WindowsCallerRecoveryTarget.CreateNewAsync(caller, request.OutputPath!, cancellationToken);
                            return FluxVaultIpcResponse.WithRestore(await repository.RestorePreviewAsync(request.VersionId!, target, cancellationToken));
                        }
                        catch (InvalidDataException)
                        { return Failure(FluxVaultIpcErrorCode.InvalidRequest, "Only a valid file version can be opened as a preview."); }
                    default: return Failure(FluxVaultIpcErrorCode.Unavailable, "This command is not available in this build.");
                }
            }
        }
        finally { lock (lifetime) { active--; if (stopping && active == 0) drained.TrySetResult(); } }
    }

    private async Task<FluxVaultIpcResponse> ApplyCommittedSaveAsync(FluxVaultCallerContext caller, VaultCatalogueEntry vault,
        FluxVaultIpcRequest request, CancellationToken cancellationToken)
    {
        // Catalogue CAS has already saved this exact revision and admitted the purge.
        // Open storage inside the repository factory so post-commit failures retain
        // the successful save, update runtime state and report a failed purge.
        var configuration = vault.Configuration;
        IDisposable? pins = null;
        await using var store = new PostgreSqlRepositoryMetadataStore(vault.Binding, configuration.Sync.LocalDevice.DeviceId, sharedWindowsDataSource: source);
        try
        {
            var operations = new FluxVaultOperations(new AdmittedConfiguration(configuration),
                new WindowsCallerCaptureProvider(caller, []),
                runtimeState: runtime.State, metadataStoreFactory: _ => store, repositoryFactory: _ =>
                {
                    try { pins = storage.Open(vault, cancellationToken); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    { throw new IOException("Protected vault storage is unavailable. Purge was not started."); }
                    return new FileSystemChunkRepository(vault.Binding, new FastCdcChunker(new()), new Blake3ContentHasher(),
                        new ZstdChunkCodec(), configuration.MirrorSet, store);
                });
            var purge = await operations.ApplyCommittedConfigurationAsync(configuration, true,
                request.RemovedSelections!, request.PreservedSelections!, cancellationToken);
            return FluxVaultIpcResponse.WithPurge(purge);
        }
        finally { pins?.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            stopping = true; if (active == 0) drained.TrySetResult();
            return new(disposal ??= DisposeSourceAsync());
        }
    }
    private async Task DisposeSourceAsync() { await drained.Task; await source.DisposeAsync(); }
    private static FluxVaultIpcResponse Failure(FluxVaultIpcErrorCode code, string message) => FluxVaultIpcResponse.Failure(message) with { ErrorCode = code };
    private sealed record Runtime(VaultBinding Binding) { internal FluxVaultOperationsRuntimeState State { get; } = new(); }
    private sealed class AdmittedConfiguration(FluxVaultConfiguration configuration) : IFluxVaultConfigurationStore
    {
        public Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(configuration); }
        public Task SaveAsync(FluxVaultConfiguration value, CancellationToken cancellationToken = default) => throw new NotSupportedException("Configuration changes require catalogue CAS.");
    }
}
