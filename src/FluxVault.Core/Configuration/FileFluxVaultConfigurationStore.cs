using System.Text.Json;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public sealed class FileFluxVaultConfigurationStore(string configPath, string programDataPath) : IFluxVaultConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            return FluxVaultConfiguration.CreateDefault(programDataPath);
        }

        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync<FluxVaultConfiguration>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"FluxVault configuration could not be read: {configPath}");
    }

    public async Task SaveAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
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
    }
}
