using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Configuration;
using FluxVault.Cli;
using FluxVault.Core.Configuration;

namespace FluxVault.Integration.Tests;

public sealed class CliHarnessTests
{
    [Fact]
    public async Task Backup_list_inspect_and_restore_round_trip_a_real_file()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source.bin");
        var restored = Path.Combine(workspace.RootPath, "restored.bin");
        await File.WriteAllBytesAsync(source, CreatePayload());

        var backup = await FluxVaultCli.RunAsync([
            "backup",
            "--source", source,
            "--repository", workspace.RepositoryPath,
            "--compression", "zstd",
            "--legacy-file-manifest"
        ]);
        var list = await FluxVaultCli.RunAsync(["list", "--repository", workspace.RepositoryPath, "--legacy-file-manifest"]);
        var versionId = ParseVersionId(backup.StandardOutput);
        var inspect = await FluxVaultCli.RunAsync(["inspect", "--repository", workspace.RepositoryPath, "--version", versionId, "--legacy-file-manifest"]);
        var restore = await FluxVaultCli.RunAsync([
            "restore",
            "--repository", workspace.RepositoryPath,
            "--version", versionId,
            "--output", restored,
            "--legacy-file-manifest"
        ]);

        Assert.Equal(0, backup.ExitCode);
        Assert.Equal(0, list.ExitCode);
        Assert.Equal(0, inspect.ExitCode);
        Assert.Equal(0, restore.ExitCode);
        Assert.Contains(versionId, list.StandardOutput);
        Assert.Contains("chunks", inspect.StandardOutput);
        Assert.Equal(await Sha256Async(source), await Sha256Async(restored));
    }

    [Fact]
    public async Task Backup_to_mirror_leaves_no_temporary_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source.txt");
        var mirror = Path.Combine(workspace.RootPath, "mirror");
        await File.WriteAllTextAsync(source, string.Concat(Enumerable.Repeat("mirror payload ", 400)));

        var result = await FluxVaultCli.RunAsync([
            "backup",
            "--source", source,
            "--repository", workspace.RepositoryPath,
            "--mirror", mirror,
            "--legacy-file-manifest"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.EnumerateFiles(Path.Combine(mirror, "manifests"), "*.json").Any());
        Assert.Empty(Directory.EnumerateFiles(mirror, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Backup_returns_not_found_for_missing_source()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await FluxVaultCli.RunAsync([
            "backup",
            "--source", Path.Combine(workspace.RootPath, "missing.bin"),
            "--repository", workspace.RepositoryPath,
            "--legacy-file-manifest"
        ]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Source file not found", result.StandardError);
    }

    [Fact]
    public async Task Restore_returns_not_found_for_missing_version()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(workspace.RepositoryPath);

        var result = await FluxVaultCli.RunAsync([
            "restore",
            "--repository", workspace.RepositoryPath,
            "--version", "missing",
            "--output", Path.Combine(workspace.RootPath, "restored.bin"),
            "--legacy-file-manifest"
        ]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Version not found", result.StandardError);
    }

    [Fact]
    public async Task Metadata_init_reports_database_connection_failure_without_manifest_fallback()
    {
        using var workspace = TemporaryWorkspace.Create();
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            MetadataStore = MetadataStoreConfiguration.CreateDefault(workspace.RootPath) with
            {
                Host = "127.0.0.1",
                Port = 1,
                DatabaseName = "fluxvault_metadata",
                Username = "fluxvault"
            }
        };
        await new FileFluxVaultProfileSetStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath)
            .SaveAsync(new FluxVaultProfileSetConfiguration(
                FluxVaultProfileConfiguration.DefaultProfileId,
                [new FluxVaultProfileConfiguration(FluxVaultProfileConfiguration.DefaultProfileId, "Default", true, configuration)]));

        var result = await FluxVaultCli.RunAsync(["metadata-init", "--program-data", workspace.RootPath]);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("127.0.0.1", result.StandardError);
        Assert.DoesNotContain("manifests", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreatePayload()
    {
        var text = string.Concat(Enumerable.Repeat("FluxVault CLI round trip payload ", 1024));
        return Encoding.UTF8.GetBytes(text);
    }

    private static string ParseVersionId(string output)
    {
        var marker = "Version:";
        var line = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith(marker, StringComparison.OrdinalIgnoreCase));
        return line[marker.Length..].Trim();
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
