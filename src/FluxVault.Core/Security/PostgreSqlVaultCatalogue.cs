using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace FluxVault.Core.Security;

/// <summary>Protected service bootstrap, independent of user-editable vault configuration.</summary>
public sealed record VaultCatalogueEndpoint(Guid InstanceId, string Host, int Port, string Database, string ServiceRole,
    int ConnectionTimeoutSeconds = 5, int CommandTimeoutSeconds = 15, int MaximumConnections = 24)
{
    public void Validate()
    {
        if (InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(ServiceRole) ||
            ConnectionTimeoutSeconds is < 1 or > 60 || CommandTimeoutSeconds is < 1 or > 120 || MaximumConnections is < 1 or > 64)
            throw new ArgumentException("The protected catalogue endpoint is invalid.");
    }
}

public sealed class PostgreSqlVaultCatalogue : IVaultCatalogue, IAsyncDisposable
{
    private const long ControlMutationLock = 0x4656434F4E54524C;
    private readonly NpgsqlDataSource dataSource;
    private readonly FluxVaultIpcLimits limits;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public VaultCatalogueEndpoint Endpoint { get; }
    internal Func<Guid, CancellationToken, Task>? AfterMutationCommit { get; init; }
    internal Func<Guid, CancellationToken, Task>? BeforeMirrorDrainCommit { get; init; }

