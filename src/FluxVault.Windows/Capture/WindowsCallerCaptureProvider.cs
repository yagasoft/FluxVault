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
            using var opened = await WindowsCallerFileAccess.OpenValidatedSourceAsync(caller, root, source,
                directory: false, 0x120089, asynchronous: true, beforeOpen: null, cancellationToken).ConfigureAwait(false);
            var observation = await caller.RunAsCallerAsync(() => Task.FromResult(
                WindowsCallerFileAccess.ObserveSource(opened.Handle, opened.PhysicalPath))).ConfigureAwait(false);
            return FileCaptureResult.Captured(opened.TakeStream(), CaptureConsistency.BestEffort,
                "Captured from the caller's live readable file.", sourceLength: observation.Length,
                sourceLastWriteUtc: DateTimeOffset.FromFileTime(observation.LastWriteTime).ToUniversalTime(),
                sourceObservation: FormattableString.Invariant($"{observation.Identity:X16}:{observation.Length:X16}:{observation.LastWriteTime:X16}:{observation.ChangeTime:X16}"),
                sourceVerification: async ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    using var current = await WindowsCallerFileAccess.OpenValidatedSourceAsync(caller, root, source,
                        directory: false, 0x100080, asynchronous: false, beforeOpen: null, ct).ConfigureAwait(false);
                    await caller.RunAsCallerAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (WindowsCallerFileAccess.ObserveSource(opened.Handle, opened.PhysicalPath) != observation ||
                            WindowsCallerFileAccess.ObserveSource(current.Handle, current.PhysicalPath) != observation)
                            throw new IOException("The source changed during capture. Its earlier version is kept and this save remains pending.");
                        return Task.FromResult(true);
                    }).ConfigureAwait(false);
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A service/VSS reopen is never authority after this caller-bound open fails.
            return FileCaptureResult.Failed($"Caller source capture failed: {exception.Message}");
        }
    }
}
