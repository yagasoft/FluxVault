using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Service;

public sealed class ProtectionRuntimeCoordinator
{
    private readonly Lock gate = new();
    private TaskCompletionSource configurationChanged = NewSignal();
    private IReadOnlyList<RepositoryPurgeScope> removedScopes = [];

    public void NotifyConfigurationChanged(IReadOnlyList<RepositoryPurgeScope> changedRemovedScopes)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            removedScopes = removedScopes
                .Concat(changedRemovedScopes)
                .Where(scope => !string.IsNullOrWhiteSpace(scope.SourcePath))
                .DistinctBy(scope => $"{scope.Kind}:{Path.GetFullPath(scope.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}", StringComparer.OrdinalIgnoreCase)
                .ToArray();
            signal = configurationChanged;
            configurationChanged = NewSignal();
        }

        signal.TrySetResult();
    }

    public IReadOnlyList<RepositoryPurgeScope> GetRemovedScopes()
    {
        lock (gate)
        {
            return removedScopes.ToArray();
        }
    }

    public IReadOnlyList<RepositoryPurgeScope> TakeRemovedScopes()
    {
        lock (gate)
        {
            var scopes = removedScopes;
            removedScopes = [];
            return scopes.ToArray();
        }
    }

    public async Task WaitForConfigurationChangeOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task configurationChangedTask;
        lock (gate)
        {
            configurationChangedTask = configurationChanged.Task;
        }

        var delayTask = Task.Delay(delay, cancellationToken);
        var completed = await Task.WhenAny(configurationChangedTask, delayTask).ConfigureAwait(false);
        if (completed == delayTask)
        {
            await delayTask.ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
