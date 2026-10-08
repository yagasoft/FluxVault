using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

/// <summary>Legacy single-record file bridge retained for recovery and store regressions; never used by the service.</summary>
public sealed class FileFluxVaultProfileSetStore(string configPath, string programDataPath) : IFluxVaultProfileSetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static FileFluxVaultProfileSetStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<FluxVaultProfileSetConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            return Normalise(FluxVaultProfileSetConfiguration.CreateDefault(programDataPath));
        }

        await using var stream = File.OpenRead(configPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("profiles", out _))
        {
            var profileSet = document.RootElement.Deserialize<FluxVaultProfileSetConfiguration>(JsonOptions)
                ?? throw new InvalidDataException($"FluxVault profile configuration could not be read: {configPath}");
            return Normalise(profileSet);
        }

        var legacy = document.RootElement.Deserialize<FluxVaultConfiguration>(JsonOptions)
            ?? throw new InvalidDataException($"FluxVault configuration could not be read: {configPath}");
        var normalisedLegacy = NormaliseConfiguration(legacy);
        return Normalise(new FluxVaultProfileSetConfiguration(
            FluxVaultProfileConfiguration.DefaultProfileId,
            [
                new FluxVaultProfileConfiguration(
                    FluxVaultProfileConfiguration.DefaultProfileId,
                    "Default",
                    normalisedLegacy.IsEnabled,
                    normalisedLegacy)
            ]));
    }

    public async Task SaveAsync(FluxVaultProfileSetConfiguration configuration, CancellationToken cancellationToken = default)
    {
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

    private FluxVaultProfileSetConfiguration Normalise(FluxVaultProfileSetConfiguration configuration)
    {
        if (configuration.Profiles is { Count: > 1 })
            throw new InvalidDataException("Multiple legacy vault records require the retained staging recovery tools. This installation supports one vault and has not changed the file.");
        var profiles = (configuration.Profiles ?? [])
            .Select(NormaliseProfile)
            .ToArray();
        if (profiles.Length == 0)
        {
            profiles = [FluxVaultProfileConfiguration.CreateDefault(programDataPath)];
        }

        return configuration with
        {
            ActiveProfileId = profiles[0].Id,
            Profiles = profiles
        };
    }

    private FluxVaultProfileConfiguration NormaliseProfile(FluxVaultProfileConfiguration profile)
    {
        var id = string.IsNullOrWhiteSpace(profile.Id)
            ? $"profile-{Guid.NewGuid():N}"
            : profile.Id.Trim();
        var displayName = string.IsNullOrWhiteSpace(profile.DisplayName)
            ? id
            : profile.DisplayName.Trim();
        var configuration = profile.Configuration ?? FluxVaultConfiguration.CreateDefault(programDataPath);
        return profile with
        {
            Id = id,
            DisplayName = displayName,
            Configuration = NormaliseConfiguration(configuration)
        };
    }

    private FluxVaultConfiguration NormaliseConfiguration(FluxVaultConfiguration configuration)
    {
        return new FileFluxVaultConfigurationStore(configPath, programDataPath).Normalise(configuration);
    }

    private static void Validate(FluxVaultProfileSetConfiguration configuration)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in configuration.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                throw new InvalidDataException("Profile id is required.");
            }

            if (!ids.Add(profile.Id))
            {
                throw new InvalidDataException($"Profile id is duplicated: {profile.Id}.");
            }

            if (string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                throw new InvalidDataException($"Profile display name is required for {profile.Id}.");
            }

            FileFluxVaultConfigurationStore.Validate(profile.Configuration);
        }
    }

}
