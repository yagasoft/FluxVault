using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Storage.Integrity;
using Npgsql;

namespace FluxVault.Core.Security;

public sealed partial class PostgreSqlVaultCatalogue
{
    private const long AutomaticRuntimeOwnerLock = 0x46564155544F4F57;
    private Guid? runtimeGeneration;
    private AutomaticRuntimeOwner? runtimeOwner;

    public static string AutomaticScopeFingerprint(VaultCatalogueEntry vault) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            vault.Binding, vault.Configuration.WatchedFolders, vault.Configuration.SelectionRules,
            vault.Configuration.ExclusionRules
        }, JsonOptions)));

    /// <summary>Additive, transactional migration after verifying the existing installation binding.</summary>
    public async Task EnsureAutomaticSchemaAsync(VaultBinding binding, CancellationToken ct = default)
    {
        await using var connection = await OpenVerifiedAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        var vault = await LoadAsync(connection, transaction, binding.Id, ct);
        if (vault?.Binding != binding) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        if (await HasAutomaticSchemaAsync(connection, transaction, ct))
        {
            await using var migrate = new NpgsqlCommand("""
                DO $migration$ BEGIN
                    IF (SELECT schema_version FROM fv_control.automatic_identity WHERE singleton)=1 THEN
                        ALTER TABLE fv_control.automatic_work
                            ADD COLUMN first_event_utc timestamptz NOT NULL DEFAULT now(),
                            ADD COLUMN latest_event_utc timestamptz NOT NULL DEFAULT now(),
                            ADD COLUMN last_attempt_utc timestamptz,
                            ADD COLUMN queued_observation text,
                            ADD COLUMN is_deletion boolean NOT NULL DEFAULT false,
                            ADD COLUMN claimed_is_deletion boolean,
                            ADD COLUMN deleted_from_version_id text,
                            ADD COLUMN mirror_after_outbox_id bigint,
                            ADD COLUMN mirror_through_outbox_id bigint,
                            ADD COLUMN mirror_due_at_utc timestamptz,
                            ADD COLUMN mirror_reason text;
                        UPDATE fv_control.automatic_work SET first_event_utc=LEAST(due_at_utc,now()),
                            latest_event_utc=LEAST(due_at_utc,now()),
                            claimed_is_deletion=CASE WHEN claimed_generation IS NOT NULL THEN false END;
                        ALTER TABLE fv_control.automatic_scopes ADD COLUMN journal_checkpoint jsonb;
                        UPDATE fv_control.automatic_identity SET schema_version=2 WHERE singleton;
                    END IF;
                END $migration$;
                """, connection, transaction);
            await migrate.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        // The base identity schema and strict configuration JSON remain version 1.
        await using var command = new NpgsqlCommand("""
            CREATE TABLE fv_control.automatic_identity (
                singleton boolean PRIMARY KEY CHECK(singleton), schema_version integer NOT NULL
            );
            CREATE TABLE fv_control.automatic_policy (
                singleton boolean PRIMARY KEY CHECK(singleton), vault_id uuid NOT NULL REFERENCES fv_control.vault(vault_id),
                enabled boolean NOT NULL DEFAULT false, approval_required boolean NOT NULL DEFAULT true,
                consent_fingerprint text CHECK(consent_fingerprint IS NULL OR length(consent_fingerprint)=64),
                last_version_id text, last_success_utc timestamptz
            );
            CREATE TABLE fv_control.automatic_runtime (
                singleton boolean PRIMARY KEY CHECK(singleton), vault_id uuid NOT NULL REFERENCES fv_control.vault(vault_id),
                generation uuid NOT NULL
            );
            CREATE TABLE fv_control.automatic_scopes (
                scope_id text PRIMARY KEY CHECK(octet_length(scope_id) BETWEEN 1 AND 1024),
                root_path text NOT NULL CHECK(octet_length(root_path) BETWEEN 1 AND 131072),
                scope_fingerprint text NOT NULL CHECK(length(scope_fingerprint)=64),
                dirty_generation bigint NOT NULL CHECK(dirty_generation>0), scanned_generation bigint NOT NULL DEFAULT 0,
                reason text, journal_checkpoint jsonb
            );
            CREATE TABLE fv_control.automatic_work (
                path_key text PRIMARY KEY CHECK(length(path_key)=64), scope_id text NOT NULL,
                root_path text NOT NULL CHECK(octet_length(root_path) BETWEEN 1 AND 131072),
                source_path text NOT NULL CHECK(octet_length(source_path) BETWEEN 1 AND 131072),
                scope_fingerprint text NOT NULL CHECK(length(scope_fingerprint)=64),
                dirty_generation bigint NOT NULL CHECK(dirty_generation>0), pending boolean NOT NULL DEFAULT true,
                due_at_utc timestamptz NOT NULL, claimed_generation bigint, reserved_version_id text,
                owner_generation uuid, attempts integer NOT NULL DEFAULT 0,
                source_observation text, last_source_observation text, last_version_id text, reason text,
                first_event_utc timestamptz NOT NULL DEFAULT now(), latest_event_utc timestamptz NOT NULL DEFAULT now(),
                last_attempt_utc timestamptz, queued_observation text, is_deletion boolean NOT NULL DEFAULT false,
                claimed_is_deletion boolean, deleted_from_version_id text,
                mirror_after_outbox_id bigint, mirror_through_outbox_id bigint,
                mirror_due_at_utc timestamptz, mirror_reason text,
                CHECK ((claimed_generation IS NULL AND reserved_version_id IS NULL AND owner_generation IS NULL) OR
                       (claimed_generation IS NOT NULL AND claimed_generation>0 AND reserved_version_id IS NOT NULL
                        AND length(reserved_version_id)=32 AND owner_generation IS NOT NULL))
            );
            CREATE INDEX automatic_work_due ON fv_control.automatic_work(due_at_utc) WHERE pending;
            INSERT INTO fv_control.automatic_identity VALUES(true,2);
            INSERT INTO fv_control.automatic_policy(singleton,vault_id) VALUES(true,@vault);
            REVOKE ALL ON ALL TABLES IN SCHEMA fv_control FROM PUBLIC;
            """, connection, transaction);
        command.Parameters.AddWithValue("vault", binding.Id.Value);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<bool> HasAutomaticSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('fv_control.automatic_identity') IS NOT NULL", connection, transaction))
            if (await exists.ExecuteScalarAsync(ct) is not true) return false;
        await using var security = new NpgsqlCommand("""
            SELECT count(*)=5 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_roles r ON r.oid=c.relowner WHERE n.nspname='fv_control'
            AND c.relname IN ('automatic_identity','automatic_policy','automatic_runtime','automatic_scopes','automatic_work')
            AND c.relkind='r' AND c.relpersistence='p' AND r.rolname=@role
            AND NOT EXISTS(SELECT FROM aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) a WHERE a.grantee<>c.relowner)
            """, connection, transaction);
        security.Parameters.AddWithValue("role", Endpoint.ServiceRole);
        if (await security.ExecuteScalarAsync(ct) is not true)
            throw new AutomaticProtectionUnavailableException("Automatic protection extension ownership and durable table storage could not be verified.");
        await using var version = new NpgsqlCommand("SELECT schema_version FROM fv_control.automatic_identity WHERE singleton", connection, transaction);
        if (await version.ExecuteScalarAsync(ct) is not int schema || schema is not (1 or 2))
            throw new AutomaticProtectionUnavailableException("Automatic protection has an unsupported extension version. Manual history and recovery remain available.");
        return true;
    }

    public async Task<AutomaticCatalogueState> ReadAutomaticStateAsync(VaultBinding binding, CancellationToken ct = default)
    {
        await using var connection = await OpenVerifiedAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        var vault = await LoadAsync(connection, transaction, binding.Id, ct);
        if (vault?.Binding != binding) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        var fingerprint = AutomaticScopeFingerprint(vault);
        if (!await HasAutomaticSchemaAsync(connection, transaction, ct))
            return new(vault, new(), true, fingerprint, 0, 0, null, null);
        await using var command = new NpgsqlCommand("""
            SELECT enabled,approval_required,consent_fingerprint,last_version_id,last_success_utc,
                (SELECT count(*)::integer FROM fv_control.automatic_work WHERE pending),
                (SELECT count(*)::integer FROM fv_control.automatic_work WHERE claimed_generation IS NOT NULL),
                (SELECT count(*)::integer FROM fv_control.automatic_work WHERE mirror_through_outbox_id IS NOT NULL),
                (SELECT mirror_reason FROM fv_control.automatic_work WHERE mirror_through_outbox_id IS NOT NULL ORDER BY mirror_due_at_utc,path_key LIMIT 1)
            FROM fv_control.automatic_policy WHERE singleton AND vault_id=@vault
            """, connection, transaction);
        command.Parameters.AddWithValue("vault", binding.Id.Value);
        AutomaticCatalogueState state;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
            state = new(vault, new(reader.GetBoolean(0)), reader.GetBoolean(1) || reader.IsDBNull(2) || reader.GetString(2) != fingerprint,
                fingerprint, reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),reader.GetInt32(7),reader.IsDBNull(8)?null:reader.GetString(8));
        }
        await transaction.CommitAsync(ct);
        return state;
    }

    /// <summary>The returned dedicated connection owns the session lock until joined disposal.</summary>
    public async Task<AutomaticRuntimeOwner> OpenAutomaticRuntimeOwnerAsync(VaultBinding binding,
        AutomaticProtectionRuntimePolicy policy, CancellationToken ct = default)
    {
        policy.Validate();
        binding.Validate();
        var connection = await OpenVerifiedAsync(ct, dedicated: true);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock)", connection)
                { CommandTimeout = policy.RuntimeOwnerCommandTimeoutSeconds };
            command.Parameters.AddWithValue("lock", AutomaticRuntimeOwnerLock);
            if (await command.ExecuteScalarAsync(ct) is not true)
                throw new AutomaticProtectionUnavailableException("Another runtime owns this installation. No replacement generation was started.");
            return new(connection, binding, policy.RuntimeOwnerCommandTimeoutSeconds);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    /// <summary>Drain the old repository lease before publishing the durable generation fence.</summary>
    public async Task ActivateAutomaticRuntimeAsync(AutomaticRuntimeOwner owner, CancellationToken ct = default)
    {
        await owner.VerifyAliveAsync(ct);
        await using var lease = await RepositoryLeaseSet.AcquireAsync(owner.Binding.RepositoryPath, [], MirrorLeaseMode.None, ct, binding: owner.Binding);
        await using var connection = await OpenVerifiedAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        var vault = await LoadAsync(connection, transaction, owner.Binding.Id, ct);
        if (vault?.Binding != owner.Binding || !await HasAutomaticSchemaAsync(connection, transaction, ct))
            throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await owner.VerifyAliveAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO fv_control.automatic_runtime(singleton,vault_id,generation) VALUES(true,@vault,@generation)
            ON CONFLICT(singleton) DO UPDATE SET generation=excluded.generation WHERE automatic_runtime.vault_id=excluded.vault_id
            """, connection, transaction);
        command.Parameters.AddWithValue("vault", owner.Binding.Id.Value);
        command.Parameters.AddWithValue("generation", owner.Generation);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await transaction.CommitAsync(ct);
        runtimeOwner = owner;
        runtimeGeneration = owner.Generation;
    }

    public async ValueTask VerifyRuntimeFenceAsync(CancellationToken ct = default)
    {
        if (runtimeOwner is not { } owner || runtimeGeneration is null) return;
        await owner.VerifyAliveAsync(ct);
        await using var connection = await OpenVerifiedAsync(ct);
        await using var command = new NpgsqlCommand("SELECT generation FROM fv_control.automatic_runtime WHERE singleton AND vault_id=@vault", connection);
        command.Parameters.AddWithValue("vault", owner.Binding.Id.Value);
        if (await command.ExecuteScalarAsync(ct) is not Guid actual || actual != runtimeGeneration)
            throw new AutomaticProtectionUnavailableException("This service generation no longer owns the vault. No new repository operation was started.");
    }

    private async Task VerifyRuntimeFenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (runtimeOwner is not { } owner || runtimeGeneration is null) return;
        await owner.VerifyAliveAsync(ct);
        await using var command = new NpgsqlCommand("SELECT generation FROM fv_control.automatic_runtime WHERE singleton AND vault_id=@vault", connection, transaction);
        command.Parameters.AddWithValue("vault", owner.Binding.Id.Value);
        if (await command.ExecuteScalarAsync(ct) is not Guid actual || actual != runtimeGeneration)
            throw new AutomaticProtectionUnavailableException("This service generation no longer owns catalogue mutations.");
    }

    private async Task ApplyAutomaticPolicyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        FluxVaultCallerContext caller, VaultCatalogueEntry vault, FluxVaultIpcRequest request, CancellationToken ct)
    {
        if (caller.UserSid != vault.Access.OwnerSid)
            throw new VaultCatalogueException(VaultCatalogueFailure.Denied, "Only the creator can approve automatic access to their saved source selection.");
        if (request.IsAutomaticProtectionEnabled is not { } enabled || request != (FluxVaultIpcRequest.SetAutomaticProtection(enabled) with
            { VaultId=request.VaultId, ExpectedVaultRevision=request.ExpectedVaultRevision, OperationId=request.OperationId }))
            throw new VaultCatalogueException(VaultCatalogueFailure.InvalidConfiguration, "Choose an explicit automatic protection preference without other changes.");
        if (!await HasAutomaticSchemaAsync(connection, transaction, ct))
            throw new AutomaticProtectionUnavailableException("Automatic protection has not been initialised by the service.");
        await using var command = new NpgsqlCommand("""
            UPDATE fv_control.automatic_policy SET enabled=@enabled,approval_required=false,consent_fingerprint=@fingerprint
            WHERE singleton AND vault_id=@vault
            """, connection, transaction);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("fingerprint", AutomaticScopeFingerprint(vault));
        command.Parameters.AddWithValue("vault", vault.Binding.Id.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
    }

    private async Task UpdateAutomaticConsentForSaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        FluxVaultCallerContext caller, VaultCatalogueEntry before, VaultCatalogueEntry after, CancellationToken ct)
    {
        if (!await HasAutomaticSchemaAsync(connection, transaction, ct)) return;
        var fingerprint = AutomaticScopeFingerprint(after);
        // A replay returns before this hook. General protection grants cannot widen creator-token authority.
        if (caller.UserSid != after.Access.OwnerSid && fingerprint == AutomaticScopeFingerprint(before)) return;
        await using var command = new NpgsqlCommand("""
            UPDATE fv_control.automatic_policy SET approval_required=@required,consent_fingerprint=@fingerprint
            WHERE singleton AND vault_id=@vault
            """, connection, transaction);
        command.Parameters.AddWithValue("required", caller.UserSid != after.Access.OwnerSid);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("vault", after.Binding.Id.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
    }

    /// <summary>Used only after the automatic runtime has been quiesced for a checked rollback.</summary>
    public async Task RequireAutomaticApprovalAsync(VaultBinding binding, CancellationToken ct = default)
    {
        await using var connection = await OpenVerifiedAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        var vault = await LoadAsync(connection, transaction, binding.Id, ct);
        if (vault?.Binding != binding || !await HasAutomaticSchemaAsync(connection, transaction, ct))
            throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await using var command = new NpgsqlCommand("UPDATE fv_control.automatic_policy SET approval_required=true WHERE singleton AND vault_id=@vault", connection, transaction);
        command.Parameters.AddWithValue("vault", binding.Id.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        await transaction.CommitAsync(ct);
    }

    public sealed class AutomaticRuntimeOwner : IAsyncDisposable
    {
        private readonly NpgsqlConnection connection;
        private readonly SemaphoreSlim gate = new(1,1);
        private readonly int timeout;
        private bool failed;
        private bool disposed;
        internal VaultBinding Binding { get; }
        public Guid Generation { get; } = Guid.NewGuid();
        internal int BackendProcessId => connection.ProcessID;
        internal AutomaticRuntimeOwner(NpgsqlConnection connection, VaultBinding binding, int timeout)
        { this.connection=connection; Binding=binding; this.timeout=timeout; }
        public async ValueTask VerifyAliveAsync(CancellationToken ct = default)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (disposed || failed) throw new AutomaticProtectionUnavailableException("The runtime owner connection ended. A new fenced generation is required.");
                try
                {
                    await using var command = new NpgsqlCommand("SELECT 1", connection) { CommandTimeout=timeout };
                    if (await command.ExecuteScalarAsync(ct) is not 1) throw new IOException("The runtime owner could not be verified.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch(Exception exception)
                {
                    failed=true;
                    throw new AutomaticProtectionUnavailableException("The dedicated runtime owner connection ended. A new fenced generation is required.", exception);
                }
            }
            finally { gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (disposed) return;
                disposed=true;
                // This connection is not pooled. Closing it releases the session lock;
                // the owner never reconnects after a failure or returns a held lock to a pool.
                await connection.DisposeAsync();
            }
            finally { gate.Release(); }
        }
    }
}
