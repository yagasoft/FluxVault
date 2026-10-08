using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class ProvisioningTicketTests
{
    [Fact]
    public void Strict_ticket_round_trip_declares_the_complete_fresh_target_without_creating_it()
    {
        var ticket = Create();
        var loaded = FluxVaultProvisioningTicket.Parse(JsonSerializer.SerializeToUtf8Bytes(ticket));
        var configuration = loaded.Validate();
        Assert.Equal(ticket.Installation, loaded.Installation);
        Assert.Equal(ticket.BootstrapPath, loaded.BootstrapPath);
        Assert.Equal(120, loaded.TimeoutSeconds);
        Assert.Equal(ticket.Installation.Binding.MetadataStore, configuration.MetadataStore);
        Assert.Equal(new[] { Path.GetDirectoryName(ticket.BootstrapPath)!, ticket.Installation.Binding.RepositoryPath,
            ticket.Installation.Binding.StateRoot, ticket.Configuration.MirrorSet.Nodes[0].Path }, loaded.DirectoryTargets(configuration));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ticket.BootstrapPath)));
        Assert.False(File.Exists(ticket.BootstrapPath));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("oversized")]
    [InlineData("schema")]
    [InlineData("timeout")]
    [InlineData("relative")]
    [InlineData("binding")]
    [InlineData("metadata")]
    [InlineData("invalid-configuration")]
    [InlineData("overlapping-root")]
    [InlineData("bootstrap-is-storage")]
    [InlineData("bootstrap-is-ancestor")]
    [InlineData("pending-is-storage")]
    [InlineData("unnormalised-metadata")]
    [InlineData("bootstrap-size")]
    [InlineData("pending-leaf-length")]
    public void Ambiguous_or_inconsistent_targets_are_rejected_before_any_effect(string defect)
    {
        var ticket = Create();
        ticket = defect switch
        {
            "schema" => ticket with { SchemaVersion = 2 },
            "timeout" => ticket with { TimeoutSeconds = 0 },
            "relative" => ticket with { BootstrapPath = "relative/installation.json" },
            "binding" => ticket with { Configuration = ticket.Configuration with { RepositoryPath = ticket.Installation.Binding.StateRoot } },
            "metadata" => ticket with { Configuration = ticket.Configuration with { MetadataStore = ticket.Configuration.MetadataStore with { Username = "other" } } },
            "invalid-configuration" => ticket with { Configuration = ticket.Configuration with { CaptureCadencePolicy = ticket.Configuration.CaptureCadencePolicy with { MaximumConcurrentCaptures = 0 } } },
            "overlapping-root" => ticket with { Configuration = ticket.Configuration with { MirrorSet = new([new("mirror", "Mirror", Path.Combine(ticket.Installation.Binding.RepositoryPath, "nested-mirror"), true)]) } },
            "bootstrap-is-storage" => ticket with { BootstrapPath = ticket.Installation.Binding.RepositoryPath },
            "bootstrap-is-ancestor" => ticket with { BootstrapPath = Path.GetDirectoryName(ticket.Installation.Binding.RepositoryPath)! },
            "pending-is-storage" => ticket with { BootstrapPath = ticket.Installation.Binding.RepositoryPath[..^1],
                Installation = ticket.Installation with { Binding = ticket.Installation.Binding with { StateRoot = ticket.Installation.Binding.RepositoryPath[..^1] + ".provisioning" } } },
            "pending-leaf-length" => ticket with { BootstrapPath = Path.Combine(Path.GetDirectoryName(ticket.BootstrapPath)!, new string('x', 250)) },
            _ => ticket
        };
        var json = JsonSerializer.Serialize(ticket);
        if (defect is "unnormalised-metadata" or "bootstrap-size")
        {
            var metadata = defect == "unnormalised-metadata"
                ? ticket.Installation.Binding.MetadataStore with { BackupDirectory = "relative-backups" }
                : ticket.Installation.Binding.MetadataStore with { ServiceName = new string('x', FluxVaultInstallation.MaximumBytes) };
            ticket = ticket with { Installation = ticket.Installation with { Binding = ticket.Installation.Binding with { MetadataStore = metadata } },
                Configuration = ticket.Configuration with { MetadataStore = metadata } };
            json = JsonSerializer.Serialize(ticket);
        }
        if (defect == "unknown") json = "{\"Unrecognised\":true," + json[1..];
        if (defect == "duplicate") json = "{\"TimeoutSeconds\":120," + json[1..];
        if (defect == "nested-duplicate") json = json.Replace("\"CreatorSid\":", "\"CreatorSid\":\"S-1-5-21-1-2-3-9999\",\"CreatorSid\":", StringComparison.Ordinal);
        var bytes = defect == "oversized" ? new byte[FluxVaultProvisioningTicket.MaximumBytes + 1] : Encoding.UTF8.GetBytes(json);
        Assert.ThrowsAny<Exception>(() => FluxVaultProvisioningTicket.Parse(bytes));
        Assert.False(File.Exists(ticket.BootstrapPath));
        Assert.False(Directory.Exists(ticket.Installation.Binding.RepositoryPath));
    }

    [Fact]
    public void Setup_accepts_only_its_exact_correlated_handshake_and_native_intended_creator()
    {
        var ticket = Create();
        using var creator = new Caller(ticket.Installation.CreatorSid);
        using var stranger = new Caller("S-1-5-21-1-2-3-1002");
        using var unusable = new Caller(ticket.Installation.CreatorSid, false);
        Assert.True(ticket.Accepts(creator, ticket.Handshake()));
        Assert.False(ticket.Accepts(stranger, ticket.Handshake()));
        Assert.False(ticket.Accepts(unusable, ticket.Handshake()));
        foreach (var request in new[] { FluxVaultIpcRequest.GetStatus(), ticket.Handshake() with { OperationId = Guid.NewGuid() },
            ticket.Handshake() with { VaultId = ticket.Installation.Binding.Id }, ticket.Handshake() with { Configuration = ticket.Configuration },
            ticket.Handshake() with { OutputPath = ticket.BootstrapPath }, ticket.Handshake() with { ProfileId = "another" },
            ticket.Handshake() with { Command = FluxVaultIpcCommand.RunBackupNow } })
            Assert.False(ticket.Accepts(creator, request));
    }

    private static FluxVaultProvisioningTicket Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Provisioning", Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, "state");
        var endpoint = new VaultCatalogueEndpoint(Guid.NewGuid(), "localhost", 5432, "fluxvault_single", "fluxvault_service");
        var metadata = MetadataStoreConfiguration.CreateDefault(state) with { DatabaseName = endpoint.Database, Username = endpoint.ServiceRole };
        var binding = new VaultBinding(VaultId.New(), Path.Combine(root, "repository"), state, metadata);
        return new(new(endpoint, binding, "S-1-5-21-1-2-3-1001"), Path.Combine(root, "installation.json"),
            FluxVaultConfiguration.CreateDefault(state) with { RepositoryPath = binding.RepositoryPath, MetadataStore = metadata,
                MirrorSet = new([new("mirror", "Mirror", Path.Combine(root, "mirror"), true)]) });
    }

    private sealed class Caller(string sid, bool permitted = true) : FluxVaultCallerContext
    {
        public override string UserSid => sid;
        public override IReadOnlySet<string> EnabledGroupSids { get; } = new HashSet<string>();
        public override bool IsElevated => false;
        public override bool ImpersonationPermitted => permitted;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new InvalidOperationException("Ticket validation must have no impersonated effects.");
        public override void Dispose() { }
    }
}
