using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

/// <summary>Caller-created staging, protected from creation until caller-authorised publication.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCallerRecoveryTarget : IRepositoryRestoreTarget
{
    private readonly FluxVaultCallerContext caller;
    private readonly List<SafeFileHandle> pins;
    private readonly string physicalParent, stageName, leafName;
    private readonly bool destinationExisted;
    private readonly List<(string Path, bool Directory)> entries = [];
    private readonly List<FileStream> streams = [];
    private SafeFileHandle? root, rootSecurity;
    private RepositoryEntryKind? kind;
    private bool published, disposed, fileStreamCreated;
    public string OutputPath { get; }
    internal string StagePath => Path.Combine(Path.GetDirectoryName(OutputPath)!, stageName);
    internal Func<string, Task>? BeforePublication { get; set; }

    private WindowsCallerRecoveryTarget(FluxVaultCallerContext caller, string path, List<SafeFileHandle> pins,
        string physicalParent, bool destinationExisted)
    {
        this.caller = caller; OutputPath = path; this.pins = pins; this.physicalParent = physicalParent;
        this.destinationExisted = destinationExisted;
        stageName = ".FluxVault-recovery-" + Guid.NewGuid().ToString("N") + ".tmp";
        leafName = Path.GetFileName(path);
    }

    public static Task<WindowsCallerRecoveryTarget> CreateAsync(FluxVaultCallerContext caller, string destination,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(caller, destination, requireNewDestination: false, cancellationToken);

    public static Task<WindowsCallerRecoveryTarget> CreateNewAsync(FluxVaultCallerContext caller, string destination,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(caller, destination, requireNewDestination: true, cancellationToken);

    private static Task<WindowsCallerRecoveryTarget> CreateCoreAsync(FluxVaultCallerContext caller, string destination,
        bool requireNewDestination, CancellationToken cancellationToken)
    {
        RequireSystem();
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid)) throw new UnauthorizedAccessException("An effective recovery caller is required.");
        _ = new SecurityIdentifier(caller.UserSid);
        cancellationToken.ThrowIfCancellationRequested();
        var path = WindowsCallerFileAccess.ValidatePath(destination);
        if (Path.GetDirectoryName(path) is null || path.Length <= 3) throw new UnauthorizedAccessException("A recovery destination entry is required.");
        // Resolve the service's drive mapping before impersonation, then open only the fixed volume identity.
        var volume = WindowsCallerFileAccess.ResolveVolume(path);
        return caller.RunAsCallerAsync(() =>
        {
            var (pins, physical) = WindowsCallerFileAccess.PinParent(path, createMissingParents: true, volume);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exists = false;
                try
                {
                    using var destinationHandle = WindowsCallerFileAccess.OpenNative(pins[^1], Path.GetFileName(path), 0x100080, false, sharing: 7);
                    WindowsCallerFileAccess.VerifyHandle(destinationHandle, physical + "\\" + Path.GetFileName(path), false, allowHardLinks: true);
                    exists = true;
                }
                catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 2 }) { }
                if (requireNewDestination && exists) throw new IOException("Publication requires a new destination. The existing entry has been preserved.");
                var result = new WindowsCallerRecoveryTarget(caller, path, pins, physical, exists);
                return Task.FromResult(result);
            }
            catch { Release(pins); throw; }
        });
    }

    public async Task PrepareAsync(RepositoryEntryKind entryKind, CancellationToken cancellationToken)
    {
        CheckBuilding();
        if (kind is not null || !Enum.IsDefined(entryKind)) throw new InvalidOperationException("Recovery staging has one supported kind.");
        if (entryKind == RepositoryEntryKind.Folder && destinationExisted) throw new IOException("Folder recovery requires a new destination.");
        cancellationToken.ThrowIfCancellationRequested();
        kind = entryKind;
        root = await caller.RunAsCallerAsync(() =>
        {
            VerifyParent();
            using var sd = Descriptor.Root(caller.UserSid);
            // FILE_CREATE grants the initial private handle; the new DACL does not grant later caller mutation.
            var handle = WindowsCallerFileAccess.OpenNative(pins[^1], stageName, entryKind == RepositoryEntryKind.Folder ? 0x1301BFu : 0x13019Fu, entryKind == RepositoryEntryKind.Folder,
                disposition: 2, sharing: 0, securityDescriptor: sd.Pointer, asynchronous: entryKind == RepositoryEntryKind.File);
            try { WindowsCallerFileAccess.VerifyHandle(handle, PhysicalStage, entryKind == RepositoryEntryKind.Folder); return Task.FromResult(handle); }
            catch { Delete(handle); handle.Dispose(); throw; }
        }).ConfigureAwait(false);
        RequireSystem();
        // The caller-owned root suppresses implicit owner DAC rights. Reopen this exact pinned,
        // protected fresh object for security metadata only; never reopen a user's destination for data.
        rootSecurity = WindowsCallerFileAccess.OpenNative(pins[^1], stageName, 0x160080, entryKind == RepositoryEntryKind.Folder, sharing: 7);
        WindowsCallerFileAccess.VerifyHandle(rootSecurity, PhysicalStage, entryKind == RepositoryEntryKind.Folder);
    }

    public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken)
    {
        CheckPrepared(RepositoryEntryKind.Folder); cancellationToken.ThrowIfCancellationRequested();
        var parts = Relative(relativePath);
        using var parent = OpenProtectedParent(parts[..^1]);
        using var sd = Descriptor.Descendant(caller.UserSid);
        using var created = WindowsCallerFileAccess.OpenNative(parent.Handle, parts[^1], 0x1100A0, true, disposition: 2, securityDescriptor: sd.Pointer);
        entries.Add((relativePath, true));
        WindowsCallerFileAccess.VerifyHandle(created, PhysicalStage + "\\" + relativePath, true);
        return Task.CompletedTask;
    }

    public Task<Stream> CreateFileAsync(string relativePath, CancellationToken cancellationToken)
    {
        CheckBuilding(); cancellationToken.ThrowIfCancellationRequested();
        if (root is null || kind is null) throw new InvalidOperationException("Recovery staging is not prepared.");
        // The repository disposes each verified file before creating the next. Keep ownership of
        // active streams for failure cleanup without retaining every disposed stream's buffer.
        streams.RemoveAll(stream => !stream.CanWrite);
        SafeFileHandle file;
        if (kind == RepositoryEntryKind.File)
        {
            if (relativePath.Length != 0 || fileStreamCreated) throw new InvalidOperationException("File recovery has one stream.");
            if (!DuplicateHandle(GetCurrentProcess(), root, GetCurrentProcess(), out file, 0, false, 2)) NativeError();
            fileStreamCreated = true;
        }
        else
        {
            var parts = Relative(relativePath);
            using var parent = OpenProtectedParent(parts[..^1]);
            using var sd = Descriptor.Descendant(caller.UserSid);
            file = WindowsCallerFileAccess.OpenNative(parent.Handle, parts[^1], 0x13019F, false, disposition: 2, sharing: 0,
                securityDescriptor: sd.Pointer, asynchronous: true);
            entries.Add((relativePath, false));
            try { WindowsCallerFileAccess.VerifyHandle(file, PhysicalStage + "\\" + relativePath, false); }
            catch { file.Dispose(); throw; }
        }
        try
        {
            var stream = new FileStream(file, FileAccess.ReadWrite, 128 * 1024, isAsync: true);
            streams.Add(stream); return Task.FromResult<Stream>(stream);
        }
        catch { file.Dispose(); throw; }
    }

    public async Task FlushFileAsync(Stream file, CancellationToken cancellationToken)
    {
        CheckBuilding();
        if (file is not FileStream stream || !streams.Contains(stream)) throw new InvalidOperationException("Recovery requires its owned stream.");
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); stream.Flush(flushToDisk: true);
    }

    public async Task<IReadOnlyList<string>> PublishAsync(CancellationToken cancellationToken)
    {
        CheckBuilding();
        if (root is null || kind is null) throw new InvalidOperationException("Recovery staging is not prepared.");
        foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false);
        streams.Clear();
        if (BeforePublication is not null) await BeforePublication(StagePath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await caller.RunAsCallerAsync(() =>
        {
            VerifyParent(); WindowsCallerFileAccess.VerifyHandle(root, PhysicalStage, kind == RepositoryEntryKind.Folder);
            cancellationToken.ThrowIfCancellationRequested();
            Rename(root, pins[^1], leafName, destinationExisted && kind == RepositoryEntryKind.File);
            published = true; // Never clean up published output, even if permission handover fails.
            return Task.FromResult(true);
        }).ConfigureAwait(false);
        var warnings = new List<string>();
        try
        {
            WindowsCallerFileAccess.VerifyHandle(root, physicalParent + "\\" + leafName, kind == RepositoryEntryKind.Folder);
            using var access = Descriptor.Published(caller.UserSid);
            // Descendants become caller-writable bottom-up. No privileged traversal visits them again.
            foreach (var entry in entries.OrderByDescending(item => item.Path.Count(character => character == '\\')).ThenBy(item => item.Directory))
            {
                var parts = Relative(entry.Path);
                using var parent = OpenProtectedParent(parts[..^1]);
                using var child = WindowsCallerFileAccess.OpenNative(parent.Handle, parts[^1], 0x160080, entry.Directory);
                WindowsCallerFileAccess.VerifyHandle(child, physicalParent + "\\" + leafName + "\\" + entry.Path, entry.Directory);
                SetDacl(child, access.Pointer);
            }
            SetDacl(rootSecurity ?? throw new InvalidOperationException("The owned security handle is missing."), access.Pointer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { warnings.Add("Content was published and verified, but recovery permissions could not be fully handed over: " + exception.Message); }
        return warnings;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false);
            streams.Clear();
            if (!published && root is not null)
            {
                foreach (var entry in entries.OrderByDescending(item => item.Path.Count(character => character == '\\')).ThenBy(item => item.Directory))
                {
                    var parts = Relative(entry.Path);
                    using var parent = OpenProtectedParent(parts[..^1]);
                    using var child = WindowsCallerFileAccess.OpenNative(parent.Handle, parts[^1], 0x110080, entry.Directory);
                    WindowsCallerFileAccess.VerifyHandle(child, PhysicalStage + "\\" + entry.Path, entry.Directory);
                    Delete(child);
                }
                await caller.RunAsCallerAsync(() => { Delete(root); return Task.FromResult(true); }).ConfigureAwait(false);
            }
        }
        finally { rootSecurity?.Dispose(); root?.Dispose(); Release(pins); }
    }

    private string PhysicalStage => physicalParent + "\\" + stageName;
    private void VerifyParent() => WindowsCallerFileAccess.VerifyHandle(pins[^1], physicalParent, true);
    private void CheckBuilding() { RequireSystem(); ObjectDisposedException.ThrowIf(disposed, this); if (published) throw new InvalidOperationException("Recovery was already published."); }
    private void CheckPrepared(RepositoryEntryKind expected) { CheckBuilding(); if (kind != expected || root is null) throw new InvalidOperationException("Recovery staging kind does not match."); }
    private static void RequireSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18") throw new UnauthorizedAccessException("Protected recovery staging requires LocalSystem.");
    }
    private static string[] Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path)) throw new UnauthorizedAccessException("A staged relative path is required.");
        var parts = path.Split('\\');
        _ = WindowsCallerFileAccess.ValidatePath("C:\\" + path);
        if (parts.Any(part => part.Length == 0)) throw new UnauthorizedAccessException("An ambiguous staged path is unsupported.");
        return parts;
    }
    private ProtectedParent OpenProtectedParent(string[] parts)
    {
        var handles = new List<SafeFileHandle>();
        var current = root ?? throw new InvalidOperationException("Recovery staging is not prepared.");
        var expected = physicalParent + "\\" + (published ? leafName : stageName);
        try
        {
            WindowsCallerFileAccess.VerifyHandle(current, expected, true);
            foreach (var part in parts)
            {
                current = WindowsCallerFileAccess.Open(current, part, true); handles.Add(current); expected += "\\" + part;
                WindowsCallerFileAccess.VerifyHandle(current, expected, true);
            }
            return new(current, handles);
        }
        catch { Release(handles); throw; }
    }
    private sealed class ProtectedParent(SafeFileHandle handle, List<SafeFileHandle> handles) : IDisposable
    { internal SafeFileHandle Handle => handle; public void Dispose() => Release(handles); }
    private static void Release(List<SafeFileHandle> handles) { for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose(); handles.Clear(); }
    private static void Rename(SafeFileHandle source, SafeFileHandle parent, string name, bool replace)
    {
        var text = Encoding.Unicode.GetBytes(name); var offset = IntPtr.Size == 8 ? 20 : 12;
        var buffer = Marshal.AllocHGlobal(offset + text.Length); var held = false;
        try
        {
            parent.DangerousAddRef(ref held);
            for (var index = 0; index < offset; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteByte(buffer, replace ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, text.Length); Marshal.Copy(text, 0, buffer + offset, text.Length);
            NtCheck(NtSetInformationFile(source, out _, buffer, (uint)(offset + text.Length), 10));
        }
        finally { if (held) parent.DangerousRelease(); Marshal.FreeHGlobal(buffer); }
    }
    private static void Delete(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try { Marshal.WriteByte(buffer, 1); NtCheck(NtSetInformationFile(handle, out _, buffer, 1, 13)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void SetDacl(SafeFileHandle handle, IntPtr descriptor)
    {
        if (!GetSecurityDescriptorDacl(descriptor, out var present, out var acl, out _) || !present) NativeError();
        var status = SetSecurityInfo(handle, 1, 0x80000004, IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero);
        if (status != 0) throw new IOException("Recovery permissions could not be set (Windows " + status + ").", new Win32Exception((int)status));
    }
    private static void NtCheck(int status)
    {
        if (status == 0) return;
        var code = RtlNtStatusToDosError(status);
        if (code is 5 or 1314) throw new UnauthorizedAccessException("The caller cannot publish or remove this recovery entry.", new Win32Exception((int)code));
        throw new IOException("The recovery entry could not be published or removed.", new Win32Exception((int)code));
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void NativeError() => throw new IOException("Recovery handle operation failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    private sealed class Descriptor : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        private Descriptor(string sddl)
        { if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var pointer, out _)) NativeError(); Pointer = pointer; }
        internal static Descriptor Root(string sid) => new($"O:{sid}D:P(D;;WDWO;;;OW)(A;;FA;;;SY)(A;;FRFX;;;{sid})");
        internal static Descriptor Descendant(string sid) => new($"O:SYD:P(A;;FA;;;SY)(A;;FRFX;;;{sid})");
        internal static Descriptor Published(string sid) => new($"D:P(A;;FA;;;SY)(A;;FA;;;{sid})");
        public void Dispose() { if (Pointer != IntPtr.Zero) { LocalFree(Pointer); Pointer = IntPtr.Zero; } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status, Information; }
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus status, IntPtr info, uint length, uint kind);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr acl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeFileHandle handle, uint kind, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
