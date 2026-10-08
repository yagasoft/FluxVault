using System.Runtime.Versioning;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsRestoreSelectionOutputAccess(FluxVaultCallerContext caller) : IRestoreSelectionOutputAccess
{
    public Task<bool> ExistsAsync(string path, RepositoryEntryKind expectedKind, CancellationToken cancellationToken)
    {
        if (expectedKind is not (RepositoryEntryKind.File or RepositoryEntryKind.Folder))
            throw new ArgumentException("A file or folder destination is required.", nameof(expectedKind));
        if (!caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid))
            throw new UnauthorizedAccessException("An effective recovery caller is required.");
        cancellationToken.ThrowIfCancellationRequested();
        path = WindowsLocalPath.Validate(path);
        if (Path.GetDirectoryName(path) is null || path.Length <= 3)
            throw new UnauthorizedAccessException("A recovery destination entry is required.");
        var volume = WindowsCallerFileAccess.ResolveVolume(path);
        return caller.RunAsCallerAsync(() =>
        {
            List<SafeFileHandle>? pins = null;
            try
            {
                // Retain blind-traverse ancestors and a strictly verified immediate parent.
                // A preview never creates missing parents or opens under SYSTEM authority.
                var parent = WindowsCallerFileAccess.PinParent(path, createMissingParents: false, volume);
                pins = parent.Pins;
                cancellationToken.ThrowIfCancellationRequested();
                // Opening a folder as a file maps to generic access denied on Windows.
                // Try the folder type first; only STATUS_NOT_A_DIRECTORY permits the file open.
                try { Inspect(isDirectory: true); }
                catch (WindowsCallerFileAccess.NativeOpenException exception) when
                    (unchecked((uint)exception.NtStatus) == 0xC0000103)
                { Inspect(isDirectory: false); }
                return Task.FromResult(true);

                void Inspect(bool isDirectory)
                {
                    SafeFileHandle leaf;
                    try { leaf = WindowsCallerFileAccess.OpenNative(pins[^1], Path.GetFileName(path), 0x100080, isDirectory, sharing: 7); }
                    catch (IOException exception) when (WindowsCallerFileAccess.IsConfirmedMissingOpen(exception, validatedChain: true))
                    { throw new WindowsCallerFileAccess.ConfirmedSourceMissingException(exception); }
                    using var ownedLeaf = leaf;
                    WindowsCallerFileAccess.VerifyHandle(leaf, parent.PhysicalParent + "\\" + Path.GetFileName(path),
                        isDirectory, allowHardLinks: true);
                }
            }
            catch (WindowsCallerFileAccess.ConfirmedSourceMissingException)
            { return Task.FromResult(false); }
            finally { if (pins is not null) for (var index = pins.Count - 1; index >= 0; index--) pins[index].Dispose(); }
        });
    }

    public async Task<IRepositoryRestoreTarget> CreateAsync(string path, RepositoryEntryKind kind, bool overwriteConfirmed, CancellationToken cancellationToken)
        => kind == RepositoryEntryKind.Folder || !overwriteConfirmed
            ? await WindowsCallerRecoveryTarget.CreateNewAsync(caller, path, cancellationToken).ConfigureAwait(false)
            : await WindowsCallerRecoveryTarget.CreateAsync(caller, path, cancellationToken).ConfigureAwait(false);
}
