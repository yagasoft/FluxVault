using Npgsql;

namespace FluxVault.Core.Security;

public sealed partial class PostgreSqlVaultCatalogue
{
    public Task<IReadOnlyList<AutomaticMirrorWork>> ListAutomaticMirrorWorkAsync(VaultBinding binding,int maximum,CancellationToken ct=default) =>
        AutomaticMutationAsync<IReadOnlyList<AutomaticMirrorWork>>(binding,async(connection,transaction,_)=>
        {
            if(maximum is <1 or >256)throw new ArgumentOutOfRangeException(nameof(maximum));
            await using var command=new NpgsqlCommand("""
                SELECT path_key,scope_id,source_path,mirror_after_outbox_id,mirror_through_outbox_id
                FROM fv_control.automatic_work WHERE mirror_through_outbox_id IS NOT NULL AND mirror_due_at_utc<=now()
                ORDER BY mirror_due_at_utc,path_key LIMIT @maximum
                """,connection,transaction);
            command.Parameters.AddWithValue("maximum",maximum);
            var result=new List<AutomaticMirrorWork>();
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),reader.GetInt64(4)));
            return result;
        },ct);

    public Task<AutomaticMirrorVersion?> FindNextAutomaticMirrorVersionAsync(VaultBinding binding,AutomaticMirrorWork work,CancellationToken ct=default) =>
        AutomaticMutationAsync<AutomaticMirrorVersion?>(binding,async(connection,transaction,_)=>
        {
            // Outbox ordinals provide ordering only. Retained version metadata supplies authority.
            await using var command=new NpgsqlCommand($"""
                SELECT o.outbox_id,v.version_id FROM "{binding.MetadataNamespace}".versions v
                JOIN "{binding.MetadataNamespace}".metadata_outbox o ON o.version_id=v.version_id
                WHERE v.source_path=@path AND v.watched_folder_id=@scope AND v.entry_kind='File'
                    AND o.outbox_id>@after AND o.outbox_id<=@through
                ORDER BY o.outbox_id LIMIT 1
                """,connection,transaction);
            command.Parameters.AddWithValue("path",work.SourcePath);command.Parameters.AddWithValue("scope",work.ScopeId);
            command.Parameters.AddWithValue("after",work.AfterOutboxId);command.Parameters.AddWithValue("through",work.ThroughOutboxId);
            await using var reader=await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)?new(reader.GetInt64(0),reader.GetString(1)):null;
        },ct);

    /// <summary>Call only under the repository lease after verified replication or authoritative absence.</summary>
    public Task AdvanceAutomaticMirrorWorkAsync(VaultBinding binding,AutomaticMirrorWork work,long through,CancellationToken ct=default) =>
        AutomaticMutationAsync(binding,async(connection,transaction,_)=>
        {
            if(through<=work.AfterOutboxId || through>work.ThroughOutboxId)throw new ArgumentOutOfRangeException(nameof(through));
            await using var command=new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET
                    mirror_after_outbox_id=CASE WHEN mirror_through_outbox_id<=@through THEN NULL ELSE @through END,
                    mirror_through_outbox_id=CASE WHEN mirror_through_outbox_id<=@through THEN NULL ELSE mirror_through_outbox_id END,
                    mirror_due_at_utc=CASE WHEN mirror_through_outbox_id<=@through THEN NULL ELSE now() END,
                    mirror_reason=CASE WHEN mirror_through_outbox_id<=@through THEN NULL ELSE 'Primary history is saved; older mirror chunk replication remains pending.' END
                WHERE path_key=@key AND mirror_after_outbox_id=@after AND mirror_through_outbox_id>=@through
                """,connection,transaction);
            command.Parameters.AddWithValue("key",work.PathKey);command.Parameters.AddWithValue("after",work.AfterOutboxId);
            command.Parameters.AddWithValue("through",through);
            if(await command.ExecuteNonQueryAsync(ct)!=1)throw new AutomaticProtectionUnavailableException("The mirror retry cursor changed; newer coverage was preserved.");
            return true;
        },ct);

    public Task DelayAutomaticMirrorWorkAsync(VaultBinding binding,AutomaticMirrorWork work,string reason,TimeSpan delay,CancellationToken ct=default) =>
        AutomaticMutationAsync(binding,async(connection,transaction,_)=>
        {
            if(delay<=TimeSpan.Zero || delay>TimeSpan.FromMinutes(5))throw new ArgumentOutOfRangeException(nameof(delay));
            await using var command=new NpgsqlCommand("""
                UPDATE fv_control.automatic_work SET mirror_due_at_utc=@due,mirror_reason=@reason
                WHERE path_key=@key AND mirror_after_outbox_id=@after AND mirror_through_outbox_id>=@through
                """,connection,transaction);
            command.Parameters.AddWithValue("key",work.PathKey);command.Parameters.AddWithValue("after",work.AfterOutboxId);
            command.Parameters.AddWithValue("through",work.ThroughOutboxId);command.Parameters.AddWithValue("due",DateTimeOffset.UtcNow+delay);
            command.Parameters.AddWithValue("reason",BoundedReason(reason));
            await command.ExecuteNonQueryAsync(ct);return true;
        },ct);
}
