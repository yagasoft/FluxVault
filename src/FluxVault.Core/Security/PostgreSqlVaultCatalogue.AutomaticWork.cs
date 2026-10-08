using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Configuration;
using Npgsql;

namespace FluxVault.Core.Security;

public sealed partial class PostgreSqlVaultCatalogue
{
    public Task MarkAutomaticScopeDirtyAsync(VaultBinding binding, string scopeId, string fingerprint, string reason, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            RequireCurrentAutomaticScope(vault, fingerprint);
            var folder = vault.Configuration.WatchedFolders.SingleOrDefault(item => item.IsEnabled && item.Id == scopeId) ??
                throw new AutomaticProtectionUnavailableException("The protected source scope changed.");
            await using var command = new NpgsqlCommand("""
                INSERT INTO fv_control.automatic_scopes(scope_id,root_path,scope_fingerprint,dirty_generation,reason)
                VALUES(@scope,@root,@fingerprint,1,@reason)
                ON CONFLICT(scope_id) DO UPDATE SET root_path=excluded.root_path,scope_fingerprint=excluded.scope_fingerprint,
                    dirty_generation=automatic_scopes.dirty_generation+1,reason=excluded.reason
                """, connection, transaction);
            command.Parameters.AddWithValue("scope", scopeId);
            command.Parameters.AddWithValue("root", folder.Path);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("reason", BoundedReason(reason));
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public Task<IReadOnlyList<AutomaticScopeWork>> ListAutomaticScopesAsync(VaultBinding binding, CancellationToken ct = default) =>
        AutomaticMutationAsync<IReadOnlyList<AutomaticScopeWork>>(binding, async (connection, transaction, _) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT scope_id,root_path,scope_fingerprint,dirty_generation FROM fv_control.automatic_scopes
                WHERE dirty_generation>scanned_generation ORDER BY scope_id LIMIT 256
                """, connection, transaction);
            var result = new List<AutomaticScopeWork>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3)));
            return result;
        }, ct);

    public Task CompleteAutomaticScopeAsync(VaultBinding binding, AutomaticScopeWork scope, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            RequireCurrentAutomaticScope(vault, scope.ScopeFingerprint);
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_scopes SET scanned_generation=@generation,reason=NULL
                WHERE scope_id=@scope AND scope_fingerprint=@fingerprint AND dirty_generation=@generation
                """, connection, transaction);
            command.Parameters.AddWithValue("scope", scope.ScopeId);
            command.Parameters.AddWithValue("fingerprint", scope.ScopeFingerprint);
            command.Parameters.AddWithValue("generation", scope.DirtyGeneration);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }, ct);

    /// <summary>Every accepted hint reaches durable work before its discovery coverage is acknowledged.</summary>
    public Task<bool> QueueAutomaticPathAsync(VaultBinding binding, string sourcePath, string fingerprint,
        DateTimeOffset dueAtUtc, string reason, AutomaticProtectionRuntimePolicy policy, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            policy.Validate();
            RequireCurrentAutomaticScope(vault, fingerprint);
            if (!ProtectionSourceSelection.TryFindFolder(vault.Configuration, sourcePath, out var folder))
                throw new AutomaticProtectionUnavailableException("The path is outside the saved automatic source selection.");
            var path = Path.GetFullPath(sourcePath);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.Id + "\0" + path.ToUpperInvariant())));
            await using (var capacity = new NpgsqlCommand("""
                SELECT EXISTS(SELECT FROM fv_control.automatic_work WHERE path_key=@key AND pending) OR
                    (SELECT count(*) FROM fv_control.automatic_work WHERE pending)<@maximum
                """, connection, transaction))
            {
                capacity.Parameters.AddWithValue("key", key);
                capacity.Parameters.AddWithValue("maximum", policy.MaximumPendingPaths);
                if (await capacity.ExecuteScalarAsync(ct) is not true) return false;
            }
            await using var command = new NpgsqlCommand("""
                INSERT INTO fv_control.automatic_work(path_key,scope_id,root_path,source_path,scope_fingerprint,dirty_generation,due_at_utc,reason)
                VALUES(@key,@scope,@root,@path,@fingerprint,1,@due,@reason)
                ON CONFLICT(path_key) DO UPDATE SET dirty_generation=automatic_work.dirty_generation+1,
                    pending=true,scope_fingerprint=excluded.scope_fingerprint,root_path=excluded.root_path,
                    due_at_utc=LEAST(excluded.due_at_utc,CASE WHEN automatic_work.pending THEN automatic_work.due_at_utc ELSE excluded.due_at_utc END),
                    reason=excluded.reason
                """, connection, transaction);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("scope", folder.Id);
            command.Parameters.AddWithValue("root", folder.Path);
            command.Parameters.AddWithValue("path", path);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("due", dueAtUtc.ToUniversalTime());
            command.Parameters.AddWithValue("reason", BoundedReason(reason));
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public Task<AutomaticPathWork?> ClaimAutomaticPathAsync(VaultBinding binding, CancellationToken ct = default) =>
        AutomaticMutationAsync<AutomaticPathWork?>(binding, async (connection, transaction, vault) =>
        {
            if (!vault.Configuration.IsEnabled) return null;
            var fingerprint = AutomaticScopeFingerprint(vault);
            await using (var approved = new NpgsqlCommand("""
                SELECT enabled AND NOT approval_required AND consent_fingerprint=@fingerprint
                FROM fv_control.automatic_policy WHERE singleton AND vault_id=@vault
                """, connection, transaction))
            {
                approved.Parameters.AddWithValue("fingerprint", fingerprint);
                approved.Parameters.AddWithValue("vault", binding.Id.Value);
                if (await approved.ExecuteScalarAsync(ct) is not true) return null;
            }
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET claimed_generation=dirty_generation,reserved_version_id=@version,
                    owner_generation=@owner,attempts=attempts+1,source_observation=NULL
                WHERE path_key=(SELECT path_key FROM fv_control.automatic_work
                    WHERE pending AND claimed_generation IS NULL AND due_at_utc<=now() AND scope_fingerprint=@fingerprint
                    ORDER BY due_at_utc,path_key LIMIT 1 FOR UPDATE)
                RETURNING path_key,scope_id,root_path,source_path,scope_fingerprint,dirty_generation,claimed_generation,
                    reserved_version_id,owner_generation,attempts,source_observation,last_source_observation,last_version_id,reason
                """, connection, transaction);
            command.Parameters.AddWithValue("version", Guid.CreateVersion7().ToString("N"));
            command.Parameters.AddWithValue("owner", runtimeGeneration!.Value);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadAutomaticWork(reader) : null;
        }, ct);

    public Task<IReadOnlyList<AutomaticPathWork>> ListAutomaticClaimsAsync(VaultBinding binding, int maximum, CancellationToken ct = default) =>
        AutomaticMutationAsync<IReadOnlyList<AutomaticPathWork>>(binding, async (connection, transaction, _) =>
        {
            if (maximum is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximum));
            await using var command = new NpgsqlCommand("""
                SELECT path_key,scope_id,root_path,source_path,scope_fingerprint,dirty_generation,claimed_generation,
                    reserved_version_id,owner_generation,attempts,source_observation,last_source_observation,last_version_id,reason
                FROM fv_control.automatic_work WHERE claimed_generation IS NOT NULL ORDER BY path_key LIMIT @maximum
                """, connection, transaction);
            command.Parameters.AddWithValue("maximum", maximum);
            var result = new List<AutomaticPathWork>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(ReadAutomaticWork(reader));
            return result;
        }, ct);

    public Task ObserveAutomaticClaimAsync(VaultBinding binding, AutomaticPathWork work, string observation, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            RequireCurrentAutomaticScope(vault, work.ScopeFingerprint);
            if (string.IsNullOrWhiteSpace(observation) || observation.Length>1024) throw new ArgumentException("A bounded verified source observation is required.");
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET source_observation=@observation
                WHERE path_key=@key AND claimed_generation=@claimed AND reserved_version_id=@version AND owner_generation=@owner
                """, connection, transaction);
            AddAutomaticClaim(command, work);
            command.Parameters.AddWithValue("observation", observation);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomaticProtectionUnavailableException("The automatic claim changed before source observation.");
            return true;
        }, ct);

    /// <summary>Call only after verifying the exact reserved manifest, or a retained unchanged version.</summary>
    public Task CompleteAutomaticPathAsync(VaultBinding binding, AutomaticPathWork work, string versionId, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, _) =>
        {
            if (work.ReservedVersionId != versionId && work.LastVersionId != versionId)
                throw new AutomaticProtectionUnavailableException("The completion does not match its reserved or retained version identity.");
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET pending=dirty_generation>@claimed,claimed_generation=NULL,
                    reserved_version_id=NULL,owner_generation=NULL,last_source_observation=source_observation,
                    last_version_id=@result,source_observation=NULL,reason=NULL
                WHERE path_key=@key AND claimed_generation=@claimed AND reserved_version_id=@version AND owner_generation=@owner
                """, connection, transaction);
            AddAutomaticClaim(command, work);
            command.Parameters.AddWithValue("result", versionId);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomaticProtectionUnavailableException("The automatic result could not be associated with its original claim.");
            await using var status = new NpgsqlCommand("""
                UPDATE fv_control.automatic_policy SET last_version_id=@version,last_success_utc=now() WHERE singleton AND vault_id=@vault
                """, connection, transaction);
            status.Parameters.AddWithValue("version", versionId);
            status.Parameters.AddWithValue("vault", binding.Id.Value);
            if (await status.ExecuteNonQueryAsync(ct) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
            return true;
        }, ct);

    /// <summary>Only absence verified under the repository lease may release an interrupted identity.</summary>
    public Task RequeueAbsentAutomaticClaimAsync(VaultBinding binding, AutomaticPathWork work, string reason,
        TimeSpan retryDelay, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, _) =>
        {
            if (retryDelay<TimeSpan.Zero || retryDelay>TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(retryDelay));
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET claimed_generation=NULL,reserved_version_id=NULL,owner_generation=NULL,
                    source_observation=NULL,due_at_utc=@due,reason=@reason
                WHERE path_key=@key AND claimed_generation=@claimed AND reserved_version_id=@version AND owner_generation=@owner
                """, connection, transaction);
            AddAutomaticClaim(command, work);
            command.Parameters.AddWithValue("due", DateTimeOffset.UtcNow+retryDelay);
            command.Parameters.AddWithValue("reason", BoundedReason(reason));
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomaticProtectionUnavailableException("The interrupted automatic claim changed during reconciliation.");
            return true;
        }, ct);

    public Task SupersedeObsoleteAutomaticWorkAsync(VaultBinding binding, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            // Interrupted publications must have been reconciled before this mutation is admitted.
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET pending=false,reason='Superseded by the current saved source selection.'
                WHERE scope_fingerprint<>@fingerprint AND claimed_generation IS NULL;
                UPDATE fv_control.automatic_scopes SET scanned_generation=dirty_generation,reason='Superseded by current selection.'
                WHERE scope_fingerprint<>@fingerprint
                """, connection, transaction);
            command.Parameters.AddWithValue("fingerprint", AutomaticScopeFingerprint(vault));
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    private async Task<T> AutomaticMutationAsync<T>(VaultBinding binding,
        Func<NpgsqlConnection,NpgsqlTransaction,VaultCatalogueEntry,Task<T>> action, CancellationToken ct)
    {
        if (runtimeGeneration is null) throw new AutomaticProtectionUnavailableException("Automatic work requires an activated runtime owner.");
        await using var connection = await OpenVerifiedAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        await VerifyRuntimeFenceAsync(connection, transaction, ct);
        var vault = await LoadAsync(connection, transaction, binding.Id, ct);
        if (vault?.Binding != binding || !await HasAutomaticSchemaAsync(connection, transaction, ct))
            throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
        var result = await action(connection, transaction, vault);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static void RequireCurrentAutomaticScope(VaultCatalogueEntry vault, string fingerprint)
    {
        if (AutomaticScopeFingerprint(vault) != fingerprint)
            throw new AutomaticProtectionUnavailableException("Automatic source approval changed. Reconcile the current saved scope.");
    }

    private static string BoundedReason(string reason) => reason.Length<=2048 ? reason : reason[..2048];
    private static AutomaticPathWork ReadAutomaticWork(NpgsqlDataReader reader) => new(
        reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),
        reader.IsDBNull(6)?null:reader.GetInt64(6),reader.IsDBNull(7)?null:reader.GetString(7),
        reader.IsDBNull(8)?null:reader.GetGuid(8),reader.GetInt32(9),reader.IsDBNull(10)?null:reader.GetString(10),
        reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13));
    private static void AddAutomaticClaim(NpgsqlCommand command, AutomaticPathWork work)
    {
        if (work.ClaimedGeneration is null || work.ReservedVersionId is null || work.OwnerGeneration is null)
            throw new ArgumentException("A durable automatic claim is required.");
        command.Parameters.AddWithValue("key", work.PathKey);
        command.Parameters.AddWithValue("claimed", work.ClaimedGeneration.Value);
        command.Parameters.AddWithValue("version", work.ReservedVersionId);
        command.Parameters.AddWithValue("owner", work.OwnerGeneration.Value);
    }
}
