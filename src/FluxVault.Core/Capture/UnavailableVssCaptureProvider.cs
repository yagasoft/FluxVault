using FluxVault.Abstractions.Capture;

namespace FluxVault.Core.Capture;

public sealed class UnavailableVssCaptureProvider : IFileCaptureProvider
{
    public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(FileCaptureResult.Failed("VSS capture is not available in this host."));
    }
}
