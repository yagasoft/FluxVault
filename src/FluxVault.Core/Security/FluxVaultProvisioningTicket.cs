using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Configuration;

namespace FluxVault.Core.Security;

/// <summary>Protected, once-only installation intent; never supplied by an ordinary client.</summary>
public sealed record FluxVaultProvisioningTicket(FluxVaultInstallation Installation, string BootstrapPath,
    FluxVaultConfiguration Configuration, int TimeoutSeconds = FluxVaultProvisioningTicket.DefaultTimeoutSeconds, int SchemaVersion = 1)
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MaximumTimeoutSeconds = 600;
    public const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    [JsonIgnore] public string PendingBootstrapPath => BootstrapPath + ".provisioning";

    public static FluxVaultProvisioningTicket Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new InvalidDataException("Provisioning ticket size is invalid.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 32 });
        FluxVaultInstallation.RejectDuplicates(document.RootElement);
        var ticket = JsonSerializer.Deserialize<FluxVaultProvisioningTicket>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Provisioning ticket is missing.");
        ticket.Validate();
        return ticket;
    }

    public FluxVaultConfiguration Validate()
    {
        if (SchemaVersion != 1 || Installation is null || Configuration is null || TimeoutSeconds is < 1 or > MaximumTimeoutSeconds)
            throw new InvalidDataException("Provisioning ticket schema or deadline is invalid.");
        Installation.Validate();
        // The activation artefact has a tighter bound than its surrounding ticket.
        // Prove it is publishable before filesystem or database provisioning begins.
        _ = FluxVaultInstallation.Parse(JsonSerializer.SerializeToUtf8Bytes(Installation));
        if (!Path.IsPathFullyQualified(BootstrapPath) || string.IsNullOrWhiteSpace(Path.GetFileName(BootstrapPath)))
            throw new InvalidDataException("Provisioning requires an absolute bootstrap file path.");
        _ = WindowsLocalPath.Validate(BootstrapPath);
        _ = WindowsLocalPath.Validate(PendingBootstrapPath);
        var binding = Installation.Binding;
        if (!Path.IsPathFullyQualified(Configuration.RepositoryPath) || !SamePath(Configuration.RepositoryPath, binding.RepositoryPath) ||
            Configuration.MetadataStore != binding.MetadataStore)
            throw new InvalidDataException("Provisioning configuration must match the protected storage binding.");

        var store = new FileFluxVaultConfigurationStore(BootstrapPath, binding.StateRoot);
        var configuration = store.Normalise(Configuration);
        FileFluxVaultConfigurationStore.Validate(configuration);
        if (configuration.MetadataStore != binding.MetadataStore)
            throw new InvalidDataException("Provisioning requires an already normalised immutable metadata binding.");
        var roots = new[] { binding.RepositoryPath, binding.StateRoot }
            .Concat(configuration.MirrorSet.Nodes.Where(node => node.IsEnabled).Select(node => node.Path)).ToArray();
        foreach (var root in roots)
        {
            if (!Path.IsPathFullyQualified(root) || SamePath(root, Path.GetPathRoot(root)!))
                throw new InvalidDataException("Provisioning requires absolute private directory roots below a volume root.");
            if (Contains(BootstrapPath, root) || Contains(PendingBootstrapPath, root))
                throw new InvalidDataException("Provisioning activation files must not collide with directory targets.");
        }
        for (var first = 0; first < roots.Length; first++)
            for (var second = first + 1; second < roots.Length; second++)
                if (Contains(roots[first], roots[second]) || Contains(roots[second], roots[first]))
                    throw new InvalidDataException("Provisioning private storage roots must not overlap.");
        if (SamePath(Path.GetDirectoryName(BootstrapPath)!, Path.GetPathRoot(BootstrapPath)!))
            throw new InvalidDataException("The bootstrap requires a private directory below a volume root.");
        return configuration;
    }

    public IReadOnlyList<string> DirectoryTargets(FluxVaultConfiguration configuration) =>
        new[] { Path.GetDirectoryName(BootstrapPath)!, Installation.Binding.RepositoryPath, Installation.Binding.StateRoot }
            .Concat(configuration.MirrorSet.Nodes.Where(node => node.IsEnabled).Select(node => node.Path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public FluxVaultIpcRequest Handshake() => FluxVaultIpcRequest.GetStatus() with { OperationId = Installation.Endpoint.InstanceId };

    public bool Accepts(FluxVaultCallerContext caller, FluxVaultIpcRequest request) =>
        caller.ImpersonationPermitted && string.Equals(caller.UserSid, Installation.CreatorSid, StringComparison.Ordinal) && request == Handshake();

    private static bool SamePath(string first, string second) => string.Equals(Canonical(first), Canonical(second), StringComparison.OrdinalIgnoreCase);
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Contains(string parent, string child) => SamePath(parent, child) ||
        Canonical(child).StartsWith(Canonical(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
