using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using FluxVault.Core.Service;

namespace FluxVault.Core.Tests;

public sealed class PausedBackupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paused_backup_uses_real_stored_enablement_before_any_source_or_repository_access(bool targeted)
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { IsEnabled = false };
        await store.SaveAsync(configuration);
        var access = new ForbiddenSourceAccess();
        var operations = new FluxVaultOperations(store, access, sourceAccess: access,
            metadataStoreFactory: _ => throw new InvalidOperationException("Paused backup must not open metadata."),
            repositoryFactory: _ => throw new InvalidOperationException("Paused backup must not open repository or run retention."));

        var result = targeted ? await operations.RunBackupForFilesAsync(ForbiddenPaths()) : await operations.RunBackupNowAsync();

        Assert.True(result.Success);
        Assert.Equal("Protection is disabled.", result.Message);
        Assert.Equal(0, result.CapturedFileCount);
        Assert.Equal(0, result.EnumeratedFileCount);
        Assert.Equal(0, access.Calls);
        Assert.False((await store.LoadAsync()).IsEnabled);
        Assert.False(Directory.Exists(configuration.RepositoryPath));
    }

    private static IEnumerable<string> ForbiddenPaths()
    {
        yield return UnavailablePath();
        static string UnavailablePath() => throw new InvalidOperationException("Paused targeted backup must not enumerate its request.");
    }

    private sealed class ForbiddenSourceAccess : IFileCaptureProvider, IProtectionSourceAccess
    {
        internal int Calls;
        public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Paused backup must not capture."); }
        public ProtectionSourceInspection Inspect(string protectionRoot, string path, RepositoryEntryKind kind, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Paused backup must not inspect sources."); }
        public IEnumerable<ProtectionSourceCandidate> EnumerateDirectory(string protectionRoot, string directory, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Paused backup must not enumerate sources."); }
    }
}
