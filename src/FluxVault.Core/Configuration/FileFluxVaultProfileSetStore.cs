using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

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
        var profiles = (configuration.Profiles ?? [])
            .Select(NormaliseProfile)
            .ToArray();
        if (profiles.Length == 0)
        {
            profiles = [FluxVaultProfileConfiguration.CreateDefault(programDataPath)];
        }

        var activeProfileId = string.IsNullOrWhiteSpace(configuration.ActiveProfileId)
            ? (profiles.FirstOrDefault(profile => profile.IsEnabled)?.Id ?? profiles[0].Id)
            : configuration.ActiveProfileId.Trim();
        if (!profiles.Any(profile => string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            activeProfileId = profiles.FirstOrDefault(profile => profile.IsEnabled)?.Id ?? profiles[0].Id;
        }

        return configuration with
        {
            ActiveProfileId = activeProfileId,
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
        var profileProgramDataPath = ProfileProgramDataPath(id);
        var configuration = profile.Configuration ?? FluxVaultConfiguration.CreateDefault(profileProgramDataPath);
        return profile with
        {
            Id = id,
            DisplayName = displayName,
            Configuration = NormaliseConfiguration(configuration, profileProgramDataPath)
        };
    }

    private FluxVaultConfiguration NormaliseConfiguration(FluxVaultConfiguration configuration)
    {
        return new FileFluxVaultConfigurationStore(configPath, programDataPath).Normalise(configuration);
    }

    private FluxVaultConfiguration NormaliseConfiguration(FluxVaultConfiguration configuration, string profileProgramDataPath)
    {
        return new FileFluxVaultConfigurationStore(configPath, profileProgramDataPath).Normalise(configuration);
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

    private string ProfileProgramDataPath(string profileId)
    {
        return string.Equals(profileId, FluxVaultProfileConfiguration.DefaultProfileId, StringComparison.OrdinalIgnoreCase)
            ? programDataPath
            : Path.Combine(programDataPath, "profiles", profileId);
    }
}
