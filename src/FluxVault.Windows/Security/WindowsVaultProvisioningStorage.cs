using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

/// <summary>Fresh installation targets only. Pins all existing parents before any creation.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsVaultProvisioningStorage : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private readonly List<Target> targets = [];
    private readonly IReadOnlySet<string> trusted = WindowsVaultStorageGuard.ReadTrustedPrincipals();
    private readonly Dictionary<string, Target> byPath = new(StringComparer.OrdinalIgnoreCase);

    internal static WindowsVaultProvisioningStorage Prepare(FluxVaultProvisioningTicket ticket, FluxVaultConfiguration configuration)
    {
        var storage = new WindowsVaultProvisioningStorage();
        try
        {
            foreach (var value in ticket.DirectoryTargets(configuration).OrderBy(path => path.Length))
            {
                var path = WindowsCallerFileAccess.ValidatePath(value);
                var parent = Path.GetDirectoryName(path)!;
                Target target;
                if (storage.byPath.TryGetValue(parent, out var plannedParent)) target = new(path, plannedParent, null, null);
                else
                {
                    if (storage.byPath.Keys.Any(root => parent.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Each new storage parent must exist or be an explicitly planned root.");
                    var (pins, physical) = WindowsCallerFileAccess.PinParent(path, readSecurity: true);
                    storage.handles.AddRange(pins);
                    foreach (var pin in pins) WindowsVaultStorageGuard.CheckDescriptor(pin, null, storage.trusted, storageRoot: false);
                    RequireAbsent(pins[^1], Path.GetFileName(path));
                    target = new(path, null, pins[^1], physical);
                }
                storage.targets.Add(target);
                storage.byPath.Add(path, target);
            }
            return storage;
        }
        catch { storage.Dispose(); throw; }
    }

    internal void Create(CancellationToken token)
    {
        var descriptor = new RawSecurityDescriptor("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                var parent = target.PlannedParent?.Handle ?? target.Parent!;
                var physicalParent = target.PlannedParent?.PhysicalPath ?? target.PhysicalParent!;
                // FILE_CREATE is exclusive and installs the final owner/DACL in the
                // creation syscall. An existing race target is never opened or hardened.
                var handle = WindowsCallerFileAccess.OpenNative(parent, Path.GetFileName(target.Path), 0x1200A0,
                    directory: true, disposition: 2, securityDescriptor: pinned.AddrOfPinnedObject(), dontReparse: true);
                handles.Add(handle); target.Handle = handle;
                target.PhysicalPath = physicalParent + "\\" + Path.GetFileName(target.Path);
                WindowsCallerFileAccess.VerifyHandle(handle, target.PhysicalPath, directory: true);
                WindowsVaultStorageGuard.CheckDescriptor(handle, "S-1-5-18", trusted, storageRoot: true);
            }
        }
        finally { pinned.Free(); }
    }

    internal async Task PublishAsync(FluxVaultProvisioningTicket ticket, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ticket.Installation);
        _ = FluxVaultInstallation.Parse(bytes);
        var target = byPath[WindowsCallerFileAccess.ValidatePath(Path.GetDirectoryName(ticket.BootstrapPath)!)];
        var pendingName = Path.GetFileName(ticket.PendingBootstrapPath);
        var descriptor = new RawSecurityDescriptor("O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)");
        var sdBytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(sdBytes, 0);
        var pinned = GCHandle.Alloc(sdBytes, GCHandleType.Pinned);
        try
        {
            token.ThrowIfCancellationRequested();
            var handle = WindowsCallerFileAccess.OpenNative(target.Handle, pendingName, 0x120196, directory: false,
                disposition: 2, sharing: 0, securityDescriptor: pinned.AddrOfPinnedObject(), asynchronous: true, dontReparse: true);
            await using var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: true);
            WindowsCallerFileAccess.VerifyHandle(handle, target.PhysicalPath + "\\" + pendingName, directory: false);
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally { pinned.Free(); }
        // The flushed writer is closed before publication. Keep the trusted parent
        // pinned and use a require-new, handle-relative rename as the activation point.
        token.ThrowIfCancellationRequested();
        using var source = WindowsCallerFileAccess.OpenNative(target.Handle, pendingName, 0x130089, directory: false,
            sharing: 0, dontReparse: true);
        WindowsCallerFileAccess.VerifyHandle(source, target.PhysicalPath + "\\" + pendingName, directory: false);
        WindowsVaultStorageGuard.CheckDescriptor(source, "S-1-5-18", trusted, storageRoot: true);
        token.ThrowIfCancellationRequested();
        WindowsCallerRecoveryTarget.Rename(source, target.Handle!, Path.GetFileName(ticket.BootstrapPath), replace: false);
        // No cancellation check after activation: cancellation/lost ACK must not
        // imply the installation was not published or cause it to be deleted.
    }

    private static void RequireAbsent(SafeFileHandle parent, string name)
    {
        try
        {
            using var existing = WindowsCallerFileAccess.OpenNative(parent, name, 0x1200A0, directory: true, dontReparse: true);
        }
        catch (IOException exception) when (WindowsCallerFileAccess.IsConfirmedMissingOpen(exception, validatedChain: true)) { return; }
        throw new IOException("Provisioning refuses every existing storage target, including empty directories.");
    }

    public void Dispose()
    {
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        handles.Clear();
    }

    private sealed class Target(string path, Target? plannedParent, SafeFileHandle? parent, string? physicalParent)
    {
        internal string Path { get; } = path;
        internal Target? PlannedParent { get; } = plannedParent;
        internal SafeFileHandle? Parent { get; } = parent;
        internal string? PhysicalParent { get; } = physicalParent;
        internal SafeFileHandle? Handle { get; set; }
        internal string? PhysicalPath { get; set; }
    }
}
