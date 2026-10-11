using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Configuration;
using FluxVault.Core.Policies;
using FluxVault.Abstractions.ChangeTracking;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

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
        DateTimeOffset dueAtUtc, string reason, AutomaticProtectionRuntimePolicy policy, CancellationToken ct = default,
        AutomaticPathObservation? observation = null) =>
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            policy.Validate();
            RequireCurrentAutomaticScope(vault, fingerprint);
            if (!ProtectionSourceSelection.TryFindFolder(vault.Configuration, sourcePath, out var folder))
                throw new AutomaticProtectionUnavailableException("The path is outside the saved automatic source selection.");
            var path = Path.GetFullPath(sourcePath);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.Id + "\0" + path.ToUpperInvariant())));
            AutomaticPathWork? previous;
            await using (var read = new NpgsqlCommand(AutomaticWorkSelect + " WHERE path_key=@key", connection, transaction))
            {
                read.Parameters.AddWithValue("key",key);
                await using var reader = await read.ExecuteReaderAsync(ct);
                previous = await reader.ReadAsync(ct) ? ReadAutomaticWork(reader) : null;
            }
            if (observation is not null && (string.IsNullOrWhiteSpace(observation.Value) || observation.Value.Length>1024))
                throw new ArgumentException("A bounded source observation is required.");
            if (observation is not null && previous?.ScopeFingerprint == fingerprint && previous.QueuedObservation == observation.Value &&
                (previous.ClaimedGeneration is not null || await IsAutomaticPathPendingAsync(connection,transaction,key,ct) || observation.RetainedResultVerified))
                return true;
            var observedAt = observation?.ObservedAtUtc.ToUniversalTime() ?? DateTimeOffset.UtcNow;
            var existingPending = previous?.ScopeFingerprint == fingerprint && await IsAutomaticPathPendingAsync(connection,transaction,key,ct);
            var firstEvent = existingPending && previous!.DirtyGeneration != previous.ClaimedGeneration
                ? previous.FirstEventUtc ?? observedAt : observedAt;
            var latestEvent = observedAt > (existingPending ? previous?.LatestEventUtc : null)
                ? observedAt : existingPending ? previous?.LatestEventUtc ?? observedAt : observedAt;
            if (observation is not null)
                dueAtUtc = CaptureCadenceScheduler.GetDueAtUtc(vault.Configuration.CaptureCadencePolicy,folder.ResourceProfile,
                    firstEvent,latestEvent,previous?.LastAttemptUtc);
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
                INSERT INTO fv_control.automatic_work(path_key,scope_id,root_path,source_path,scope_fingerprint,dirty_generation,due_at_utc,reason,
                    first_event_utc,latest_event_utc,queued_observation,is_deletion)
                VALUES(@key,@scope,@root,@path,@fingerprint,1,@due,@reason,@first,@latest,@observation,@deletion)
                ON CONFLICT(path_key) DO UPDATE SET dirty_generation=automatic_work.dirty_generation+1,
                    pending=true,scope_fingerprint=excluded.scope_fingerprint,root_path=excluded.root_path,
                    due_at_utc=excluded.due_at_utc,first_event_utc=excluded.first_event_utc,latest_event_utc=excluded.latest_event_utc,
                    queued_observation=excluded.queued_observation,is_deletion=excluded.is_deletion,
                    reason=excluded.reason
                """, connection, transaction);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("scope", folder.Id);
            command.Parameters.AddWithValue("root", folder.Path);
            command.Parameters.AddWithValue("path", path);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("due", dueAtUtc.ToUniversalTime());
            command.Parameters.AddWithValue("reason", BoundedReason(reason));
            command.Parameters.AddWithValue("first",firstEvent);
            command.Parameters.AddWithValue("latest",latestEvent);
            command.Parameters.AddWithValue("observation",(object?)observation?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("deletion",observation?.IsDeletion ?? false);
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
                    owner_generation=@owner,attempts=attempts+1,source_observation=NULL,last_attempt_utc=now(),
                    claimed_is_deletion=is_deletion,deleted_from_version_id=NULL
                WHERE path_key=(SELECT path_key FROM fv_control.automatic_work
                    WHERE pending AND claimed_generation IS NULL AND due_at_utc<=now() AND scope_fingerprint=@fingerprint
                    ORDER BY due_at_utc,path_key LIMIT 1 FOR UPDATE)
                RETURNING path_key,scope_id,root_path,source_path,scope_fingerprint,dirty_generation,claimed_generation,
                    reserved_version_id,owner_generation,attempts,source_observation,last_source_observation,last_version_id,reason,
                    first_event_utc,latest_event_utc,last_attempt_utc,queued_observation,claimed_is_deletion,deleted_from_version_id
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
            await using var command = new NpgsqlCommand(AutomaticWorkSelect + " WHERE claimed_generation IS NOT NULL ORDER BY path_key LIMIT @maximum", connection, transaction);
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
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            if (work.ReservedVersionId != versionId && work.LastVersionId != versionId)
                throw new AutomaticProtectionUnavailableException("The completion does not match its reserved or retained version identity.");
            long? mirrorOrdinal=null;
            if(work.ReservedVersionId==versionId && vault.Configuration.MirrorSet.EnabledNodes.Count>0)
            {
                await using var ordinal=new NpgsqlCommand($"SELECT outbox_id FROM \"{binding.MetadataNamespace}\".metadata_outbox WHERE device_id=@device AND operation_id=@version AND version_id=@version",connection,transaction);
                ordinal.Parameters.AddWithValue("device",vault.Configuration.Sync.LocalDevice.DeviceId);
                ordinal.Parameters.AddWithValue("version",versionId);
                mirrorOrdinal=await ordinal.ExecuteScalarAsync(ct) is long value ? value : throw new AutomaticProtectionUnavailableException("The primary publication has no verified durable mirror ordering record.");
            }
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET pending=dirty_generation>@claimed,claimed_generation=NULL,
                    reserved_version_id=NULL,owner_generation=NULL,last_source_observation=source_observation,
                    claimed_is_deletion=NULL,deleted_from_version_id=NULL,
                    last_version_id=@result,source_observation=NULL,reason=NULL,
                    mirror_after_outbox_id=CASE WHEN @mirror IS NULL THEN mirror_after_outbox_id ELSE coalesce(mirror_after_outbox_id,@mirror-1) END,
                    mirror_through_outbox_id=CASE WHEN @mirror IS NULL THEN mirror_through_outbox_id ELSE GREATEST(coalesce(mirror_through_outbox_id,@mirror),@mirror) END,
                    mirror_due_at_utc=CASE WHEN @mirror IS NULL THEN mirror_due_at_utc ELSE coalesce(mirror_due_at_utc,now()) END,
                    mirror_reason=CASE WHEN @mirror IS NULL THEN mirror_reason ELSE coalesce(mirror_reason,'Primary history is saved; mirror chunk replication is pending verification.') END
                WHERE path_key=@key AND claimed_generation=@claimed AND reserved_version_id=@version AND owner_generation=@owner
                """, connection, transaction);
            AddAutomaticClaim(command, work);
            command.Parameters.AddWithValue("result", versionId);
            command.Parameters.Add("mirror",NpgsqlDbType.Bigint).Value=(object?)mirrorOrdinal ?? DBNull.Value;
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AutomaticProtectionUnavailableException("The automatic result could not be associated with its original claim.");
            await using var status = new NpgsqlCommand("""
                UPDATE fv_control.automatic_policy SET last_version_id=@version,
                    last_success_utc=CASE WHEN @version=@reserved THEN now() ELSE last_success_utc END WHERE singleton AND vault_id=@vault
                """, connection, transaction);
            status.Parameters.AddWithValue("version", versionId);
            status.Parameters.AddWithValue("reserved", work.ReservedVersionId!);
            status.Parameters.AddWithValue("vault", binding.Id.Value);
            if (await status.ExecuteNonQueryAsync(ct) != 1) throw new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch);
            return true;
        }, ct);

    /// <summary>Only absence verified under the repository lease may release an interrupted identity.</summary>
    public Task RequeueAbsentAutomaticClaimAsync(VaultBinding binding, AutomaticPathWork work, string reason,
        TimeSpan retryDelay, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection, transaction, vault) =>
        {
            if (retryDelay<TimeSpan.Zero || retryDelay>TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(retryDelay));
            AutomaticPathWork current;
            await using (var read = new NpgsqlCommand(AutomaticWorkSelect+" WHERE path_key=@key",connection,transaction))
            {
                read.Parameters.AddWithValue("key",work.PathKey);
                await using var reader = await read.ExecuteReaderAsync(ct);
                current = await reader.ReadAsync(ct) ? ReadAutomaticWork(reader) : throw new AutomaticProtectionUnavailableException("The interrupted claim is missing.");
            }
            var profile = vault.Configuration.WatchedFolders.FirstOrDefault(folder=>folder.Id==work.ScopeId)?.ResourceProfile
                ?? FluxVault.Abstractions.Policies.ResourceProfile.Balanced;
            var cadenceDue = CaptureCadenceScheduler.GetDueAtUtc(vault.Configuration.CaptureCadencePolicy,profile,
                current.FirstEventUtc!.Value,current.LatestEventUtc!.Value,current.LastAttemptUtc);
            var retryDue = DateTimeOffset.UtcNow+retryDelay;
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET claimed_generation=NULL,reserved_version_id=NULL,owner_generation=NULL,
                    source_observation=NULL,claimed_is_deletion=NULL,deleted_from_version_id=NULL,due_at_utc=GREATEST(@due,due_at_utc),reason=@reason
                WHERE path_key=@key AND claimed_generation=@claimed AND reserved_version_id=@version AND owner_generation=@owner
                """, connection, transaction);
            AddAutomaticClaim(command, work);
            command.Parameters.AddWithValue("due",cadenceDue>retryDue ? cadenceDue : retryDue);
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

    public Task<AutomaticPathWork?> FindAutomaticPathAsync(VaultBinding binding, string scopeId, string path, CancellationToken ct = default) =>
        AutomaticMutationAsync<AutomaticPathWork?>(binding, async (connection,transaction,_) =>
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeId+"\0"+Path.GetFullPath(path).ToUpperInvariant())));
            await using var command = new NpgsqlCommand(AutomaticWorkSelect+" WHERE path_key=@key",connection,transaction);
            command.Parameters.AddWithValue("key",key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadAutomaticWork(reader) : null;
        },ct);

    public Task<IReadOnlyList<AutomaticPathWork>> ListAutomaticScopePathsAsync(VaultBinding binding, string scopeId,
        string fingerprint, string afterKey, int maximum, CancellationToken ct = default) =>
        AutomaticMutationAsync<IReadOnlyList<AutomaticPathWork>>(binding, async (connection,transaction,vault) =>
        {
            RequireCurrentAutomaticScope(vault,fingerprint);
            if (maximum is <1 or >256) throw new ArgumentOutOfRangeException(nameof(maximum));
            await using var command = new NpgsqlCommand(AutomaticWorkSelect+
                " WHERE scope_id=@scope AND scope_fingerprint=@fingerprint AND path_key>@after ORDER BY path_key LIMIT @maximum",connection,transaction);
            command.Parameters.AddWithValue("scope",scopeId); command.Parameters.AddWithValue("fingerprint",fingerprint);
            command.Parameters.AddWithValue("after",afterKey); command.Parameters.AddWithValue("maximum",maximum);
            var result = new List<AutomaticPathWork>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(ReadAutomaticWork(reader));
            return result;
        },ct);

    public Task ObserveAutomaticDeletionClaimAsync(VaultBinding binding, AutomaticPathWork work, string parentVersion, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection,transaction,vault) =>
        {
            RequireCurrentAutomaticScope(vault,work.ScopeFingerprint);
            if (!work.IsDeletion || !Guid.TryParseExact(parentVersion,"N",out var parent) || parent==Guid.Empty || parentVersion!=parent.ToString("N"))
                throw new ArgumentException("A deletion claim requires its exact retained parent identity.");
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET source_observation='missing',deleted_from_version_id=@parent
                WHERE path_key=@key AND claimed_generation=@claimed AND reserved_version_id=@version AND owner_generation=@owner
                    AND claimed_is_deletion
                """,connection,transaction);
            AddAutomaticClaim(command,work); command.Parameters.AddWithValue("parent",parentVersion);
            if (await command.ExecuteNonQueryAsync(ct)!=1) throw new AutomaticProtectionUnavailableException("The deletion claim changed before publication.");
            return true;
        },ct);

    public Task ExcludeAutomaticPathAsync(VaultBinding binding, string scopeId, string path, string fingerprint, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection,transaction,vault) =>
        {
            RequireCurrentAutomaticScope(vault,fingerprint);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeId+"\0"+Path.GetFullPath(path).ToUpperInvariant())));
            await using var command = new NpgsqlCommand("UPDATE fv_control.automatic_work SET pending=false,reason='Excluded by the current workload policy.' WHERE path_key=@key AND scope_fingerprint=@fingerprint AND claimed_generation IS NULL",connection,transaction);
            command.Parameters.AddWithValue("key",key); command.Parameters.AddWithValue("fingerprint",fingerprint);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        },ct);

    /// <summary>Confirmed absence with no live repository version has no new publication to acknowledge.</summary>
    public Task CompleteAutomaticNoChangeAsync(VaultBinding binding, AutomaticPathWork work, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection,transaction,_) =>
        {
            if (!work.IsDeletion) throw new ArgumentException("Only a verified no-op deletion uses this completion.");
            await using var command = new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET pending=dirty_generation>@claimed,claimed_generation=NULL,
                    reserved_version_id=NULL,owner_generation=NULL,source_observation=NULL,last_source_observation='missing',
                    claimed_is_deletion=NULL,deleted_from_version_id=NULL,reason=NULL
                WHERE path_key=@key AND claimed_generation=@claimed AND reserved_version_id=@version AND owner_generation=@owner
                """,connection,transaction);
            AddAutomaticClaim(command,work);
            if (await command.ExecuteNonQueryAsync(ct)!=1) throw new AutomaticProtectionUnavailableException("The no-op deletion claim changed.");
            return true;
        },ct);

    public Task<IReadOnlyList<UsnJournalCheckpoint>> ReadAutomaticJournalCheckpointsAsync(VaultBinding binding, string fingerprint, CancellationToken ct = default) =>
        AutomaticMutationAsync<IReadOnlyList<UsnJournalCheckpoint>>(binding, async (connection,transaction,vault) =>
        {
            RequireCurrentAutomaticScope(vault,fingerprint);
            await using var command = new NpgsqlCommand("SELECT journal_checkpoint::text FROM fv_control.automatic_scopes WHERE scope_fingerprint=@fingerprint AND journal_checkpoint IS NOT NULL",connection,transaction);
            command.Parameters.AddWithValue("fingerprint",fingerprint);
            var result = new List<UsnJournalCheckpoint>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<UsnJournalCheckpoint>(reader.GetString(0),JsonOptions)
                ?? throw new AutomaticProtectionUnavailableException("The saved journal checkpoint is invalid."));
            return result;
        },ct);

    /// <summary>Unresolved journal names are covered by durable dirty scopes, atomically with cursor advancement.</summary>
    public Task AdmitAutomaticJournalCoverageAsync(VaultBinding binding, string fingerprint,
        IReadOnlyList<UsnJournalCheckpoint> checkpoints, string reason, CancellationToken ct = default) =>
        AutomaticMutationAsync(binding, async (connection,transaction,vault) =>
        {
            RequireCurrentAutomaticScope(vault,fingerprint);
            foreach (var checkpoint in checkpoints)
            {
                var representative = vault.Configuration.WatchedFolders.SingleOrDefault(folder=>folder.IsEnabled && folder.Id==checkpoint.WatchedFolderId)
                    ?? throw new AutomaticProtectionUnavailableException("The journal scope changed.");
                if (checkpoint.NextUsn<0 || !string.Equals(checkpoint.VolumeRoot,Path.GetPathRoot(Path.GetFullPath(representative.Path)),StringComparison.OrdinalIgnoreCase))
                    throw new AutomaticProtectionUnavailableException("The journal checkpoint does not match its source volume.");
                foreach (var folder in vault.Configuration.WatchedFolders.Where(folder=>folder.IsEnabled &&
                    string.Equals(Path.GetPathRoot(Path.GetFullPath(folder.Path)),checkpoint.VolumeRoot,StringComparison.OrdinalIgnoreCase)))
                {
                    var covered = checkpoint with { WatchedFolderId=folder.Id };
                    await using var command = new NpgsqlCommand("""
                        INSERT INTO fv_control.automatic_scopes(scope_id,root_path,scope_fingerprint,dirty_generation,reason,journal_checkpoint)
                        VALUES(@scope,@root,@fingerprint,1,@reason,@checkpoint::jsonb)
                        ON CONFLICT(scope_id) DO UPDATE SET root_path=excluded.root_path,scope_fingerprint=excluded.scope_fingerprint,
                            dirty_generation=automatic_scopes.dirty_generation+1,reason=excluded.reason,journal_checkpoint=excluded.journal_checkpoint
                        WHERE automatic_scopes.scope_fingerprint<>excluded.scope_fingerprint OR
                            automatic_scopes.journal_checkpoint IS NULL OR
                            automatic_scopes.journal_checkpoint->>'journalId'<>excluded.journal_checkpoint->>'journalId' OR
                            automatic_scopes.journal_checkpoint->>'nextUsn'<>excluded.journal_checkpoint->>'nextUsn' OR
                            automatic_scopes.journal_checkpoint->>'reasonMask'<>excluded.journal_checkpoint->>'reasonMask'
                        """,connection,transaction);
                    command.Parameters.AddWithValue("scope",folder.Id); command.Parameters.AddWithValue("root",folder.Path);
                    command.Parameters.AddWithValue("fingerprint",fingerprint); command.Parameters.AddWithValue("reason",BoundedReason(reason));
                    command.Parameters.AddWithValue("checkpoint",JsonSerializer.Serialize(covered,JsonOptions));
                    await command.ExecuteNonQueryAsync(ct);
                }
            }
            return true;
        },ct);

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
        reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13),
        reader.GetFieldValue<DateTimeOffset>(14),reader.GetFieldValue<DateTimeOffset>(15),reader.IsDBNull(16)?null:reader.GetFieldValue<DateTimeOffset>(16),
        reader.IsDBNull(17)?null:reader.GetString(17),reader.GetBoolean(18),reader.IsDBNull(19)?null:reader.GetString(19));
    private const string AutomaticWorkSelect = """
        SELECT path_key,scope_id,root_path,source_path,scope_fingerprint,dirty_generation,claimed_generation,
            reserved_version_id,owner_generation,attempts,source_observation,last_source_observation,last_version_id,reason,
            first_event_utc,latest_event_utc,last_attempt_utc,queued_observation,coalesce(claimed_is_deletion,is_deletion),deleted_from_version_id
        FROM fv_control.automatic_work
        """;
    private static async Task<bool> IsAutomaticPathPendingAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string key,CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pending FROM fv_control.automatic_work WHERE path_key=@key",connection,transaction);
        command.Parameters.AddWithValue("key",key);
        return await command.ExecuteScalarAsync(ct) is true;
    }
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
