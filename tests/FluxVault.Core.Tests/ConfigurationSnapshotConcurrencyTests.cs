using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Configuration;

namespace FluxVault.Core.Tests;

public sealed class ConfigurationSnapshotConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_publication_keeps_configuration_and_removes_its_owned_temporary_file(bool cancel)
    {
        using var workspace=TemporaryWorkspace.Create(); var path=Path.Combine(workspace.RootPath,"config.json");
        var store=new FileFluxVaultConfigurationStore(path,workspace.RootPath); var original=FluxVaultConfiguration.CreateDefault(workspace.RootPath) with {IsEnabled=false};
        await store.SaveAsync(original); var before=await File.ReadAllBytesAsync(path);
        using var blocked=cancel ? null : File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var cancelled=new CancellationTokenSource(); if(cancel)cancelled.Cancel();
        var failure=await Record.ExceptionAsync(()=>store.SaveAsync(original with {IsEnabled=true},cancelled.Token));
        Assert.NotNull(failure);
        if(cancel)Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(before,await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath,"config.json.*.tmp"));
    }

    [Fact]
    public async Task Atomic_save_can_replace_configuration_while_actual_store_read_retains_its_original_snapshot()
    {
        using var workspace=TemporaryWorkspace.Create();
        var store=new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath,"config.json"),workspace.RootPath);
        var original=FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            IsEnabled=false,
            WatchedFolders=Enumerable.Range(0,10_000).Select(i=>new WatchedFolderConfiguration("folder"+i,Path.Combine(workspace.RootPath,"working"+i),true,
                ["*.dwg",new string('x',512)],[],CompressionPreference.Off,ResourceProfile.Balanced,true)).ToArray()
        };
        await store.SaveAsync(original);
        var reading=store.LoadAsync();
        try
        {
            Assert.False(reading.IsCompleted,"The fixture must establish an actual outstanding store read.");
            await store.SaveAsync(original with {IsEnabled=true,WatchedFolders=[]});
            var snapshot=await reading;
            Assert.False(snapshot.IsEnabled); Assert.Equal(10_000,snapshot.WatchedFolders.Count);
            var latest=await store.LoadAsync(); Assert.True(latest.IsEnabled); Assert.Empty(latest.WatchedFolders);
        }
        finally { await reading; }
    }
}
