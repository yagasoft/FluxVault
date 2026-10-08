using FluxVault.Abstractions.Capture;
using FluxVault.Core.Security;
using System.Runtime.Versioning;
using FluxVault.Windows.Security;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Windows.Capture;

[SupportedOSPlatform("windows")]
public sealed class WindowsCallerCaptureProvider : IFileCaptureProvider
{
    private readonly FluxVaultCallerContext caller;
    private readonly string[] acceptedRoots;

    public WindowsCallerCaptureProvider(FluxVaultCallerContext caller, IReadOnlyList<string> acceptedRoots)
    {
        ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(acceptedRoots);
        this.caller = caller;
        // Admission owns this selection snapshot. Neither later UI edits nor caller-supplied
        // path hints can widen it while its worker tasks are running.
        this.acceptedRoots = acceptedRoots.ToArray();
    }

    public async Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var source = WindowsCallerFileAccess.ValidatePath(request.SourcePath);
            var root = acceptedRoots.Select(WindowsCallerFileAccess.ValidatePath)
                .FirstOrDefault(candidate => source.StartsWith(candidate.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (root is null) return FileCaptureResult.Failed("The source is outside the admitted protection selection.");
            var stream = await new WindowsCallerFileAccess().OpenSourceAsync(caller, root, source, cancellationToken).ConfigureAwait(false);
            return FileCaptureResult.Captured(stream, CaptureConsistency.BestEffort, "Captured from the caller's live readable file.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A service/VSS reopen is never authority after this caller-bound open fails.
            return FileCaptureResult.Failed($"Caller source capture failed: {exception.Message}");
        }
    }
}
