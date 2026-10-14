using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Abstractions.Storage;
using Npgsql;

namespace FluxVault.TestHost;

internal static class VaultAutomaticProtectionProbe
{
    internal static async Task RunAsync(PostgreSqlVaultCatalogue original, NpgsqlDataSource source,
        FluxVaultCallerContext creator, FluxVaultCallerContext administrator, FluxVaultCallerContext other, List<string> checks)
    {
        var baseline = (await original.AdmitAsync(creator,FluxVaultIpcRequest.GetStatus())).Vault;
        var binding = baseline.Binding;
        await using var store = new PostgreSqlVaultCatalogue(original.Endpoint);
        await store.EnsureAutomaticSchemaAsync(binding);
        await store.EnsureAutomaticSchemaAsync(binding);
        var defaults = await store.ReadAutomaticStateAsync(binding);
        Check(!defaults.Policy.Enabled && defaults.RequiresCreatorApproval && defaults.PendingCount==0,
            "automatic extension migration is idempotent and defaults off without touching base revision/configuration");
        Check(defaults.Vault.Revision==baseline.Revision && JsonSerializer.Serialize(defaults.Vault.Configuration)==JsonSerializer.Serialize(baseline.Configuration),
            "automatic extension preserves predecessor-readable base configuration and identity");
        await using var wrong = new PostgreSqlVaultCatalogue(store.Endpoint with { InstanceId=Guid.NewGuid() });
        await Reject(() => wrong.EnsureAutomaticSchemaAsync(binding), "automatic migration refuses wrong installation identity", VaultCatalogueFailure.IdentityMismatch);
        await VaultAutomaticCadenceProbe.VerifyMigrationAsync(store,source,binding,checks);
        await using (var storage = await RepositoryLeaseSet.AcquireAsync(binding.RepositoryPath,[],MirrorLeaseMode.None,CancellationToken.None,binding:binding,provision:true)) { }
        await using var owner = await store.OpenAutomaticRuntimeOwnerAsync(binding,new());
        await store.ActivateAutomaticRuntimeAsync(owner);
        await using var metadata=new PostgreSqlRepositoryMetadataStore(binding,baseline.Configuration.Sync.LocalDevice.DeviceId);
        await metadata.ProvisionVaultAsync();
        await Reject(() => original.OpenAutomaticRuntimeOwnerAsync(binding,new()), "exclusive automatic owner refuses a concurrent generation");
        var root = Path.Combine(Path.GetDirectoryName(binding.RepositoryPath)!,"automatic-source");
        var saved = await store.SaveConfigurationAsync(creator, Request(baseline,FluxVaultIpcCommand.SaveConfiguration) with
        {
            Configuration=baseline.Configuration with { IsEnabled=true, SelectionRules=[],ExclusionRules=[],
                WatchedFolders=[new("automatic",root,true,["*"],[],CompressionPreference.Off,ResourceProfile.Fast,true)] }
        });
        var state = await store.ReadAutomaticStateAsync(binding);
        var enable = FluxVaultIpcRequest.SetAutomaticProtection(true) with
            { VaultId=binding.Id, ExpectedVaultRevision=state.Vault.Revision, OperationId=Guid.NewGuid() };
        var enabled = await store.AdmitAsync(creator,enable);
        Check(enabled.Receipt?.Response?.Success==true && enabled.Vault.Revision==saved.Vault.Revision+1,
            "creator automatic preference and completed original receipt share one durable revision");
        state=await store.ReadAutomaticStateAsync(binding);
        Check(state.Policy.Enabled && !state.RequiresCreatorApproval,"creator enable approves the authoritative source scope");
        await VaultAutomaticCadenceProbe.VerifyAsync(store,source,creator,state,checks);
        state=await store.ReadAutomaticStateAsync(binding);
        await VaultAutomaticMirrorProbe.VerifyAsync(store,source,metadata,state,checks);
        await store.MarkAutomaticScopeDirtyAsync(binding,"automatic",state.ScopeFingerprint,"fixture restart coverage");
        var marker=(await store.ListAutomaticScopesAsync(binding)).Single();
        var path=Path.Combine(root,"saved.txt");
        var observed = new AutomaticPathObservation("native-observation-one", DateTimeOffset.UtcNow.AddMinutes(-1));
        Check(await store.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,DateTimeOffset.UtcNow.AddSeconds(-1),"fixture first save",new(),observation:observed),
            "automatic change is durably admitted before dispatch");
        await store.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,DateTimeOffset.UtcNow.AddSeconds(-1),"duplicate scan",new(),observation:observed);
        var first=await store.ClaimAutomaticPathAsync(binding) ?? throw new InvalidOperationException("Automatic claim absent.");
        Check(first.DirtyGeneration==1, "duplicate pending observations do not invent a generation or postpone capture");
        Check(first.ReservedVersionId is { Length:32 } && first.OwnerGeneration==owner.Generation,
            "dispatch reserves immutable capture identity before source work");
        await store.ObserveAutomaticClaimAsync(binding,first,"native-observation-one");
        await store.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,DateTimeOffset.UtcNow.AddSeconds(-1),"fixture newer save",new());
        await metadata.RecordVersionAsync(new(first.ReservedVersionId!,first.ScopeId,first.SourcePath,DateTimeOffset.UtcNow,CaptureConsistency.CrashConsistent,0,[],VaultId:binding.Id));
        await store.CompleteAutomaticPathAsync(binding,first,first.ReservedVersionId!);
        Check((await store.ReadAutomaticStateAsync(binding)).PendingCount==1,
            "completing one claimed generation preserves a newer save as pending");
        var second=await store.ClaimAutomaticPathAsync(binding) ?? throw new InvalidOperationException("Newer save lost.");
        Check(second.DirtyGeneration>first.ClaimedGeneration && second.ReservedVersionId!=first.ReservedVersionId,
            "newer durable generation receives its own immutable capture identity");
        await store.MarkAutomaticScopeDirtyAsync(binding,"automatic",state.ScopeFingerprint,"fixture overflow during scan");
        await store.CompleteAutomaticScopeAsync(binding,marker);
        Check((await store.ListAutomaticScopesAsync(binding)).Count==1,
            "completing an older scan cannot acknowledge newer dirty coverage");
        var grant=await store.SetAccessAsync(administrator,Request((await store.ReadAutomaticStateAsync(binding)).Vault,FluxVaultIpcCommand.SetVaultAccess) with
            { AccessGrants=[new(other.UserSid,VaultPermission.ManageProtection)] });
        await Reject(() => store.AdmitAsync(other,FluxVaultIpcRequest.SetAutomaticProtection(true) with
            { VaultId=binding.Id,ExpectedVaultRevision=grant.Vault.Revision,OperationId=Guid.NewGuid() }),
            "ManageProtection grant cannot approve creator-session automatic source access", VaultCatalogueFailure.Denied);
        var widened=await store.SaveConfigurationAsync(other,Request(grant.Vault,FluxVaultIpcCommand.SaveConfiguration) with
            { Configuration=grant.Vault.Configuration with { WatchedFolders=[grant.Vault.Configuration.WatchedFolders[0] with { Path=root+"-other" }] } });
        Check((await store.ReadAutomaticStateAsync(binding)).RequiresCreatorApproval,
            "granted source change invalidates automatic creator consent");
        var replay=await store.AdmitAsync(creator,enable);
        Check(replay.IsReplay && (await store.ReadAutomaticStateAsync(binding)).RequiresCreatorApproval,
            "replaying an earlier enable receipt never renews consent for a changed source scope");
        await store.RequireAutomaticApprovalAsync(binding);
        Check((await store.ReadAutomaticStateAsync(binding)).Policy.Enabled,
            "rollback approval invalidation preserves the automatic preference and interrupted work");
        await using var takeover = new PostgreSqlVaultCatalogue(original.Endpoint);
        await using (var connection=await source.OpenConnectionAsync())
        await using (var terminate=new NpgsqlCommand("SELECT pg_terminate_backend(@pid)",connection))
        {
            terminate.Parameters.AddWithValue("pid",owner.BackendProcessId);
            Check(await terminate.ExecuteScalarAsync() is true,"owned fixture terminates only its dedicated automatic owner session");
        }
        await using var replacement=await takeover.OpenAutomaticRuntimeOwnerAsync(binding,new());
        await using (var previousLease=await RepositoryLeaseSet.AcquireAsync(binding.RepositoryPath,[],MirrorLeaseMode.None,CancellationToken.None,binding:binding))
        {
            var activation=takeover.ActivateAutomaticRuntimeAsync(replacement);
            try
            {
                await Task.Delay(100);
                Check(!activation.IsCompleted,"replacement generation waits for the previous repository lease to drain");
            }
            finally { await previousLease.DisposeAsync(); await activation; }
        }
        await Reject(() => store.SaveConfigurationAsync(creator,Request(widened.Vault,FluxVaultIpcCommand.SaveConfiguration) with { Configuration=widened.Vault.Configuration }),
            "lost owner cannot admit a fresh catalogue mutation after takeover");
        var interrupted=(await takeover.ListAutomaticClaimsAsync(binding,32)).Single();
        Check(interrupted.ReservedVersionId==second.ReservedVersionId && interrupted.OwnerGeneration==owner.Generation,
            "restart retains the exact interrupted capture identity and original owner for reconciliation");
        await takeover.RequeueAbsentAutomaticClaimAsync(binding,interrupted,"fixture proves reserved result absent",TimeSpan.Zero);
        await takeover.SupersedeObsoleteAutomaticWorkAsync(binding);
        Check((await takeover.ReadAutomaticStateAsync(binding)).PendingCount==0,
            "obsolete queued source scope is retained as superseded without privileged recapture");
        var restored=await takeover.SaveConfigurationAsync(creator,Request((await takeover.ReadAutomaticStateAsync(binding)).Vault,FluxVaultIpcCommand.SaveConfiguration) with
            { Configuration=baseline.Configuration });
        await takeover.SetAccessAsync(administrator,Request(restored.Vault,FluxVaultIpcCommand.SetVaultAccess) with { AccessGrants=baseline.Access.Grants });
        var final=await takeover.ReadAutomaticStateAsync(binding);
        Check(JsonSerializer.Serialize(final.Vault.Configuration)==JsonSerializer.Serialize(baseline.Configuration),
            "automatic real-store contracts restore the complete original configuration");
        await using (var connection=await source.OpenConnectionAsync())
        await using (var persistence=new NpgsqlCommand("ALTER TABLE fv_control.automatic_work SET UNLOGGED",connection))
        {
            await persistence.ExecuteNonQueryAsync();
            try
            {
                var recovery=await takeover.AdmitAsync(creator,Request(final.Vault,FluxVaultIpcCommand.RestoreVersion) with
                    { VersionId=Guid.NewGuid().ToString("N"),OutputPath=Path.Combine(root,"manual-recovery.txt") });
                Check(recovery.Receipt?.State==VaultOperationState.Admitted,
                    "unavailable automatic extension preserves manual recovery admission");
                await takeover.CompleteAsync(recovery.Receipt!,FluxVaultIpcResponse.Ok());
                await Reject(() => takeover.ReadAutomaticStateAsync(binding),
                    "automatic work refuses unlogged persistence rather than claiming durable pending work");
            }
            finally
            {
                persistence.CommandText="ALTER TABLE fv_control.automatic_work SET LOGGED";
                await persistence.ExecuteNonQueryAsync();
            }
        }
        await using (var connection=await source.OpenConnectionAsync())
        await using (var identity=new NpgsqlCommand("""
            CREATE FUNCTION fv_control.automatic_identity_tripwire() RETURNS integer LANGUAGE plpgsql AS $tripwire$
            BEGIN RAISE EXCEPTION 'Unexpected automatic identity view evaluation'; END;
            $tripwire$;
            REVOKE ALL ON FUNCTION fv_control.automatic_identity_tripwire() FROM PUBLIC;
            ALTER TABLE fv_control.automatic_identity RENAME TO automatic_identity_fixture;
            CREATE VIEW fv_control.automatic_identity AS
                SELECT true AS singleton,fv_control.automatic_identity_tripwire() AS schema_version;
            """,connection))
        {
            await identity.ExecuteNonQueryAsync();
            try
            {
                await Reject(() => takeover.ReadAutomaticStateAsync(binding),
                    "automatic identity relation is verified before an unexpected view can be evaluated");
            }
            finally
            {
                identity.CommandText="""
                    DROP VIEW fv_control.automatic_identity;
                    ALTER TABLE fv_control.automatic_identity_fixture RENAME TO automatic_identity;
                    DROP FUNCTION fv_control.automatic_identity_tripwire();
                    """;
                await identity.ExecuteNonQueryAsync();
            }
        }
        await using(var cleanupConnection=await source.OpenConnectionAsync())
        await using(var cleanupMetadata=new NpgsqlCommand($"DROP SCHEMA \"{binding.MetadataNamespace}\" CASCADE",cleanupConnection))
            await cleanupMetadata.ExecuteNonQueryAsync(); // This probe created this empty-before-test namespace; the following metadata fixture starts fresh.
        void Check(bool condition,string name) { if(!condition)throw new InvalidOperationException("Automatic catalogue contract failed: "+name);checks.Add(name); }
        async Task Reject(Func<Task> action,string name,VaultCatalogueFailure? failure=null)
        {
            try { await action(); }
            catch(VaultCatalogueException exception) when(exception.Failure==failure) { checks.Add(name);return; }
            catch(AutomaticProtectionUnavailableException) when(failure is null) { checks.Add(name);return; }
            throw new InvalidOperationException("Automatic catalogue contract did not refuse: "+name);
        }
    }
    private static FluxVaultIpcRequest Request(VaultCatalogueEntry vault,FluxVaultIpcCommand command) =>
        new(command,null,null,null,null,VaultId:vault.Binding.Id,ExpectedVaultRevision:vault.Revision,OperationId:Guid.NewGuid());
}
