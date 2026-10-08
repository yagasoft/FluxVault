using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Testing;
using FluxVault.Windows.Security;
using Npgsql;

namespace FluxVault.TestHost;

/// <summary>Failure gates use the actual retained native creator context, protected tickets and real stores.</summary>
internal sealed class WindowsProvisioningProbe(WindowsDatabaseProbeConfiguration fixture,
    FluxVaultProvisioningTicket ticket, WindowsSingleVaultProvisioner setup) : IAuthenticatedFluxVaultRequestHandler
{
    internal List<string> Checks { get; } = [];
    internal bool NativeWrongActorDenied { get; private set; }
    private bool attempted;

    internal static async Task WriteTicketAsync(string path, FluxVaultProvisioningTicket ticket, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ticket), token);
        await stream.FlushAsync(token); stream.Flush(flushToDisk: true);
    }

    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token)
    {
        if (!ticket.Accepts(caller, request))
        {
            var denial = await setup.HandleAsync(caller, request, token);
            Check(!denial.Success && denial.ErrorCode == FluxVaultIpcErrorCode.Denied && !setup.Terminal &&
                !Directory.Exists(ticket.Installation.Binding.RepositoryPath) && !Directory.Exists(ticket.Installation.Binding.StateRoot),
                "wrong native actor/correlation causes no storage effect and does not end setup");
            if (caller.UserSid == fixture.Actors["B"]) NativeWrongActorDenied = true;
            return denial;
        }
        if (attempted) throw new InvalidOperationException("The fixture expected one authorised transport attempt.");
        attempted = true;
        Check(NativeWrongActorDenied && caller.UserSid == fixture.Actors["A"] && caller.ImpersonationPermitted,
            "actual native B refusal precedes actual native A provisioning");
        await FailureCasesAsync(caller, token);
        var response = await setup.HandleAsync(caller, request, token);
        if (!response.Success) throw new InvalidOperationException("Real setup failed.", setup.LastFailure);
        Check(response.VaultId == ticket.Installation.Binding.Id && response.VaultRevision == 1 && File.Exists(ticket.BootstrapPath),
            "real setup publishes the exact bootstrap only after bound catalogue metadata and repository creation");
        var hash = Hash(ticket.BootstrapPath);
        var replay = await setup.HandleAsync(caller, request, token);
        Check(ReferenceEquals(response, replay) && Hash(ticket.BootstrapPath) == hash,
            "same-lifetime replay returns the recorded setup result without another effect");
        await using var reopened = WindowsSingleVaultProvisioner.Open(Path.Combine(fixture.Root, "catalogue", "setup-intents", "installation-ticket.json"), ticket.BootstrapPath);
        var refused = await reopened.HandleAsync(caller, request, token);
        Check(!refused.Success && Hash(ticket.BootstrapPath) == hash,
            "reopened setup refuses the installed target and preserves its activation bytes");
        return response;
    }

    private async Task FailureCasesAsync(FluxVaultCallerContext caller, CancellationToken token)
    {
        foreach (var kind in new[] { "existing-empty", "existing-file", "metadata-namespace", "control-namespace", "creation-race", "cancel-publication", "failed-publication", "activation-collision" })
        {
            var candidate = Candidate(kind);
            var ticketPath = Path.Combine(fixture.Root, "catalogue", "setup-intents", "setup-" + kind + ".json");
            await WriteTicketAsync(ticketPath, candidate, token);
            await using var attempt = WindowsSingleVaultProvisioner.Open(ticketPath, candidate.BootstrapPath);
            var binding = candidate.Installation.Binding;
            var existing = binding.RepositoryPath;
            string? sentinelHash = null, sentinelAcl = null;
            if (kind == "existing-empty") CreatePrivateDirectory(existing);
            if (kind == "existing-file")
            {
                await File.WriteAllTextAsync(existing, "generated refusal sentinel", token);
                sentinelHash = Hash(existing);
            }
            if (kind == "metadata-namespace") await SqlAsync($"CREATE SCHEMA \"{binding.MetadataNamespace}\"", token);
            if (kind == "control-namespace") await SqlAsync("CREATE SCHEMA fv_control", token);
            if (kind == "creation-race") attempt.BeforeStorageCreation = async _ =>
            {
                CreatePrivateDirectory(existing);
                await File.WriteAllTextAsync(Path.Combine(existing, "sentinel"), "generated race sentinel", token);
                sentinelHash = Hash(Path.Combine(existing, "sentinel"));
                sentinelAcl = new DirectoryInfo(existing).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            };
            using var caseStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (kind == "cancel-publication") attempt.BeforeBootstrapPublication = _ => caseStop.CancelAsync();
            if (kind == "failed-publication") attempt.BeforeBootstrapPublication = _ => throw new IOException("Owned pre-publication failure injection.");
            if (kind == "activation-collision") attempt.BeforeBootstrapPublication = async _ =>
            {
                await File.WriteAllTextAsync(candidate.BootstrapPath, "generated existing activation sentinel", token);
                sentinelHash = Hash(candidate.BootstrapPath);
            };
            var failure = await attempt.HandleAsync(caller, candidate.Handshake(), caseStop.Token);
            Check(!failure.Success && attempt.Terminal && attempt.LastFailure is not null,
                kind + " produces a terminal, explained unavailable result");
            if (kind is "existing-empty" or "existing-file" or "metadata-namespace" or "control-namespace")
            {
                Check(!Directory.Exists(binding.StateRoot) && !File.Exists(candidate.BootstrapPath) &&
                    !await NamespaceAsync(kind == "metadata-namespace" ? "fv_control" : binding.MetadataNamespace, token),
                    kind + " is refused before any other root or namespace is created");
                if (kind == "existing-file") Check(Hash(existing) == sentinelHash, "pre-existing file bytes are untouched");
            }
            else
            {
                if (kind == "activation-collision") Check(Hash(candidate.BootstrapPath) == sentinelHash,
                    "require-new publication cannot replace an existing activation file");
                else Check(!File.Exists(candidate.BootstrapPath), kind + " cannot publish a partial installation");
                if (kind == "creation-race") Check(Hash(Path.Combine(existing, "sentinel")) == sentinelHash &&
                    new DirectoryInfo(existing).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All) == sentinelAcl,
                    "exclusive native creation refuses a race target without adopting or hardening it");
                else
                {
                    Check(Directory.Exists(binding.RepositoryPath) && Directory.Exists(binding.StateRoot) &&
                        await NamespaceAsync("fv_control", token) && await NamespaceAsync(binding.MetadataNamespace, token),
                        kind + " preserves attributable partial storage and both real namespaces");
                    var state = await StateAsync(binding, token);
                    var repeated = await attempt.HandleAsync(caller, candidate.Handshake(), token);
                    Check(ReferenceEquals(failure, repeated) && state == await StateAsync(binding, token),
                        kind + " cannot restart partial provisioning in the same lifetime");
                    await using var reopened = WindowsSingleVaultProvisioner.Open(ticketPath, candidate.BootstrapPath);
                    Check(!(await reopened.HandleAsync(caller, candidate.Handshake(), token)).Success && state == await StateAsync(binding, token),
                        kind + " cannot adopt partial state in a reopened setup lifetime");
                    try
                    {
                        await using var normal = await WindowsSingleVaultService.OpenAsync(candidate.BootstrapPath, token);
                        throw new InvalidOperationException("Partial or conflicting activation was accepted by normal startup.");
                    }
                    catch (Exception exception) when (exception is IOException or JsonException)
                    { Checks.Add(kind + " keeps normal product startup closed"); }
                }
            }
            // Only this validated disposable cluster owns these exact test namespaces.
            // Partial filesystem state/tickets remain for the parent fixture journal teardown.
            if (await NamespaceAsync(binding.MetadataNamespace, token)) await SqlAsync($"DROP SCHEMA \"{binding.MetadataNamespace}\" CASCADE", token);
            if (await NamespaceAsync("fv_control", token)) await SqlAsync("DROP SCHEMA fv_control CASCADE", token);
        }
    }

    private FluxVaultProvisioningTicket Candidate(string kind)
    {
        var root = Path.Combine(fixture.Root, "catalogue");
        var state = Path.Combine(root, "setup-" + kind + "-state");
        var repository = Path.Combine(root, "setup-" + kind + "-repository");
        var metadata = ticket.Installation.Binding.MetadataStore with { BackupDirectory = Path.Combine(state, "db-backups") };
        var binding = new VaultBinding(VaultId.New(), repository, state, metadata);
        return new(ticket.Installation with { Binding = binding }, Path.Combine(state, "installation.json"),
            FluxVaultConfiguration.CreateDefault(state) with { RepositoryPath = repository, MetadataStore = metadata });
    }

    private async Task<object?> SqlAsync(string sql, CancellationToken token)
    {
        await using var source = WindowsDatabaseProbe.CreateDataSource(WindowsDatabaseProbe.CreateConnectionSettings(fixture, "127.0.0.1", "System", Guid.NewGuid()));
        await using var connection = await source.OpenConnectionAsync(token);
        await using (var scope = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance')=@id AND current_database()=@db AND current_user=@role", connection))
        {
            scope.Parameters.AddWithValue("id", fixture.FixtureId); scope.Parameters.AddWithValue("db", fixture.Database); scope.Parameters.AddWithValue("role", fixture.Role);
            if (await scope.ExecuteScalarAsync(token) is not true) throw new InvalidOperationException("Setup regression does not own the database.");
        }
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(token);
    }
    private async Task<bool> NamespaceAsync(string name, CancellationToken token)
    {
        if (name != "fv_control" && !System.Text.RegularExpressions.Regex.IsMatch(name, "^fv_[0-9a-f]{32}$"))
            throw new InvalidOperationException("Invalid owned test namespace.");
        return await SqlAsync($"SELECT EXISTS (SELECT FROM pg_namespace WHERE nspname='{name}')", token) is true;
    }
    private async Task<string> StateAsync(VaultBinding binding, CancellationToken token) => (string)(await SqlAsync($"""
        SELECT (SELECT row_to_json(i)::text FROM fv_control.identity i) ||
            (SELECT row_to_json(v)::text FROM fv_control.vault v) ||
            (SELECT count(*)::text FROM fv_control.operations) ||
            (SELECT count(*)::text FROM "{binding.MetadataNamespace}".versions)
        """, token))!;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); Checks.Add(name); }
    private static void CreatePrivateDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Owned fixture target already exists.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) acl.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
    }
}
