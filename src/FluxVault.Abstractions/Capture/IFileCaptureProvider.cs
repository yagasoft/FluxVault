namespace FluxVault.Abstractions.Capture;

public interface IFileCaptureProvider
{
    Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default);
}
