using FluxVault.Abstractions.Storage;
using FluxVault.Core.Service;

namespace FluxVault.Core.Tests;

public sealed class MaintenanceStateStoreTests
{
    [Fact]
    public async Task Open_snapshot_reader_allows_atomic_publication_and_finishes_with_its_original_state()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.RootPath, "maintenance.json");
        var writer = new FileRepositoryMaintenanceStateStore(path);
        var baseline = State("before");
        var updated = State("after");
        await writer.SaveAsync(baseline);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FileRepositoryMaintenanceStateStore(path) { AfterReadOpened = async token =>
        {
            opened.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }};
        var reading = reader.LoadAsync();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await writer.SaveAsync(updated); }
        finally { release.TrySetResult(); await reading; }

        Assert.Equal(baseline, await reading);
        Assert.Equal(updated, await writer.LoadAsync());
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task Failed_atomic_publication_removes_its_temporary_file_and_preserves_the_previous_state()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.RootPath, "maintenance.json");
        var store = new FileRepositoryMaintenanceStateStore(path);
        var baseline = State("before");
        await store.SaveAsync(baseline);
        await using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => store.SaveAsync(State("after")));
            Assert.True(failure is IOException or UnauthorizedAccessException, $"Expected a sharing refusal, received {failure}.");
        }

        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp"));
        Assert.Equal(baseline, await store.LoadAsync());
    }

    [Fact]
    public async Task Cancellation_before_publication_cleans_the_owned_temporary_file_and_preserves_the_previous_state()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.RootPath, "maintenance.json");
        var baseline = State("before");
        await new FileRepositoryMaintenanceStateStore(path).SaveAsync(baseline);
        using var cancellation = new CancellationTokenSource();
        var store = new FileRepositoryMaintenanceStateStore(path) { BeforePublish = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }};

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(State("after"), cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp"));
        Assert.Equal(baseline, await new FileRepositoryMaintenanceStateStore(path).LoadAsync());
    }

    private static RepositoryMaintenanceState State(string summary) => RepositoryMaintenanceState.Empty with
        { LastHealth = new(DateTimeOffset.UtcNow, RepositoryHealthState.Healthy, summary) };
}
