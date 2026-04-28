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

    private static void Validate(FluxVaultConfiguration configuration)
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

        var exclusionValidation = ProtectionExclusionRuleValidator.Validate(configuration.ExclusionRules);
        if (!exclusionValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", exclusionValidation.Errors));
        }
    }

    private static FluxVaultConfiguration Normalise(FluxVaultConfiguration configuration)
    {
        var selectionRules = configuration.SelectionRules ?? [];
        var exclusionRules = configuration.ExclusionRules ?? [];
        return configuration with
        {
            RetentionPolicy = configuration.RetentionPolicy ?? RetentionPolicy.CreateDefault(),
            CaptureCadencePolicy = configuration.CaptureCadencePolicy ?? CaptureCadencePolicy.CreateDefault(),
            CodecPolicy = configuration.CodecPolicy ?? CodecPolicy.CreateDefault(),
            SelectionRules = selectionRules,
            ExclusionRules = exclusionRules,
            WatchedFolders = selectionRules.Count == 0
                ? configuration.WatchedFolders
                : ProtectionSelectionCompiler.Compile(selectionRules)
        };
    }
}
