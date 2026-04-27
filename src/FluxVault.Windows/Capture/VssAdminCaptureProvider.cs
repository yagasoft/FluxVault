using System.Diagnostics;
using System.Text.RegularExpressions;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Windows.Capture;

public sealed class VssAdminCaptureProvider : IFileCaptureProvider
{
    private static readonly Regex ShadowIdPattern = new(
        @"Shadow Copy ID:\s*(?<value>\{[0-9a-fA-F-]+\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShadowVolumePattern = new(
        @"Shadow Copy Volume Name:\s*(?<value>\\\\\?\\GLOBALROOT\\Device\\[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fullPath = Path.GetFullPath(request.SourcePath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return FileCaptureResult.Failed($"Cannot determine volume root for {fullPath}.");
        }

        var create = await RunProcessAsync(
            "vssadmin.exe",
            $"create shadow /for={root.TrimEnd('\\')}",
            cancellationToken).ConfigureAwait(false);
        if (create.ExitCode != 0)
        {
            return FileCaptureResult.Failed($"VSS shadow creation failed: {create.Error}{create.Output}");
        }

        var shadowId = ShadowIdPattern.Match(create.Output).Groups["value"].Value;
        var shadowVolume = ShadowVolumePattern.Match(create.Output).Groups["value"].Value.Trim();
        if (string.IsNullOrWhiteSpace(shadowId) || string.IsNullOrWhiteSpace(shadowVolume))
        {
            await DeleteShadowAsync(shadowId).ConfigureAwait(false);
            return FileCaptureResult.Failed("VSS shadow was created but its id or volume path could not be parsed.");
        }

        var relativePath = Path.GetRelativePath(root, fullPath);
        var shadowPath = Path.Combine(shadowVolume.TrimEnd('\\'), relativePath);
        try
        {
            var stream = new FileStream(
                shadowPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return FileCaptureResult.Captured(
                stream,
                CaptureConsistency.CrashConsistent,
                "Captured from a VSS shadow copy.",
                () => DeleteShadowAsync(shadowId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DeleteShadowAsync(shadowId).ConfigureAwait(false);
            return FileCaptureResult.Failed($"VSS shadow read failed: {ex.Message}");
        }
    }

    private static async ValueTask DeleteShadowAsync(string shadowId)
    {
        if (string.IsNullOrWhiteSpace(shadowId))
        {
            return;
        }

        _ = await RunProcessAsync(
            "vssadmin.exe",
            $"delete shadows /shadow={shadowId} /quiet",
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
