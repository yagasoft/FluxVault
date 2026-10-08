using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Capture;

public sealed class FileCaptureResult : IAsyncDisposable, IDisposable
{
    private readonly Func<ValueTask>? cleanup;
    private readonly Func<CancellationToken, ValueTask>? sourceVerification;

    private FileCaptureResult(
        bool success,
        Stream? content,
        CaptureConsistency consistency,
        string message,
        Func<ValueTask>? cleanup,
        long? sourceLength = null,
        DateTimeOffset? sourceLastWriteUtc = null,
        Func<CancellationToken, ValueTask>? sourceVerification = null,
        string? sourceObservation = null)
    {
        Success = success;
        Content = content;
        Consistency = consistency;
        Message = message;
        this.cleanup = cleanup;
        SourceLength = sourceLength;
        SourceLastWriteUtc = sourceLastWriteUtc;
        this.sourceVerification = sourceVerification;
        SourceObservation = sourceObservation;
    }

    public bool Success { get; }

    public Stream? Content { get; }

    public CaptureConsistency Consistency { get; }

    public string Message { get; }

    public long? SourceLength { get; }
    public DateTimeOffset? SourceLastWriteUtc { get; }
    public string? SourceObservation { get; }

    public ValueTask VerifySourceUnchangedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return sourceVerification is null
            ? ValueTask.FromException(new InvalidOperationException("This capture has no verified live source observation."))
            : sourceVerification(cancellationToken);
    }

    public static FileCaptureResult Captured(
        Stream content,
        CaptureConsistency consistency,
        string message,
        Func<ValueTask>? cleanup = null,
        long? sourceLength = null,
        DateTimeOffset? sourceLastWriteUtc = null,
        Func<CancellationToken, ValueTask>? sourceVerification = null,
        string? sourceObservation = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new FileCaptureResult(true, content, consistency, message, cleanup, sourceLength, sourceLastWriteUtc, sourceVerification, sourceObservation);
    }

    public static FileCaptureResult Failed(string message)
    {
        return new FileCaptureResult(false, null, CaptureConsistency.Failed, message, null);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Content is not null)
        {
            await Content.DisposeAsync().ConfigureAwait(false);
        }

        if (cleanup is not null)
        {
            await cleanup().ConfigureAwait(false);
        }
    }
}
