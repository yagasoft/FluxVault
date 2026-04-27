using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Capture;

public sealed class FileCaptureResult : IAsyncDisposable, IDisposable
{
    private readonly Func<ValueTask>? cleanup;

    private FileCaptureResult(
        bool success,
        Stream? content,
        CaptureConsistency consistency,
        string message,
        Func<ValueTask>? cleanup)
    {
        Success = success;
        Content = content;
        Consistency = consistency;
        Message = message;
        this.cleanup = cleanup;
    }

    public bool Success { get; }

    public Stream? Content { get; }

    public CaptureConsistency Consistency { get; }

    public string Message { get; }

    public static FileCaptureResult Captured(
        Stream content,
        CaptureConsistency consistency,
        string message,
        Func<ValueTask>? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new FileCaptureResult(true, content, consistency, message, cleanup);
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
