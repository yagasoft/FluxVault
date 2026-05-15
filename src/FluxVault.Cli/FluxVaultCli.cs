using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Cli;

public static class FluxVaultCli
{
    public static async Task<CliResult> RunAsync(IReadOnlyList<string> args)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await RunAsync(args, standardOutput, standardError).ConfigureAwait(false);
        return new CliResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter standardOutput, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (args.Count == 0)
        {
            await standardError.WriteLineAsync("Missing command.").ConfigureAwait(false);
            return 1;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());

            return command switch
            {
                "backup" => await BackupAsync(options, standardOutput, standardError).ConfigureAwait(false),
                "list" => await ListAsync(options, standardOutput, standardError).ConfigureAwait(false),
                "inspect" => await InspectAsync(options, standardOutput, standardError).ConfigureAwait(false),
                "restore" => await RestoreAsync(options, standardOutput, standardError).ConfigureAwait(false),
                "metadata-init" => await MetadataInitAsync(options, standardOutput).ConfigureAwait(false),
                _ => await InvalidAsync(standardError, $"Unknown command: {args[0]}").ConfigureAwait(false)
            };
        }
        catch (ArgumentException ex)
        {
            await standardError.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            await standardError.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }
        catch (DirectoryNotFoundException ex)
        {
            await standardError.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or InvalidOperationException
                                   || ex.GetType().FullName?.StartsWith("Npgsql.", StringComparison.Ordinal) == true)
        {
            await standardError.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 3;
        }
    }

    private static async Task<int> BackupAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        var source = Require(options, "source");
        options.TryGetValue("mirror", out var mirrorPath);
        var compression = ParseCompression(options.TryGetValue("compression", out var value) ? value : "zstd");

        if (!File.Exists(source))
        {
            await standardError.WriteLineAsync($"Source file not found: {source}").ConfigureAwait(false);
            return 2;
        }

        var repository = await CreateRepositoryAsync(options, mirrorPath).ConfigureAwait(false);
        await using var stream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var result = await repository.CommitAsync(new FileCommitRequest(
            WatchedFolderId: "cli",
            SourcePath: Path.GetFullPath(source),
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Consistency: CaptureConsistency.BestEffort,
            Compression: compression,
            MinimumCompressionBytes: 256 * 1024,
            Content: stream)).ConfigureAwait(false);

        await standardOutput.WriteLineAsync($"Version: {result.Manifest.VersionId}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Source: {result.Manifest.SourcePath}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Bytes: {result.Manifest.LogicalLength}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Chunks: {result.Manifest.Chunks.Count}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"New chunks: {result.NewChunkCount}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ListAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        var repositoryPath = GetRepositoryPathForExistenceCheck(options);
        if (!Directory.Exists(repositoryPath))
        {
            await standardError.WriteLineAsync($"Repository not found: {repositoryPath}").ConfigureAwait(false);
            return 2;
        }

        var repository = await CreateRepositoryAsync(options).ConfigureAwait(false);
        var versions = await repository.ListVersionsAsync().ConfigureAwait(false);
        foreach (var version in versions)
        {
            await standardOutput.WriteLineAsync(
                $"{version.VersionId}\t{version.CapturedAtUtc:O}\t{version.LogicalLength}\t{version.ChunkCount}\t{version.SourcePath}")
                .ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> InspectAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        var repositoryPath = GetRepositoryPathForExistenceCheck(options);
        var versionId = Require(options, "version");
        if (!Directory.Exists(repositoryPath))
        {
            await standardError.WriteLineAsync($"Repository not found: {repositoryPath}").ConfigureAwait(false);
            return 2;
        }

        var repository = await CreateRepositoryAsync(options).ConfigureAwait(false);
        RepositoryInspection inspection;
        try
        {
            inspection = await repository.InspectAsync(versionId).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            await standardError.WriteLineAsync($"Version not found: {versionId}").ConfigureAwait(false);
            return 2;
        }

        await standardOutput.WriteLineAsync($"Version: {inspection.Manifest.VersionId}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Source: {inspection.Manifest.SourcePath}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Captured UTC: {inspection.Manifest.CapturedAtUtc:O}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Consistency: {inspection.Manifest.Consistency}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Bytes: {inspection.LogicalLength}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Stored bytes: {inspection.StoredLength}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"chunks: {inspection.ChunkCount}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RestoreAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        var repositoryPath = GetRepositoryPathForExistenceCheck(options);
        var versionId = Require(options, "version");
        var output = Require(options, "output");
        if (!Directory.Exists(repositoryPath))
        {
            await standardError.WriteLineAsync($"Repository not found: {repositoryPath}").ConfigureAwait(false);
            return 2;
        }

        var repository = await CreateRepositoryAsync(options).ConfigureAwait(false);
        try
        {
            await repository.RestoreAsync(versionId, output).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            await standardError.WriteLineAsync($"Version not found: {versionId}").ConfigureAwait(false);
            return 2;
        }

        await standardOutput.WriteLineAsync($"Restored: {output}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> MetadataInitAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter standardOutput)
    {
        var configuration = await LoadConfigurationAsync(options).ConfigureAwait(false);
        var metadataStore = new PostgreSqlRepositoryMetadataStore(
            configuration.MetadataStore,
            configuration.Sync.LocalDevice.DeviceId);

        await metadataStore.InitializeAsync().ConfigureAwait(false);
        var schemaVersion = await metadataStore.GetSchemaVersionAsync().ConfigureAwait(false);
        var status = await metadataStore
            .GetRuntimeStatusAsync(configuration.MetadataStore.ExportLagWarningThreshold)
            .ConfigureAwait(false);

        await standardOutput.WriteLineAsync(
                $"Metadata store: {configuration.MetadataStore.Host}:{configuration.MetadataStore.Port}/{configuration.MetadataStore.DatabaseName}")
            .ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Schema initialized: {status.SchemaInitialized}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Schema version: {schemaVersion}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Pending outbox: {status.PendingOutboxCount}").ConfigureAwait(false);
        await standardOutput.WriteLineAsync(
                $"Oldest unexported UTC: {(status.OldestUnexportedUtc?.ToString("O") ?? "(none)")}")
            .ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"Export lag exceeded: {status.IsExportLagExceeded}").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            await standardOutput.WriteLineAsync($"Last error: {status.LastError}").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<IChunkRepository> CreateRepositoryAsync(
        IReadOnlyDictionary<string, string> options,
        string? mirrorPath = null)
    {
        if (IsLegacyFileManifestMode(options))
        {
            return CreateLegacyRepository(Require(options, "repository"), mirrorPath);
        }

        var configuration = await LoadConfigurationAsync(options).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(mirrorPath))
        {
            configuration = configuration with { MirrorSet = MirrorSetConfiguration.FromLegacyPath(mirrorPath) };
        }

        var metadataStore = new PostgreSqlRepositoryMetadataStore(
            configuration.MetadataStore,
            configuration.Sync.LocalDevice.DeviceId);
        return new FileSystemChunkRepository(
            configuration.RepositoryPath,
            new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            configuration.MirrorSet,
            metadataStore);
    }

    private static FileSystemChunkRepository CreateLegacyRepository(string repositoryPath, string? mirrorPath = null)
    {
        return new FileSystemChunkRepository(
            repositoryPath,
            new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)),
            new Blake3ContentHasher(),
            new ZstdChunkCodec(),
            string.IsNullOrWhiteSpace(mirrorPath) ? null : mirrorPath);
    }

    private static async Task<FluxVaultConfiguration> LoadConfigurationAsync(IReadOnlyDictionary<string, string> options)
    {
        var programDataPath = options.TryGetValue("program-data", out var configuredProgramData)
            ? Path.GetFullPath(configuredProgramData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FluxVault");
        var profileSetStore = new FileFluxVaultProfileSetStore(Path.Combine(programDataPath, "config.json"), programDataPath);
        var profileSet = await profileSetStore.LoadAsync().ConfigureAwait(false);
        var profile = options.TryGetValue("profile", out var profileId)
            ? profileSet.Profiles.FirstOrDefault(value => string.Equals(value.Id, profileId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Profile not found: {profileId}.")
            : profileSet.ActiveProfile;
        var configuration = profile.Configuration;
        if (options.TryGetValue("repository", out var repositoryPath) && !string.IsNullOrWhiteSpace(repositoryPath))
        {
            configuration = configuration with { RepositoryPath = Path.GetFullPath(repositoryPath) };
        }

        return configuration;
    }

    private static string GetRepositoryPathForExistenceCheck(IReadOnlyDictionary<string, string> options)
    {
        if (IsLegacyFileManifestMode(options))
        {
            return Require(options, "repository");
        }

        return options.TryGetValue("repository", out var repositoryPath) && !string.IsNullOrWhiteSpace(repositoryPath)
            ? Path.GetFullPath(repositoryPath)
            : LoadConfigurationAsync(options).GetAwaiter().GetResult().RepositoryPath;
    }

    private static bool IsLegacyFileManifestMode(IReadOnlyDictionary<string, string> options)
    {
        return options.ContainsKey("legacy-file-manifest");
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || key.Length <= 2)
            {
                throw new ArgumentException($"Invalid argument: {key}");
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(key, "--legacy-file-manifest", StringComparison.OrdinalIgnoreCase))
                {
                    result[key[2..]] = "true";
                    continue;
                }

                throw new ArgumentException($"Missing value for {key}.");
            }

            result[key[2..]] = args[++index];
        }

        return result;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required option --{name}.");
        }

        return value;
    }

    private static CompressionPreference ParseCompression(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "zstd" => CompressionPreference.Zstd,
            "lz4" => CompressionPreference.Lz4,
            "brotli" => CompressionPreference.Brotli,
            "lzma" => CompressionPreference.Lzma,
            "off" => CompressionPreference.Off,
            _ => throw new ArgumentException($"Unsupported compression: {value}")
        };
    }

    private static async Task<int> InvalidAsync(TextWriter standardError, string message)
    {
        await standardError.WriteLineAsync(message).ConfigureAwait(false);
        return 1;
    }
}
