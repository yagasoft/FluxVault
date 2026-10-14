using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Security;
using Npgsql;
using System.IO;

namespace FluxVault.TestHost;

internal static class VaultAutomaticCadenceProbe
{
    internal static async Task VerifyMigrationAsync(PostgreSqlVaultCatalogue store, NpgsqlDataSource source, VaultBinding binding, List<string> checks)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var prepare = new NpgsqlCommand("""
            INSERT INTO fv_control.automatic_work(path_key,scope_id,root_path,source_path,scope_fingerprint,dirty_generation,due_at_utc,
                claimed_generation,reserved_version_id,owner_generation)
            VALUES(repeat('A',64),'legacy','C:\legacy','C:\legacy\pending.txt',repeat('B',64),1,now()-interval '1 minute',1,
                '00000000000000000000000000000001','00000000-0000-0000-0000-000000000001');
            ALTER TABLE fv_control.automatic_work DROP COLUMN first_event_utc,DROP COLUMN latest_event_utc,
                DROP COLUMN last_attempt_utc,DROP COLUMN queued_observation,DROP COLUMN is_deletion,
                DROP COLUMN claimed_is_deletion,DROP COLUMN deleted_from_version_id,
                DROP COLUMN mirror_after_outbox_id,DROP COLUMN mirror_through_outbox_id,
                DROP COLUMN mirror_due_at_utc,DROP COLUMN mirror_reason;
            UPDATE fv_control.automatic_identity SET schema_version=1 WHERE singleton;
            """,connection);
        await prepare.ExecuteNonQueryAsync();
        try
        {
            await store.EnsureAutomaticSchemaAsync(binding);
            throw new InvalidOperationException("Conflicting migration unexpectedly succeeded.");
        }
        catch (PostgresException exception) when (exception.SqlState==PostgresErrorCodes.DuplicateColumn) { }
        await using var verify = new NpgsqlCommand("""
            SELECT (SELECT schema_version FROM fv_control.automatic_identity WHERE singleton)=1
                AND NOT EXISTS(SELECT FROM information_schema.columns WHERE table_schema='fv_control'
                    AND table_name='automatic_work' AND column_name='first_event_utc')
                AND EXISTS(SELECT FROM fv_control.automatic_work WHERE path_key=repeat('A',64) AND pending AND claimed_generation=1)
            """,connection);
        Check(await verify.ExecuteScalarAsync() is true,"failed v1 extension migration rolls back all new work columns and retains the interrupted claim");
        prepare.CommandText="ALTER TABLE fv_control.automatic_scopes DROP COLUMN journal_checkpoint";
        await prepare.ExecuteNonQueryAsync();
        await store.EnsureAutomaticSchemaAsync(binding); await store.EnsureAutomaticSchemaAsync(binding);
        verify.CommandText="""
            SELECT (SELECT schema_version FROM fv_control.automatic_identity WHERE singleton)=2
                AND EXISTS(SELECT FROM fv_control.automatic_work WHERE path_key=repeat('A',64) AND pending
                    AND claimed_generation=1 AND NOT claimed_is_deletion AND queued_observation IS NULL
                    AND first_event_utc<=due_at_utc AND latest_event_utc<=due_at_utc)
            """;
        Check(await verify.ExecuteScalarAsync() is true,"v1 to v2 migration is idempotent and preserves conservative pending/claimed work without invented observations");
        prepare.CommandText="DELETE FROM fv_control.automatic_work WHERE path_key=repeat('A',64)";
        await prepare.ExecuteNonQueryAsync();
        void Check(bool condition,string name) { if(!condition)throw new InvalidOperationException(name);checks.Add(name); }
    }

    internal static async Task VerifyAsync(PostgreSqlVaultCatalogue store,NpgsqlDataSource source,
        FluxVaultCallerContext creator,AutomaticCatalogueState state,List<string> checks)
    {
        var binding=state.Vault.Binding;
        var folder=state.Vault.Configuration.WatchedFolders.Single();
        var path=Path.Combine(folder.Path,"cadence-fixture.txt");
        var now=DateTimeOffset.UtcNow;
        var first=new AutomaticPathObservation("cadence-one",now.AddSeconds(-80));
        await store.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,now,"first hot event",new(),observation:first);
        var latest=first with { Value="cadence-two",ObservedAtUtc=now.AddSeconds(-1) };
        await store.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,now,"later hot event",new(),observation:latest);
        var observed=(await store.FindAutomaticPathAsync(binding,folder.Id,path))!;
        Check(observed.DirtyGeneration==2 && Near(observed.FirstEventUtc,first.ObservedAtUtc) && Near(observed.LatestEventUtc,latest.ObservedAtUtc),
            "real store preserves the original hot-file event while updating its latest observation");
        await store.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,now,"duplicate at restart",new(),observation:latest with { ObservedAtUtc=now.AddHours(1) });
        var duplicate=(await store.FindAutomaticPathAsync(binding,folder.Id,path))!;
        Check(duplicate.DirtyGeneration==2 && duplicate.FirstEventUtc==observed.FirstEventUtc && duplicate.LatestEventUtc==observed.LatestEventUtc,
            "duplicate scan observations preserve durable hot and quiet deadlines across repeated admission");
        var claim=await store.ClaimAutomaticPathAsync(binding) ?? throw new InvalidOperationException("Hot-file deadline was not eligible.");
        Check(claim.PathKey==observed.PathKey && claim.LastAttemptUtc is not null,"dispatch records its attempt before source effects");
        var newer=first with { Value="cadence-three",ObservedAtUtc=DateTimeOffset.UtcNow.AddSeconds(20) };
        await store.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,now,"newer while running",new(),observation:newer);
        var current=(await store.FindAutomaticPathAsync(binding,folder.Id,path))!;
        Check(current.DirtyGeneration==claim.DirtyGeneration+1 && Near(current.FirstEventUtc,newer.ObservedAtUtc),
            "a newer observation during a claim begins its own durable event window");
        await store.RequeueAbsentAutomaticClaimAsync(binding,claim,"verified absent fixture attempt",TimeSpan.Zero);
        await using var connection=await source.OpenConnectionAsync();
        await using var timing=new NpgsqlCommand("SELECT due_at_utc FROM fv_control.automatic_work WHERE path_key=@key",connection);
        timing.Parameters.AddWithValue("key",claim.PathKey);
        var due=new DateTimeOffset((DateTime)(await timing.ExecuteScalarAsync())!);
        Check(due>=newer.ObservedAtUtc+state.Vault.Configuration.CaptureCadencePolicy.GetDebounce(folder.ResourceProfile)-TimeSpan.FromMilliseconds(1) &&
            due>=claim.LastAttemptUtc+state.Vault.Configuration.CaptureCadencePolicy.MinimumSameFileCaptureInterval-TimeSpan.FromMilliseconds(1),
            "interrupted requeue preserves both newer quiet timing and the minimum same-file interval");
        Check(await store.ClaimAutomaticPathAsync(binding) is null,"restart cannot claim work before its durable cadence deadline");
        await using (var remove=new NpgsqlCommand("DELETE FROM fv_control.automatic_work WHERE path_key=@key",connection))
        { remove.Parameters.AddWithValue("key",claim.PathKey);await remove.ExecuteNonQueryAsync(); }

        var before=state.Vault;
        var sibling=folder with { Id="journal-sibling",Path=folder.Path+"-sibling" };
        var changed=await store.SaveConfigurationAsync(creator,FluxVaultIpcRequest.SaveConfiguration(before.Configuration with { WatchedFolders=[folder,sibling] }) with
            { VaultId=binding.Id,ExpectedVaultRevision=before.Revision,OperationId=Guid.NewGuid() });
        var coverage=await store.ReadAutomaticStateAsync(binding);
        var checkpoint=new UsnJournalCheckpoint(folder.Id,Path.GetPathRoot(Path.GetFullPath(folder.Path))!,1,100,uint.MaxValue,DateTimeOffset.UtcNow);
        await store.AdmitAutomaticJournalCoverageAsync(binding,coverage.ScopeFingerprint,[checkpoint],"unresolved names still covered");
        var dirty=await store.ListAutomaticScopesAsync(binding);
        Check(dirty.Count==2 && (await store.ReadAutomaticJournalCheckpointsAsync(binding,coverage.ScopeFingerprint)).Count==2,
            "one volume cursor atomically leaves every saved scope dirty including unresolved or moved records");
        foreach(var scope in dirty)await store.CompleteAutomaticScopeAsync(binding,scope);
        await store.AdmitAutomaticJournalCoverageAsync(binding,coverage.ScopeFingerprint,[checkpoint],"same interval repeated");
        Check((await store.ListAutomaticScopesAsync(binding)).Count==0,"replayed journal checkpoint does not invent new coverage work");
        try
        {
            await store.AdmitAutomaticJournalCoverageAsync(binding,coverage.ScopeFingerprint,
                [checkpoint with { NextUsn=200 },checkpoint with { WatchedFolderId="unknown",NextUsn=200 }],"transaction failure");
            throw new InvalidOperationException("Invalid journal scope unexpectedly admitted.");
        }
        catch(AutomaticProtectionUnavailableException) { }
        Check((await store.ListAutomaticScopesAsync(binding)).Count==0 &&
            (await store.ReadAutomaticJournalCheckpointsAsync(binding,coverage.ScopeFingerprint)).All(item=>item.NextUsn==100),
            "failed coverage transaction advances neither cursor nor dirty markers");
        await store.AdmitAutomaticJournalCoverageAsync(binding,coverage.ScopeFingerprint,[checkpoint with { JournalId=2,NextUsn=1 }],"journal reset");
        Check((await store.ListAutomaticScopesAsync(binding)).Count==2,"journal replacement/reset retains full dirty coverage rather than treating lower USN as completion");
        await store.SaveConfigurationAsync(creator,FluxVaultIpcRequest.SaveConfiguration(before.Configuration) with
            { VaultId=binding.Id,ExpectedVaultRevision=changed.Vault.Revision,OperationId=Guid.NewGuid() });
        await store.SupersedeObsoleteAutomaticWorkAsync(binding);
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        static bool Near(DateTimeOffset? actual,DateTimeOffset expected)=>actual is{} value && Math.Abs((value-expected).TotalMilliseconds)<1;
    }
}
