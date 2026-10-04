using System.IO;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using FluxVault.Testing;
using Npgsql;

namespace FluxVault.TestHost;

/// <summary>Repeatable real-store contracts, confined to the explicitly owned SSPI fixture database.</summary>
internal static class VaultCatalogueProbe
{
    internal static async Task<object> RunAsync(NpgsqlDataSource dataSource, WindowsDatabaseProbeConfiguration fixture)
    {
        if (WindowsIdentity.GetCurrent().User?.Value != "S-1-5-18") throw new UnauthorizedAccessException();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var identity = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance'), current_database(), current_user", connection);
        await using (var reader = await identity.ExecuteReaderAsync())
            if (!await reader.ReadAsync() || reader.GetString(0) != fixture.FixtureId || reader.GetString(1) != fixture.Database || reader.GetString(2) != fixture.Role)
                throw new InvalidOperationException("Catalogue test does not own this database.");
        var checks = new List<string>();
        var host = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Host;
        if (host is not ("127.0.0.1" or "::1")) throw new InvalidOperationException("Catalogue test requires an explicit loopback.");
        await using var store = new PostgreSqlVaultCatalogue(new(Guid.ParseExact(fixture.FixtureId, "N"), host, fixture.Port, fixture.Database, fixture.Role));
        using var owner = new PolicyCaller(fixture.Actors["A"]);
        using var other = new PolicyCaller(fixture.Actors["B"]);
        using var administrator = new PolicyCaller("S-1-5-18");
        try
        {
            await store.ProvisionAsync();
            var one = CreateBinding();
            var two = CreateBinding();
            var initial = Configuration(one);
            var a = await store.CreateAsync(owner, one, "Alpha", initial);
            var b = await store.CreateAsync(owner, two, "Beta", Configuration(two));
            Check(a.Access.OwnerSid == owner.UserSid && a.Access.Grants.Count == 0 && a.Revision == 1, "creator and initial revision");
            Check((await store.ListAccessibleAsync(other)).Vaults.Count == 0, "ungranted discovery is empty");
            Check((await store.ListAccessibleAsync(owner)).Vaults.Count == 2, "two vaults in one database are discoverable");
            await Denied(() => store.AdmitAsync(other, Request(a, FluxVaultIpcCommand.ListVersions)), "ungranted admission");
            await Denied(() => store.AdmitAsync(owner, Request(a, FluxVaultIpcCommand.ListVersions) with { VaultId = null }), "omitted target");
            await Denied(() => store.AdmitAsync(owner, Request(a, FluxVaultIpcCommand.ListVersions) with { VaultId = VaultId.New() }), "unknown target");
            var edited = initial with { IsEnabled = false, WatchedFolders = [new("work", Path.Combine(fixture.Root, "working"), true, ["*"], [], FluxVault.Abstractions.Policies.CompressionPreference.Off, FluxVault.Abstractions.Policies.ResourceProfile.Fast, true)] };
            var save = Request(a, FluxVaultIpcCommand.SaveConfiguration) with { Configuration = edited, OperationId = Guid.NewGuid() };
            await Denied(() => store.AdmitAsync(owner, save), "generic admission cannot consume save identity");
            var saved = await store.SaveConfigurationAsync(owner, save);
            Check(saved.Vault.Revision == 2 && !saved.Vault.Configuration.IsEnabled, "saved round trip and revision");
            Check(JsonSerializer.Serialize(edited) == JsonSerializer.Serialize(saved.Vault.Configuration), "full configuration round trip");
            var replay = await store.SaveConfigurationAsync(owner, save);
            Check(replay.IsReplay && replay.Vault.Revision == 2 && replay.Receipt?.Response?.Success == true, "exact committed save replay");
            await Failure(() => store.SaveConfigurationAsync(owner, save with { Configuration = edited with { IsEnabled = true } }), VaultCatalogueFailure.OperationConflict, "operation fingerprint collision");
            await Failure(() => store.SaveConfigurationAsync(owner, save with { OperationId = Guid.NewGuid() }), VaultCatalogueFailure.StaleRevision, "stale revision CAS");
            var accepted = saved.Vault;
            await Failure(() => store.SaveConfigurationAsync(owner, Request(accepted, FluxVaultIpcCommand.SaveConfiguration) with { OperationId = Guid.NewGuid(), Configuration = edited with { MetadataStore = edited.MetadataStore with { DatabaseName = "other" } } }), VaultCatalogueFailure.InvalidConfiguration, "metadata rebinding rejected");
            await Failure(() => store.SaveConfigurationAsync(owner, Request(accepted, FluxVaultIpcCommand.SaveConfiguration) with { OperationId = Guid.NewGuid(), Configuration = edited with { DiagnosticsPolicy = edited.DiagnosticsPolicy with { LogDirectory = Path.Combine(fixture.Root, "other") } } }), VaultCatalogueFailure.InvalidConfiguration, "privileged log rebinding rejected");
            var grant = Request(accepted, FluxVaultIpcCommand.SetVaultAccess) with { OperationId = Guid.NewGuid(), AccessGrants = [new(other.UserSid, VaultPermission.ReadHistory)] };
            await Denied(() => store.AdmitAsync(administrator, grant), "generic admission cannot consume access identity");
            await Denied(() => store.SetAccessAsync(owner, grant), "owner cannot manage access");
            var granted = await store.SetAccessAsync(administrator, grant);
            Check((await store.ListAccessibleAsync(other)).Vaults.Single().VaultId == one.Id, "grant exposes only its vault");
            await store.AdmitAsync(other, Request(granted.Vault, FluxVaultIpcCommand.ListVersions));
            checks.Add("read grant admits history");
            await Denied(() => store.AdmitAsync(other, Request(granted.Vault, FluxVaultIpcCommand.RestoreVersion)), "read grant cannot restore");
            var revoked = await store.SetAccessAsync(administrator, Request(granted.Vault, FluxVaultIpcCommand.SetVaultAccess) with { OperationId = Guid.NewGuid(), AccessGrants = [] });
            await Denied(() => store.AdmitAsync(other, Request(revoked.Vault, FluxVaultIpcCommand.ListVersions)), "acknowledged revocation blocks new admission");
            await using (var restarted = new PostgreSqlVaultCatalogue(store.Endpoint))
            {
                Check((await restarted.ListAccessibleAsync(other)).Vaults.Count == 0, "revocation persists across store restart");
                var record = (await restarted.AdmitAsync(owner, Request(b, FluxVaultIpcCommand.GetStatus))).Vault;
                Check(record.Revision == b.Revision && JsonSerializer.Serialize(record.Configuration) == JsonSerializer.Serialize(b.Configuration), "other vault stays unchanged");
                var firstPage = await restarted.ListAccessibleAsync(owner, pageSize: 1);
                var secondPage = await restarted.ListAccessibleAsync(owner, firstPage.NextAfter, pageSize: 1);
                Check(firstPage.NextAfter is not null && secondPage.NextAfter is null && firstPage.Vaults.Concat(secondPage.Vaults).Select(v => v.VaultId).ToHashSet().SetEquals([one.Id, two.Id]), "paged discovery preserves complete authorised inventory");
            }
            await using (var mismatched = new PostgreSqlVaultCatalogue(store.Endpoint with { InstanceId = Guid.NewGuid() }))
                await Failure(() => mismatched.ListAccessibleAsync(owner), VaultCatalogueFailure.IdentityMismatch, "catalogue identity mismatch fails closed");
            var mutation = Request(revoked.Vault, FluxVaultIpcCommand.RunBackupNow) with { OperationId = Guid.NewGuid() };
            var admitted = await store.AdmitAsync(owner, mutation);
            Check(admitted.Receipt?.State == VaultOperationState.Admitted && !admitted.IsReplay, "durable admission before side effects");
            var duplicate = await store.AdmitAsync(owner, mutation);
            Check(duplicate.IsReplay && duplicate.Receipt?.Response is null, "interrupted mutation cannot be replayed");
            await store.CompleteAsync(admitted.Receipt!, FluxVaultIpcResponse.Ok());
            Check((await store.GetReceiptAsync(owner, one.Id, mutation.OperationId!.Value))?.Response?.Success == true, "receipt reconciliation after completion");
            await using (var corrupt = new NpgsqlCommand("UPDATE fv_control.operations SET response='{invalid response' WHERE operation_id=@operation", connection))
            {
                corrupt.Parameters.AddWithValue("operation", mutation.OperationId.Value);
                await corrupt.ExecuteNonQueryAsync();
            }
            await Denied(() => store.GetReceiptAsync(other, one.Id, mutation.OperationId.Value), "other actor cannot inspect receipt");
            var permitted = await store.SetAccessAsync(administrator, Request(revoked.Vault, FluxVaultIpcCommand.SetVaultAccess) with { OperationId = Guid.NewGuid(), AccessGrants = [new(other.UserSid, VaultPermission.ManageProtection | VaultPermission.Maintain | VaultPermission.DeleteHistory)] });
            await Failure(() => store.AdmitAsync(other, mutation with { ExpectedVaultRevision = permitted.Vault.Revision }), VaultCatalogueFailure.OperationConflict, "cross actor collision rejects malformed response before decoding");
            var purge = Request(permitted.Vault, FluxVaultIpcCommand.SaveConfiguration) with { OperationId = Guid.NewGuid(), Configuration = permitted.Vault.Configuration, PurgeRemovedSelections = true };
            var purgeSaved = await store.SaveConfigurationAsync(other, purge);
            Check(purgeSaved.Receipt?.RequiredPermissions == (VaultPermission.ManageProtection | VaultPermission.Maintain | VaultPermission.DeleteHistory), "effective purge permissions are durable");
            await store.CompleteAsync(purgeSaved.Receipt!, FluxVaultIpcResponse.Ok());
            var limited = await store.SetAccessAsync(administrator, Request(purgeSaved.Vault, FluxVaultIpcCommand.SetVaultAccess) with { OperationId = Guid.NewGuid(), AccessGrants = [new(other.UserSid, VaultPermission.ManageProtection)] });
            await Denied(() => store.GetReceiptAsync(other, one.Id, purge.OperationId!.Value), "partial revocation denies purge receipt");
            await Denied(() => store.SaveConfigurationAsync(other, purge), "partial revocation denies purge replay");
            var executor = new CountingExecutor();
            var dispatcher = new AuthenticatedFluxVaultRequestHandler(store, executor);
            var deniedResponse = await dispatcher.HandleAsync(other, Request(b, FluxVaultIpcCommand.ListVersions));
            Check(!deniedResponse.Success && deniedResponse.ErrorCode == FluxVaultIpcErrorCode.Denied && executor.Calls == 0, "real catalogue denial precedes repository executor");
            var allowedResponse = await dispatcher.HandleAsync(owner, Request(b, FluxVaultIpcCommand.ListVersions));
            Check(allowedResponse.Success && allowedResponse.VaultId == two.Id && allowedResponse.VaultRevision == b.Revision && executor.Calls == 1, "real catalogue dispatch retains authorised target and revision");
            var discovery = await dispatcher.HandleAsync(other, new(FluxVaultIpcCommand.ListVaults, null, null, null, null));
            Check(discovery.Vaults?.Single().VaultId == one.Id && discovery.Vaults.Single().Permissions == VaultPermission.ManageProtection && discovery.Status is null, "discovery returns only authorised summary and permissions");
            var unknownSave = Request(limited.Vault, FluxVaultIpcCommand.SaveConfiguration) with { OperationId = Guid.NewGuid(), Configuration = limited.Vault.Configuration with { IsEnabled = true } };
            await using (var disconnected = new PostgreSqlVaultCatalogue(store.Endpoint)
                { AfterMutationCommit = (_, _) => throw new IOException("fixture lost acknowledgement after WAL commit") })
            {
                var uncertainDispatcher = new AuthenticatedFluxVaultRequestHandler(disconnected, executor);
                var uncertain = await uncertainDispatcher.HandleAsync(owner, unknownSave);
                Check(!uncertain.Success && uncertain.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown && executor.Calls == 1, "lost save acknowledgement does not execute dependent repository command");
                var receipt = await store.GetReceiptAsync(owner, one.Id, unknownSave.OperationId!.Value);
                Check(receipt?.Response?.Success == true && receipt.Revision == limited.Vault.Revision + 1, "durable save is reconciled after acknowledgement loss");
                var reconciled = await uncertainDispatcher.HandleAsync(owner, unknownSave);
                Check(reconciled.Success && reconciled.VaultRevision == receipt!.Revision && executor.Calls == 1, "same save returns committed receipt without repeating effect");
            }
            await using var acl = new NpgsqlCommand("SELECT has_schema_privilege('public', 'fv_control', 'USAGE')", connection);
            Check(await acl.ExecuteScalarAsync() is false, "PUBLIC has no control schema access");
            await using var durability = new NpgsqlCommand("SELECT current_setting('fsync') = 'on' AND current_setting('synchronous_commit') = 'on' AND current_setting('full_page_writes') = 'on'", connection);
            Check(await durability.ExecuteScalarAsync() is true, "acknowledgement durability settings");
            var metadata = fixture.RunMetadataTests ? await VaultMetadataProbe.RunAsync(dataSource, one, two) : null;
            var repository = fixture.RunMetadataTests ? await VaultRepositoryProbe.RunAsync(dataSource, one) : null;
            return new { Passed = checks.Count, Checks = checks, NativeActor = "SYSTEM", PolicyActorsAreDoubles = true, StoreHost = host, Metadata = metadata, Repository = repository };
        }
        finally
        {
            // Server/DB/SID identity above confines destructive fixture teardown; never a normal endpoint.
            await using var cleanup = new NpgsqlCommand("DROP SCHEMA IF EXISTS fv_control CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync();
        }

        VaultBinding CreateBinding()
        {
            var id = VaultId.New();
            var state = Path.Combine(fixture.Root, "catalogue", id.ToString());
            return new(id, Path.Combine(state, "repository"), Path.Combine(state, "state"), MetadataStoreConfiguration.CreateDefault(state) with { Host = host, Port = fixture.Port, DatabaseName = fixture.Database, Username = fixture.Role });
        }
        FluxVaultConfiguration Configuration(VaultBinding binding) => FluxVaultConfiguration.CreateDefault(binding.StateRoot) with { RepositoryPath = binding.RepositoryPath, MetadataStore = binding.MetadataStore };
        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Catalogue contract failed: " + name); checks.Add(name); }
        async Task Failure(Func<Task> action, VaultCatalogueFailure expected, string name)
        {
            try { await action(); } catch (VaultCatalogueException exception) when (exception.Failure == expected) { checks.Add(name); return; }
            throw new InvalidOperationException("Catalogue contract did not reject: " + name);
        }
        Task Denied(Func<Task> action, string name) => Failure(action, VaultCatalogueFailure.Denied, name);
    }

    private static FluxVaultIpcRequest Request(VaultCatalogueEntry vault, FluxVaultIpcCommand command) => new(command, null, null, null, null,
        VaultId: vault.Binding.Id, ExpectedVaultRevision: vault.Revision);

    private sealed class PolicyCaller(string sid) : FluxVaultCallerContext
    {
        public override string UserSid => sid;
        public override IReadOnlySet<string> EnabledGroupSids => new HashSet<string>();
        public override bool IsElevated => sid == "S-1-5-18";
        public override bool ImpersonationPermitted => true;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new NotSupportedException("Catalogue policy double has no Windows token.");
        public override void Dispose() { }
    }

    private sealed class CountingExecutor : IAuthorisedVaultCommandExecutor
    {
        internal int Calls;
        public Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(FluxVaultIpcResponse.Ok()); }
    }
}
