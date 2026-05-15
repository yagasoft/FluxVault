using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Configuration;

public sealed class FileFluxVaultConfigurationStore(string configPath, string programDataPath) : IFluxVaultConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static FileFluxVaultConfigurationStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            return FluxVaultConfiguration.CreateDefault(programDataPath);
        }

        await using var stream = File.OpenRead(configPath);
        var configuration = await JsonSerializer.DeserializeAsync<FluxVaultConfiguration>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"FluxVault configuration could not be read: {configPath}");
        return Normalise(configuration);
    }

    public async Task SaveAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration = Normalise(configuration);
        Validate(configuration);
        var directory = Path.GetDirectoryName(configPath) ?? throw new InvalidOperationException("Configuration path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(
            tempPath,
            JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, configPath, overwrite: true);
    }

    internal static void Validate(FluxVaultConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.RepositoryPath))
        {
            throw new InvalidDataException("Repository path is required.");
        }

        foreach (var folder in configuration.WatchedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.Id))
            {
                throw new InvalidDataException("Watched folder id is required.");
            }

            if (string.IsNullOrWhiteSpace(folder.Path))
            {
                throw new InvalidDataException($"Watched folder path is required for {folder.Id}.");
            }
        }

        if (configuration.CaptureCadencePolicy.MaximumConcurrentCaptures < 1)
        {
            throw new InvalidDataException("Maximum concurrent captures must be at least 1.");
        }

        if (configuration.CaptureCadencePolicy.WatcherEventBacklogLimit < 16)
        {
            throw new InvalidDataException("Watcher event backlog limit must be at least 16.");
        }

        var exclusionValidation = ProtectionExclusionRuleValidator.Validate(configuration.ExclusionRules);
        if (!exclusionValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", exclusionValidation.Errors));
        }

        ValidateMirrorSet(configuration.MirrorSet);
        ValidatePerformanceWorkspace(configuration.PerformanceWorkspace);
        ValidateShellIntegration(configuration.ShellIntegration);
        ValidateDirectCloud(configuration.DirectCloud);
        ValidateSecurityPosture(configuration.SecurityPosture);
        ValidateFleet(configuration.Fleet);
    }

    internal FluxVaultConfiguration Normalise(FluxVaultConfiguration configuration)
    {
        var selectionRules = configuration.SelectionRules ?? [];
        var exclusionRules = configuration.ExclusionRules ?? [];
        var mirrorSet = configuration.MirrorSet is null
            ? MirrorSetConfiguration.FromLegacyPath(configuration.MirrorPath)
            : configuration.MirrorSet.Normalise();
        if ((mirrorSet.Nodes.Count == 0) && !string.IsNullOrWhiteSpace(configuration.MirrorPath))
        {
            mirrorSet = MirrorSetConfiguration.FromLegacyPath(configuration.MirrorPath);
        }

        return configuration with
        {
            MirrorPath = null,
            MirrorSet = mirrorSet,
            RetentionPolicy = configuration.RetentionPolicy ?? RetentionPolicy.CreateDefault(),
            CaptureCadencePolicy = NormaliseCaptureCadencePolicy(configuration.CaptureCadencePolicy),
            CodecPolicy = configuration.CodecPolicy ?? CodecPolicy.CreateDefault(),
            RepositoryMaintenancePolicy = configuration.RepositoryMaintenancePolicy ?? RepositoryMaintenancePolicy.CreateDefault(),
            WorkloadPolicy = configuration.WorkloadPolicy ?? WorkloadPolicyConfiguration.CreateDefault(),
            Sync = (configuration.Sync ?? SyncConfiguration.CreateDefault(programDataPath)).Normalise(programDataPath),
            PerformanceWorkspace = (configuration.PerformanceWorkspace ?? PerformanceWorkspaceConfiguration.CreateDefault(programDataPath)).Normalise(programDataPath),
            ShellIntegration = (configuration.ShellIntegration ?? ShellIntegrationConfiguration.CreateDefault(programDataPath)).Normalise(programDataPath),
            DirectCloud = (configuration.DirectCloud ?? DirectCloudConfiguration.CreateDefault()).Normalise(),
            SecurityPosture = (configuration.SecurityPosture ?? SecurityPostureConfiguration.CreateDefault()).Normalise(),
            Fleet = (configuration.Fleet ?? EnterpriseFleetConfiguration.CreateDefault()).Normalise(),
            SelectionRules = selectionRules.Select(NormaliseSelectionRule).ToArray(),
            ExclusionRules = exclusionRules,
            WatchedFolders = selectionRules.Count == 0
                ? configuration.WatchedFolders
                : ProtectionSelectionCompiler.Compile(selectionRules.Select(NormaliseSelectionRule).ToArray())
        };
    }

    private static CaptureCadencePolicy NormaliseCaptureCadencePolicy(CaptureCadencePolicy? policy)
    {
        var defaults = CaptureCadencePolicy.CreateDefault();
        policy ??= defaults;
        return policy with
        {
            UsnFallbackFullScanCooldown = policy.UsnFallbackFullScanCooldown <= TimeSpan.Zero
                ? defaults.UsnFallbackFullScanCooldown
                : policy.UsnFallbackFullScanCooldown,
            SourceDeepVerificationInterval = policy.SourceDeepVerificationInterval <= TimeSpan.Zero
                ? defaults.SourceDeepVerificationInterval
                : policy.SourceDeepVerificationInterval
        };
    }

    private static void ValidateMirrorSet(MirrorSetConfiguration mirrorSet)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in mirrorSet.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                throw new InvalidDataException("Mirror node id is required.");
            }

            if (!ids.Add(node.Id))
            {
                throw new InvalidDataException($"Mirror node id is duplicated: {node.Id}.");
            }

            if (node.IsEnabled && string.IsNullOrWhiteSpace(node.Path))
            {
                throw new InvalidDataException($"Mirror path is required for enabled mirror node {node.Label}.");
            }
        }
    }

    private static void ValidatePerformanceWorkspace(PerformanceWorkspaceConfiguration configuration)
    {
        if (configuration.IsEnabled && string.IsNullOrWhiteSpace(configuration.WorkspacePath))
        {
            throw new InvalidDataException("Performance workspace path is required when the workspace is enabled.");
        }

        if (configuration.CacheSizeMegabytes < 128)
        {
            throw new InvalidDataException("Performance workspace cache size must be at least 128 MB.");
        }
    }

    private static void ValidateShellIntegration(ShellIntegrationConfiguration configuration)
    {
        if (configuration.IsEnabled && string.IsNullOrWhiteSpace(configuration.SyncRootPath))
        {
            throw new InvalidDataException("Shell integration sync root path is required when shell integration is enabled.");
        }

        if (configuration.IsEnabled && string.IsNullOrWhiteSpace(configuration.PlaceholderStatePath))
        {
            throw new InvalidDataException("Shell integration placeholder state path is required when shell integration is enabled.");
        }
    }

    private static void ValidateDirectCloud(DirectCloudConfiguration configuration)
    {
        if (configuration.IsEnabled && configuration.Adapters.Count == 0)
        {
            throw new InvalidDataException("At least one direct cloud adapter is required when direct cloud mode is enabled.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in configuration.Adapters)
        {
            if (string.IsNullOrWhiteSpace(adapter.Id))
            {
                throw new InvalidDataException("Direct cloud adapter id is required.");
            }

            if (!ids.Add(adapter.Id))
            {
                throw new InvalidDataException($"Direct cloud adapter id is duplicated: {adapter.Id}.");
            }

            if (adapter.IsEnabled && string.IsNullOrWhiteSpace(adapter.CredentialReference))
            {
                throw new InvalidDataException($"Credential reference is required for enabled direct cloud adapter {adapter.DisplayName}.");
            }
        }
    }

    private static void ValidateSecurityPosture(SecurityPostureConfiguration configuration)
    {
        var encryption = configuration.ClientSideEncryption;
        if (encryption.IsEnabled)
        {
            if (string.IsNullOrWhiteSpace(encryption.ActiveKeyReferenceId))
            {
                throw new InvalidDataException("Active encryption key reference is required when client-side encryption is enabled.");
            }

            if (encryption.KeyReferences.Count == 0)
            {
                throw new InvalidDataException("At least one encryption key reference is required when client-side encryption is enabled.");
            }
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in encryption.KeyReferences)
        {
            if (string.IsNullOrWhiteSpace(reference.Id))
            {
                throw new InvalidDataException("Encryption key reference id is required.");
            }

            if (!ids.Add(reference.Id))
            {
                throw new InvalidDataException($"Encryption key reference id is duplicated: {reference.Id}.");
            }

            if (string.IsNullOrWhiteSpace(reference.ReferenceName))
            {
                throw new InvalidDataException($"Encryption key reference name is required for {reference.Id}.");
            }

            if (LooksLikeInlineSecret(reference.ReferenceName))
            {
                throw new InvalidDataException($"Encryption key reference {reference.Id} appears to contain inline key material.");
            }
        }

        if (encryption.IsEnabled && !ids.Contains(encryption.ActiveKeyReferenceId!))
        {
            throw new InvalidDataException($"Active encryption key reference was not found: {encryption.ActiveKeyReferenceId}.");
        }
    }

    private static void ValidateFleet(EnterpriseFleetConfiguration configuration)
    {
        if (configuration.IsEnabled && string.IsNullOrWhiteSpace(configuration.PolicySource))
        {
            throw new InvalidDataException("Fleet policy source is required when fleet policy is enabled.");
        }

        var assignmentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in configuration.Assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Id)
                || string.IsNullOrWhiteSpace(assignment.PolicyId)
                || string.IsNullOrWhiteSpace(assignment.TargetDeviceId))
            {
                throw new InvalidDataException("Fleet policy assignment id, policy id, and target device id are required.");
            }

            if (!assignmentIds.Add(assignment.Id))
            {
                throw new InvalidDataException($"Fleet policy assignment id is duplicated: {assignment.Id}.");
            }
        }

        foreach (var status in configuration.LocalStatuses)
        {
            if (string.IsNullOrWhiteSpace(status.DeviceId) || string.IsNullOrWhiteSpace(status.PolicyId))
            {
                throw new InvalidDataException("Fleet status device id and policy id are required.");
            }
        }
    }

    private static bool LooksLikeInlineSecret(string value)
    {
        return value.Contains("BEGIN ", StringComparison.OrdinalIgnoreCase)
               || value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)
               || value.Contains('\n')
               || value.Contains('\r');
    }

    private static ProtectionSelectionRule NormaliseSelectionRule(ProtectionSelectionRule rule)
    {
        return rule with
        {
            WorkloadPreset = rule.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose
        };
    }
}
