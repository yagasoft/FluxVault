using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Capture;

public sealed class NormalFileCaptureProvider : IFileCaptureProvider
{
    public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var stream = new FileStream(
                request.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(FileCaptureResult.Captured(stream, CaptureConsistency.BestEffort, "Captured from live readable file."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(FileCaptureResult.Failed(ex.Message));
        }
    }
}
