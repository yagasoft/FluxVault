namespace FluxVault.Abstractions.Storage;

/// <summary>An owned destination whose implementation preserves caller authority through publication.</summary>
/// <remarks>The repository consumes and disposes this target, including on validation or cancellation failure.</remarks>
public interface IRepositoryRestoreTarget : IAsyncDisposable
{
    string OutputPath { get; }
    Task PrepareAsync(RepositoryEntryKind kind, CancellationToken cancellationToken);
    Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken);
    Task<Stream> CreateFileAsync(string relativePath, CancellationToken cancellationToken);
    Task FlushFileAsync(Stream file, CancellationToken cancellationToken);
    /// <summary>Returns post-publication warnings. Failure before publication throws; published output must never be deleted.</summary>
    Task<IReadOnlyList<string>> PublishAsync(CancellationToken cancellationToken);
}