    public PostgreSqlVaultCatalogue(VaultCatalogueEndpoint endpoint, FluxVaultIpcLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        Endpoint = endpoint;
        this.limits = limits ?? new();
        this.limits.Validate();
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host, Port = endpoint.Port, Database = endpoint.Database, Username = endpoint.ServiceRole,
            RequireAuth = "SSPI", Pooling = true, MinPoolSize = 0, MaxPoolSize = endpoint.MaximumConnections,
            Timeout = endpoint.ConnectionTimeoutSeconds, CommandTimeout = endpoint.CommandTimeoutSeconds,
            SearchPath = "pg_catalog", IncludeErrorDetail = false
        };
        var source = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        source.UsePasswordProvider(_ => throw new NotSupportedException("The vault catalogue requires Windows SSPI."),
            (_, _) => ValueTask.FromException<string>(new NotSupportedException("The vault catalogue requires Windows SSPI.")));
        dataSource = source.Build();
    }

    /// <summary>Explicit control-plane provisioning; ordinary opens never create or adopt a catalogue.</summary>
    public async Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT FROM pg_namespace WHERE nspname = 'fv_control')", connection, transaction))
            if (await exists.ExecuteScalarAsync(cancellationToken) is true)
                throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await using var command = new NpgsqlCommand("""
            CREATE SCHEMA fv_control;
            REVOKE ALL ON SCHEMA fv_control FROM PUBLIC;
            CREATE TABLE fv_control.identity (
                singleton boolean PRIMARY KEY CHECK (singleton), instance_id uuid NOT NULL, schema_version integer NOT NULL CHECK (schema_version = 1)
            );
            CREATE TABLE fv_control.vault (
                singleton boolean PRIMARY KEY CHECK (singleton), vault_id uuid UNIQUE NOT NULL,
                revision bigint NOT NULL CHECK (revision > 0), display_name text NOT NULL CHECK (octet_length(display_name) BETWEEN 1 AND 1024),
                owner_sid text NOT NULL CHECK (octet_length(owner_sid) BETWEEN 1 AND 256),
                grants text NOT NULL CHECK (octet_length(grants) <= 16777216),
                binding text NOT NULL CHECK (octet_length(binding) <= 16777216),
                configuration text NOT NULL CHECK (octet_length(configuration) <= 16777216)
            );
            CREATE TABLE fv_control.operations (
                operation_id uuid PRIMARY KEY, vault_id uuid NOT NULL REFERENCES fv_control.vault(vault_id), actor_sid text NOT NULL,
                command integer NOT NULL, fingerprint text NOT NULL CHECK (length(fingerprint) = 64),
                required_permissions integer NOT NULL CHECK (required_permissions BETWEEN 1 AND 63),
                state integer NOT NULL CHECK (state IN (0,1)), revision bigint NOT NULL CHECK (revision > 0),
                response text CHECK (octet_length(response) <= 67108864),
                admitted_at_utc timestamptz NOT NULL DEFAULT now(), completed_at_utc timestamptz
            );
            REVOKE ALL ON ALL TABLES IN SCHEMA fv_control FROM PUBLIC;
            ALTER DEFAULT PRIVILEGES IN SCHEMA fv_control REVOKE ALL ON TABLES FROM PUBLIC;
            INSERT INTO fv_control.identity VALUES (true, @instance, 1);
            """, connection, transaction);
        command.Parameters.AddWithValue("instance", Endpoint.InstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Binding/configuration come from trusted provisioning, never directly from an ordinary IPC payload.</summary>
    public async Task<VaultCatalogueEntry> InitializeAsync(FluxVaultCallerContext creator, VaultBinding binding, string displayName,
        FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
    {
        binding.Validate();
        var policy = VaultAccessPolicy.ForCreator(creator);
        if (string.IsNullOrWhiteSpace(displayName) || System.Text.Encoding.UTF8.GetByteCount(displayName) > 1024)
            throw new ArgumentException("A bounded display name is required.", nameof(displayName));
        configuration = Normalize(binding, configuration);
        ValidateBindingConfiguration(binding, configuration);
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        // Initialisation is one-time and serialised. It never replaces an existing vault.
        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT FROM fv_control.vault)", connection, transaction))
            if (await exists.ExecuteScalarAsync(cancellationToken) is true)
                throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration);
        await using var command = new NpgsqlCommand("""
            INSERT INTO fv_control.vault(singleton,vault_id,revision,display_name,owner_sid,grants,binding,configuration)
            VALUES (true,@id,1,@name,@owner,@grants,@binding,@configuration)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", binding.Id.Value);
        command.Parameters.AddWithValue("name", displayName);
        command.Parameters.AddWithValue("owner", policy.OwnerSid);
        command.Parameters.AddWithValue("grants", Encode(policy.Grants));
        command.Parameters.AddWithValue("binding", Encode(binding));
        command.Parameters.AddWithValue("configuration", Encode(configuration));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(binding, 1, displayName, policy, configuration);
    }

    public Task<VaultAdmission> AdmitAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) =>
        MutateAsync(caller, request, Mutation.Admit, cancellationToken);

    /// <summary>Service bootstrap integrity check; never exposed as an IPC authorisation route.</summary>
    public async Task<VaultCatalogueEntry> VerifyInstallationAsync(FluxVaultInstallation installation, CancellationToken cancellationToken = default)
    {
        installation.Validate();
        if (installation.Endpoint != Endpoint) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        var entry = await LoadAsync(connection, transaction, installation.Binding.Id, cancellationToken);
        if (entry is null || entry.Binding != installation.Binding || entry.Access.OwnerSid != installation.CreatorSid)
            throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public Task<VaultAdmission> SaveConfigurationAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) =>
        MutateAsync(caller, request, Mutation.Save, cancellationToken);

    public Task<VaultAdmission> SetAccessAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) =>
        MutateAsync(caller, request, Mutation.Access, cancellationToken);

    private enum Mutation { Admit, Save, Access }

    private static bool ValidPurgeScopes(IReadOnlyList<RepositoryPurgeScope> scopes)
    {
        foreach (var scope in scopes)
        {
            if (scope is null || !Enum.IsDefined(scope.Kind) || string.IsNullOrWhiteSpace(scope.SourcePath) ||
                !Path.IsPathFullyQualified(scope.SourcePath)) return false;
            try { _ = Path.GetFullPath(scope.SourcePath); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        }
        return true;
    }

    private async Task<VaultAdmission> MutateAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, Mutation mutation, CancellationToken ct)
    {
        RequireCaller(caller);
        if (request.VaultId is { IsValid: false } || request.VaultId is null && request.Command != FluxVaultIpcCommand.GetStatus ||
            !VaultCommandPolicy.TryGet(request, out var requirement) || request.ProfileId is not null || request.SourceProfileId is not null ||
            (mutation == Mutation.Save && request.Command != FluxVaultIpcCommand.SaveConfiguration) ||
            (mutation == Mutation.Access && request.Command != FluxVaultIpcCommand.SetVaultAccess) ||
            (mutation == Mutation.Admit && request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.SetVaultAccess))
            throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
        var modifying = IsMutation(request.Command);
        if (modifying && (request.OperationId is null || request.OperationId == Guid.Empty || request.ExpectedVaultRevision is null or <= 0))
            throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
        var fingerprint = RequestFingerprint(request);
        await using var connection = await OpenVerifiedAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The installation lock is held only for bounded catalogue transactions.
        await LockAsync(connection, transaction, ct);
        var vault = await LoadAsync(connection, transaction, request.VaultId, ct);
        if (vault is null || !VaultCommandPolicy.TryGet(request, vault.Configuration, out requirement) ||
            !VaultAuthorizer.IsAllowed(caller, vault.Access, requirement.Permissions))
            throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
        var id = vault.Binding.Id;
        if (modifying)
        {
            var receipt = await ReadReceiptAsync(connection, transaction, request.OperationId!.Value, ct);
            if (receipt is not null)
            {
                if (receipt.VaultId != id || receipt.ActorSid != caller.UserSid || receipt.Command != request.Command || receipt.Fingerprint != fingerprint)
                    throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
                if (!VaultAuthorizer.IsAllowed(caller, vault.Access, receipt.RequiredPermissions)) throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
                receipt = await WithResponseAsync(connection, transaction, receipt, ct);
                await transaction.CommitAsync(ct);
                return new(vault, receipt, true);
            }
        }
        if (request.ExpectedVaultRevision is not null && request.ExpectedVaultRevision != vault.Revision)
            throw new VaultCatalogueException(VaultCatalogueFailure.StaleRevision);
        if (request.Command == FluxVaultIpcCommand.ExportDiagnostics)
        {
            try { _ = WindowsLocalPath.Validate(request.ExportPath ?? string.Empty); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration, "Choose an unambiguous absolute local directory for diagnostics."); }
        }
        if (request.Command is FluxVaultIpcCommand.PreviewMirrorRepair or FluxVaultIpcCommand.RunMirrorRepair or
                FluxVaultIpcCommand.PreviewMirrorDrain or FluxVaultIpcCommand.RunMirrorDrain &&
            (request.MirrorNodeId is null ? request.Command is FluxVaultIpcCommand.PreviewMirrorDrain or FluxVaultIpcCommand.RunMirrorDrain :
                string.IsNullOrWhiteSpace(request.MirrorNodeId) || !vault.Configuration.MirrorSet.EnabledNodes.Any(node =>
                    string.Equals(node.Id, request.MirrorNodeId, StringComparison.OrdinalIgnoreCase))))
            throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration,
                "Choose an enabled mirror destination. Only repair supports no selection to target all mirrors.");
        if (request.Command == FluxVaultIpcCommand.RunMirrorDrain && vault.Configuration.MirrorSet.EnabledNodes.Count < 2)
            throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration,
                "Keep at least one enabled mirror destination after draining the selected destination.");
        if (mutation == Mutation.Save)
        {
            if (request.Configuration is null) throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration);
            if (request.PurgeRemovedSelections && (request.RemovedSelections is not { Count: > 0 } ||
                request.PreservedSelections is null || !ValidPurgeScopes(request.RemovedSelections) || !ValidPurgeScopes(request.PreservedSelections)))
                throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration);
            var configuration = Normalize(vault.Binding, request.Configuration);
            ValidateBindingConfiguration(vault.Binding, configuration);
            if (!SameInfrastructure(vault.Configuration, configuration)) throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration);
            // Combined save/purge is completed by the dispatcher only after its repository effect is known.
            vault = vault with { Revision = checked(vault.Revision + 1), Configuration = configuration };
        }
        else if (mutation == Mutation.Access)
        {
            if (request.AccessGrants is null) throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration);
            var access = new VaultAccessPolicy(vault.Access.OwnerSid, request.AccessGrants);
            vault = vault with { Revision = checked(vault.Revision + 1), Access = access };
        }
        if (mutation != Mutation.Admit)
        {
            await using var update = new NpgsqlCommand("UPDATE fv_control.vault SET revision=@revision, configuration=@configuration, grants=@grants WHERE singleton=true AND vault_id=@id", connection, transaction);
            update.Parameters.AddWithValue("revision", vault.Revision);
            update.Parameters.AddWithValue("configuration", Encode(vault.Configuration));
            update.Parameters.AddWithValue("grants", Encode(vault.Access.Grants));
            update.Parameters.AddWithValue("id", id.Value);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        }
        VaultOperationReceipt? recorded = null;
        if (modifying)
        {
            var completed = mutation == Mutation.Access || (mutation == Mutation.Save && !request.PurgeRemovedSelections);
            var response = completed ? FluxVaultIpcResponse.Ok() with { VaultId = id, VaultRevision = vault.Revision, OperationId = request.OperationId } : null;
            recorded = new(request.OperationId!.Value, id, caller.UserSid, request.Command, fingerprint,
                completed ? VaultOperationState.Completed : VaultOperationState.Admitted, vault.Revision, response, requirement.Permissions);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO fv_control.operations(operation_id,vault_id,actor_sid,command,fingerprint,required_permissions,state,revision,response,completed_at_utc)
                VALUES(@operation,@id,@actor,@command,@fingerprint,@permissions,@state,@revision,@response,CASE WHEN @state=1 THEN now() ELSE NULL END)
                """, connection, transaction);
            AddReceiptParameters(insert, recorded);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        if (recorded is not null && AfterMutationCommit is not null) await AfterMutationCommit(recorded.OperationId, ct);
        return new(vault, recorded, false);
    }

    public async Task<VaultOperationReceipt?> GetReceiptAsync(FluxVaultCallerContext caller, VaultId vaultId, Guid operationId, CancellationToken cancellationToken = default)
    {
        RequireCaller(caller);
        if (!vaultId.IsValid || operationId == Guid.Empty) throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        var vault = await LoadAsync(connection, transaction, vaultId, cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, operationId, cancellationToken);
        if (vault is null || receipt is null || receipt.VaultId != vaultId || receipt.ActorSid != caller.UserSid ||
            !VaultAuthorizer.IsAllowed(caller, vault.Access, receipt.RequiredPermissions))
            throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
        receipt = await WithResponseAsync(connection, transaction, receipt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    public async Task CompleteAsync(VaultOperationReceipt receipt, FluxVaultIpcResponse response, CancellationToken cancellationToken = default)
    {
        if (receipt.Command == FluxVaultIpcCommand.RunMirrorDrain)
            throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
        response = response with { VaultId = receipt.VaultId, VaultRevision = receipt.Revision, OperationId = receipt.OperationId };
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE fv_control.operations SET state=1,response=@response,completed_at_utc=now()
            WHERE operation_id=@operation AND vault_id=@id AND actor_sid=@actor AND command=@command AND fingerprint=@fingerprint AND revision=@revision AND state=0
            """, connection);
        AddReceiptParameters(command, receipt with { Response = response });
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
    }

    /// <summary>Repository effects already ran. Publish their result and the single mirror disablement together.</summary>
    public async Task<FluxVaultIpcResponse> CompleteMirrorDrainAsync(VaultOperationReceipt receipt, FluxVaultIpcRequest request,
        FluxVaultIpcResponse response, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.Command != FluxVaultIpcCommand.RunMirrorDrain || receipt.State != VaultOperationState.Admitted ||
            request.Command != receipt.Command || request.VaultId != receipt.VaultId || request.OperationId != receipt.OperationId ||
            request.ExpectedVaultRevision != receipt.Revision || string.IsNullOrWhiteSpace(request.MirrorNodeId) ||
            RequestFingerprint(request) != receipt.Fingerprint)
            throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
        var report = response.MirrorRebalance;
        if (response.Success && report is null || report is not null &&
            (report.Operation != MirrorRebalanceOperation.Drain || report.IsPreview || report.Actions is null || report.Nodes is null ||
             !string.Equals(report.RequestedMirrorNodeId, request.MirrorNodeId, StringComparison.OrdinalIgnoreCase)))
            throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        var vault = await LoadAsync(connection, transaction, receipt.VaultId, cancellationToken);
        var durable = await ReadReceiptAsync(connection, transaction, receipt.OperationId, cancellationToken);
        if (vault is null || durable != receipt)
            throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
        if (vault.Revision != receipt.Revision)
            throw new VaultCatalogueException(VaultCatalogueFailure.StaleRevision);
        var completed = response.Success && report is { IsCompletedDrain: true };
        if (completed)
        {
            var mirrors = vault.Configuration.MirrorSet;
            if (mirrors.EnabledNodes.Count < 2 || !mirrors.EnabledNodes.Any(node =>
                string.Equals(node.Id, request.MirrorNodeId, StringComparison.OrdinalIgnoreCase)))
                throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
            // No general infrastructure-save route: change only the confirmed destination.
            var configuration = vault.Configuration with { MirrorSet = new(mirrors.Nodes.Select(node =>
                string.Equals(node.Id, request.MirrorNodeId, StringComparison.OrdinalIgnoreCase) ? node with { IsEnabled = false } : node).ToArray(),
                mirrors.PlacementPolicy) };
            vault = vault with { Revision = checked(vault.Revision + 1), Configuration = configuration };
            await using var update = new NpgsqlCommand("""
                UPDATE fv_control.vault SET revision=@revision,configuration=@configuration
                WHERE singleton=true AND vault_id=@id AND revision=@original
                """, connection, transaction);
            update.Parameters.AddWithValue("revision", vault.Revision);
            update.Parameters.AddWithValue("configuration", Encode(configuration));
            update.Parameters.AddWithValue("id", receipt.VaultId.Value);
            update.Parameters.AddWithValue("original", receipt.Revision);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new VaultCatalogueException(VaultCatalogueFailure.StaleRevision);
        }
        response = response with { VaultId = receipt.VaultId, VaultRevision = vault.Revision, OperationId = receipt.OperationId };
        await using (var finish = new NpgsqlCommand("""
            UPDATE fv_control.operations SET state=1,revision=@completed_revision,response=@response,completed_at_utc=now()
            WHERE operation_id=@operation AND vault_id=@id AND actor_sid=@actor AND command=@command
                AND fingerprint=@fingerprint AND revision=@revision AND required_permissions=@permissions AND state=0
            """, connection, transaction))
        {
            AddReceiptParameters(finish, receipt with { Response = response });
            finish.Parameters.AddWithValue("completed_revision", vault.Revision);
            if (await finish.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new VaultCatalogueException(VaultCatalogueFailure.OperationConflict);
        }
        if (BeforeMirrorDrainCommit is not null) await BeforeMirrorDrainCommit(receipt.OperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (AfterMutationCommit is not null) await AfterMutationCommit(receipt.OperationId, cancellationToken);
        return response;
    }

    private static string RequestFingerprint(FluxVaultIpcRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions)));

    private const string SelectVault = "SELECT vault_id,revision,display_name,owner_sid,grants,binding,configuration FROM fv_control.vault";

    private async Task<VaultCatalogueEntry?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, VaultId? id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(SelectVault + " WHERE singleton=true AND (@id IS NULL OR vault_id=@id) FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Uuid, id is { } target ? target.Value : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVault(reader, ReadPolicy(reader)) : null;
    }

    private VaultAccessPolicy ReadPolicy(NpgsqlDataReader reader) => new(reader.GetString(3), Decode<VaultAccessGrant[]>(reader.GetString(4)));
    private VaultCatalogueEntry ReadVault(NpgsqlDataReader reader, VaultAccessPolicy access)
    {
        var binding = Decode<VaultBinding>(reader.GetString(5));
        binding.Validate();
        if (binding.Id.Value != reader.GetGuid(0) || reader.GetInt64(1) < 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        var configuration = Decode<FluxVaultConfiguration>(reader.GetString(6));
        ValidateBindingConfiguration(binding, configuration);
        return new(binding, reader.GetInt64(1), reader.GetString(2), access, configuration);
    }

    private async Task<VaultOperationReceipt?> ReadReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid operation, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT operation_id,vault_id,actor_sid,command,fingerprint,state,revision,required_permissions FROM fv_control.operations WHERE operation_id=@operation", connection, transaction);
        command.Parameters.AddWithValue("operation", operation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), new(reader.GetGuid(1)), reader.GetString(2), (FluxVaultIpcCommand)reader.GetInt32(3), reader.GetString(4),
            (VaultOperationState)reader.GetInt32(5), reader.GetInt64(6), null, (VaultPermission)reader.GetInt32(7));
    }

    private async Task<VaultOperationReceipt> WithResponseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, VaultOperationReceipt receipt, CancellationToken ct)
    {
        if (receipt.State == VaultOperationState.Admitted) return receipt;
        if (receipt.State != VaultOperationState.Completed) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await using var command = new NpgsqlCommand("SELECT response FROM fv_control.operations WHERE operation_id=@operation", connection, transaction);
        command.Parameters.AddWithValue("operation", receipt.OperationId);
        var response = await command.ExecuteScalarAsync(ct) as string ?? throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        return receipt with { Response = Decode<FluxVaultIpcResponse>(response, limits.MaximumResponseBytes) };
    }

    private void AddReceiptParameters(NpgsqlCommand command, VaultOperationReceipt receipt)
    {
        command.Parameters.AddWithValue("operation", receipt.OperationId);
        command.Parameters.AddWithValue("id", receipt.VaultId.Value);
        command.Parameters.AddWithValue("actor", receipt.ActorSid);
        command.Parameters.AddWithValue("command", (int)receipt.Command);
        command.Parameters.AddWithValue("fingerprint", receipt.Fingerprint);
        command.Parameters.AddWithValue("permissions", (int)receipt.RequiredPermissions);
        command.Parameters.AddWithValue("state", (int)receipt.State);
        command.Parameters.AddWithValue("revision", receipt.Revision);
        command.Parameters.AddWithValue("response", NpgsqlTypes.NpgsqlDbType.Text, receipt.Response is null ? DBNull.Value : Encode(receipt.Response, limits.MaximumResponseBytes));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT current_database(), current_user, session_user, current_setting('fsync'), current_setting('full_page_writes'), current_setting('synchronous_commit')", connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != Endpoint.Database || reader.GetString(1) != Endpoint.ServiceRole || reader.GetString(2) != Endpoint.ServiceRole ||
                reader.GetString(3) != "on" || reader.GetString(4) != "on" || reader.GetString(5) != "on")
                throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task<NpgsqlConnection> OpenVerifiedAsync(CancellationToken ct)
    {
        var connection = await OpenAsync(ct);
        try
        {
            await using var security = new NpgsqlCommand("""
                SELECT EXISTS (SELECT FROM pg_namespace n JOIN pg_roles r ON r.oid=n.nspowner WHERE n.nspname='fv_control' AND r.rolname=@role
                    AND NOT EXISTS (SELECT FROM aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) a WHERE a.grantee <> n.nspowner))
                    AND (SELECT count(*)=3 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner
                        WHERE n.nspname='fv_control' AND c.relname IN ('identity','vault','operations') AND c.relkind='r' AND c.relpersistence='p' AND r.rolname=@role
                        AND NOT EXISTS (SELECT FROM aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) a WHERE a.grantee <> c.relowner))
                """, connection);
            security.Parameters.AddWithValue("role", Endpoint.ServiceRole);
            if (await security.ExecuteScalarAsync(ct) is not true) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
            await using var identity = new NpgsqlCommand("SELECT instance_id,schema_version FROM fv_control.identity WHERE singleton=true", connection);
            await using var reader = await identity.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetGuid(0) != Endpoint.InstanceId || reader.GetInt32(1) != 1)
                throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SET LOCAL synchronous_commit=on; SELECT pg_advisory_xact_lock(@key)", connection, transaction);
        command.Parameters.AddWithValue("key", ControlMutationLock);
        await command.ExecuteNonQueryAsync(ct);
    }

    private string Encode<T>(T value, int? maximumBytes = null)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (encoded.Length > (maximumBytes ?? limits.MaximumRequestBytes)) throw new InvalidDataException("Catalogue payload exceeds its supported bound.");
        return System.Text.Encoding.UTF8.GetString(encoded);
    }
    private T Decode<T>(string value, int? maximumBytes = null)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) > (maximumBytes ?? limits.MaximumRequestBytes)) throw new InvalidDataException("Catalogue payload exceeds its supported bound.");
        return JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
    }
    private static FluxVaultConfiguration Normalize(VaultBinding binding, FluxVaultConfiguration configuration)
    {
        var normalized = new FileFluxVaultConfigurationStore(Path.Combine(binding.StateRoot, "unused.json"), binding.StateRoot).Normalise(configuration);
        FileFluxVaultConfigurationStore.Validate(normalized);
        return normalized;
    }
    private static void ValidateBindingConfiguration(VaultBinding binding, FluxVaultConfiguration configuration)
    {
        if (RootKey(configuration.RepositoryPath) != RootKey(binding.RepositoryPath) || configuration.MetadataStore != binding.MetadataStore)
            throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration);
    }
    private static bool SameInfrastructure(FluxVaultConfiguration before, FluxVaultConfiguration after) =>
        JsonSerializer.Serialize(new { before.RepositoryPath, before.MirrorPath, before.MirrorSet, before.MetadataStore,
            before.PerformanceWorkspace, before.ShellIntegration, before.DirectCloud, before.SecurityPosture, before.Fleet,
            before.DiagnosticsPolicy.LogDirectory }, JsonOptions) ==
        JsonSerializer.Serialize(new { after.RepositoryPath, after.MirrorPath, after.MirrorSet, after.MetadataStore,
            after.PerformanceWorkspace, after.ShellIntegration, after.DirectCloud, after.SecurityPosture, after.Fleet,
            after.DiagnosticsPolicy.LogDirectory }, JsonOptions);
    private static string RootKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    private static void RequireCaller(FluxVaultCallerContext caller)
    {
        if (!caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid)) throw new VaultCatalogueException(VaultCatalogueFailure.Denied);
    }
    public static bool IsMutation(FluxVaultIpcCommand command) => command is not (
        FluxVaultIpcCommand.GetStatus or FluxVaultIpcCommand.ListVersions or FluxVaultIpcCommand.InspectVersion or
        FluxVaultIpcCommand.GetActivity or FluxVaultIpcCommand.ListBlockedFiles or FluxVaultIpcCommand.GetSyncStatus or
        FluxVaultIpcCommand.GetRepositoryHealth or FluxVaultIpcCommand.GetPerformance or FluxVaultIpcCommand.PreviewRetention or
        FluxVaultIpcCommand.PreviewMirrorRebalance or FluxVaultIpcCommand.PreviewMirrorRepair or FluxVaultIpcCommand.PreviewMirrorDrain or
        FluxVaultIpcCommand.GetOperationStatus);
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
