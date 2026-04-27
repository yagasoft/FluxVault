using FluxVault.Abstractions.ChangeTracking;

namespace FluxVault.Core.ChangeTracking;

public interface IUsnJournalCheckpointStore
{
    Task<IReadOnlyList<UsnJournalCheckpoint>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<UsnJournalCheckpoint> checkpoints, CancellationToken cancellationToken = default);
}
