using FluxVault.Abstractions.Capture;

namespace FluxVault.Core.Capture;

public sealed class FallbackFileCaptureProvider(IFileCaptureProvider primary, IFileCaptureProvider fallback) : IFileCaptureProvider
{
    public async Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var primaryResult = await primary.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        if (primaryResult.Success)
        {
            return primaryResult;
        }

        await primaryResult.DisposeAsync().ConfigureAwait(false);
        return await fallback.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
