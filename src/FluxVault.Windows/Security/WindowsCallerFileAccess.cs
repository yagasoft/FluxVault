using FluxVault.Core.Security;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

/// <summary>Source handles are acquired under the effective caller, never reopened by the service.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCallerFileAccess
{
    private readonly Action<string>? beforeFileOpen;
    public WindowsCallerFileAccess() { }
    // Native test barrier only; not supplied by requests or configuration.
    internal WindowsCallerFileAccess(Action<string> beforeFileOpen) => this.beforeFileOpen = beforeFileOpen;

    public Task<Stream> OpenSourceAsync(FluxVaultCallerContext caller, string allowedRoot, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid)) Denied("No effective caller is available.");
        cancellationToken.ThrowIfCancellationRequested();
        var root = ValidatePath(allowedRoot);
        var path = ValidatePath(sourcePath);
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) Denied("The source is outside the protection selection.");
        var mount = Path.GetPathRoot(path)!;
        var volume = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPoint(mount, volume, (uint)volume.Capacity)) NativeError();
        var volumePath = volume.ToString();
        if (!volumePath.StartsWith("\\\\?\\Volume{", StringComparison.Ordinal) || !volumePath.EndsWith("}\\", StringComparison.Ordinal))
            Denied("A local volume identity is required.");
        return caller.RunAsCallerAsync(() =>
        {
            var pins = new List<SafeFileHandle>();
            SafeFileHandle? file = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var anchor = Open(null, "\\??\\" + volumePath[4..], directory: true);
                pins.Add(anchor);
                var fs = new StringBuilder(64);
                if (!GetVolumeInformationByHandle(anchor, null, 0, out _, out _, out _, fs, (uint)fs.Capacity)) NativeError();
                if (!string.Equals(fs.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase)) Denied("Caller file access requires a local NTFS volume.");
                var expected = volumePath.TrimEnd('\\');
                VerifyHandle(anchor, expected, directory: true);
                // Resolve the accepted root, rather than the request's spelling of its prefix.
                // Ancestors above that root may allow blind traversal without read-attributes rights.
                var rootComponents = root[mount.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
                foreach (var component in rootComponents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    expected += "\\" + component;
                    var parent = OpenNative(pins[^1], component, 0x100020, true, dontReparse: true);
                    pins.Add(parent);
                    VerifyTraversal(parent, expected);
                }
                var selectedRoot = Open(pins[^1], "", directory: true);
                pins.Add(selectedRoot);
                VerifyHandle(selectedRoot, expected, directory: true);
                var requestedPrefix = path[..root.TrimEnd('\\').Length];
                if (!string.Equals(requestedPrefix, root.TrimEnd('\\'), StringComparison.Ordinal))
                {
                    // A blind ancestor can contain case-distinct roots. Resolve the request's
                    // prefix against the same fixed volume, retain both roots, and compare identity.
                    var requestedRoot = anchor;
                    var requestedPhysical = volumePath.TrimEnd('\\');
                    foreach (var component in requestedPrefix[mount.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        requestedRoot = OpenNative(requestedRoot, component, 0x100020, true, dontReparse: true);
                        pins.Add(requestedRoot); requestedPhysical += "\\" + component;
                        VerifyTraversal(requestedRoot, requestedPhysical);
                    }
                    if (FileIdentity(requestedRoot) != FileIdentity(selectedRoot))
                        Denied("The requested source root is not the selected physical directory.");
                }
                var components = path[(root.TrimEnd('\\').Length + 1)..].Split('\\');
                var sourceParent = selectedRoot;
                foreach (var component in components[..^1])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    expected += "\\" + component;
                    var parent = Open(sourceParent, component, directory: true);
                    pins.Add(parent); VerifyHandle(parent, expected, directory: true);
                    sourceParent = parent;
                }
                beforeFileOpen?.Invoke(path);
                cancellationToken.ThrowIfCancellationRequested();
                VerifyHandle(sourceParent, expected, directory: true);
                file = Open(sourceParent, components[^1], directory: false);
                VerifyHandle(file, expected + "\\" + components[^1], directory: false);
                cancellationToken.ThrowIfCancellationRequested();
                var input = new FileStream(file, FileAccess.Read, 128 * 1024, isAsync: true);
                file = null;
                Stream owned = new PinnedSourceStream(input, pins);
                pins = [];
                return Task.FromResult(owned);
            }
            finally
            {
                file?.Dispose();
                for (var index = pins.Count - 1; index >= 0; index--) pins[index].Dispose();
            }
        });
    }

    internal static string ValidatePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32767 || value.Length < 3 || !char.IsAsciiLetter(value[0]) ||
            value[1] != ':' || value[2] != '\\') Denied("An absolute local drive path is required.");
        var trimmed = value.TrimEnd('\\');
        foreach (var part in trimmed[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(character => character < 32 || "<>:\"/|?*".Contains(character))) Denied("Ambiguous Windows paths are unsupported.");
            var device = part.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(device[3])) Denied("Windows device names are unsupported.");
        }
        var full = Path.GetFullPath(value);
        if (full[3..].Contains("\\\\", StringComparison.Ordinal)) Denied("Ambiguous Windows paths are unsupported.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    internal static SafeFileHandle Open(SafeFileHandle? parent, string name, bool directory)
        => OpenNative(parent, name, directory ? 0x1000A0u : 0x120089u, directory, asynchronous: !directory);

    internal static SafeFileHandle OpenNative(SafeFileHandle? parent, string name, uint access, bool directory,
        uint disposition = 1, uint sharing = 3, IntPtr securityDescriptor = default, bool asynchronous = false, bool dontReparse = false)
    {
        // Each child is resolved against an already opened directory. The component itself is opened
        // without following reparses and is verified before it can become a parent or data stream.
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var held = false;
        try
        {
            parent?.DangerousAddRef(ref held);
            var length = checked((ushort)(name.Length * 2));
            Marshal.StructureToPtr(new UnicodeString { Length = length, MaximumLength = length, Buffer = nameBuffer }, unicodePointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero,
                ObjectName = unicodePointer, Attributes = dontReparse ? 0x1040u : 0x40u, SecurityDescriptor = securityDescriptor };
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0,
                sharing, disposition, directory ? dontReparse ? 0x21u : 0x200021u : asynchronous ? 0x200044u : 0x200064u, IntPtr.Zero, 0);
            // Directory handles are synchronous; source data handles are asynchronous/sequential.
            // No backup intent or DELETE sharing is used. Handles are non-inheritable.
            if (status != 0)
            {
                handle.Dispose();
                var code = RtlNtStatusToDosError(status);
                if (code is 5 or 1314 || unchecked((uint)status) is 0xC000050B or 0x8000002D) throw new UnauthorizedAccessException("The caller cannot access this source.", new Win32Exception((int)code));
                throw new IOException("The caller source could not be opened.", new Win32Exception((int)code));
            }
            return handle;
        }
        finally
        {
            if (held) parent!.DangerousRelease();
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    internal static void VerifyHandle(SafeFileHandle handle, string expected, bool directory, bool allowHardLinks = false)
    {
        if (GetFileType(handle) != 1) Denied("Only disk files are supported.");
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, Marshal.SizeOf<AttributeTag>())) NativeError();
        if ((tag.Attributes & 0x400) != 0 || ((tag.Attributes & 0x10) != 0) != directory) Denied("Reparse points and unexpected object types are unsupported.");
        if (directory)
        {
            if (!GetFileInformationByHandleEx(handle, 23, out CaseSensitive flags, sizeof(uint))) NativeError();
            if ((flags.Flags & 1) != 0) Denied("Case-sensitive directories are unsupported.");
        }
        else
        {
            if (!GetFileInformationByHandleEx(handle, 1, out StandardInfo standard, Marshal.SizeOf<StandardInfo>())) NativeError();
            if (standard.Directory != 0 || standard.DeletePending != 0 || (!allowHardLinks && standard.NumberOfLinks != 1)) Denied("Hard-linked or pending-deletion sources are unsupported.");
        }
        VerifyFinalPath(handle, expected);
    }

    private static void VerifyTraversal(SafeFileHandle handle, string expected)
    {
        if (GetFileType(handle) != 1) Denied("Only disk directories are supported.");
        if (!GetFileInformationByHandleEx(handle, 1, out StandardInfo standard, Marshal.SizeOf<StandardInfo>())) NativeError();
        if (standard.Directory == 0 || standard.DeletePending != 0) Denied("A retained traversal directory is required.");
        VerifyFinalPath(handle, expected);
    }

    private static long FileIdentity(SafeFileHandle handle)
    {
        var status = NtQueryInformationFile(handle, out _, out var identity, sizeof(long), 6);
        if (status != 0) throw new UnauthorizedAccessException("The caller source identity could not be verified.",
            new Win32Exception((int)RtlNtStatusToDosError(status)));
        return identity;
    }

    private static void VerifyFinalPath(SafeFileHandle handle, string expected)
    {
        var final = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 1);
        if (length == 0) NativeError();
        if (length >= final.Capacity)
        {
            if (length > 32767) Denied("The final source path is too long.");
            final = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 1);
            if (length == 0 || length >= final.Capacity) NativeError();
        }
        if (!string.Equals(final.ToString().TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            Denied("The opened object does not match the selected physical path.");
    }

    internal static string ResolveVolume(string path)
    {
        var mount = Path.GetPathRoot(path)!;
        var volume = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPoint(mount, volume, (uint)volume.Capacity)) NativeError();
        var volumePath = volume.ToString();
        if (!volumePath.StartsWith("\\\\?\\Volume{", StringComparison.Ordinal) || !volumePath.EndsWith("}\\", StringComparison.Ordinal))
            Denied("A local volume identity is required.");
        return volumePath;
    }

    internal static (List<SafeFileHandle> Pins, string PhysicalParent) PinParent(string path, bool createMissingParents = false, string? volumeIdentity = null)
    {
        var mount = Path.GetPathRoot(path)!;
        var volumePath = volumeIdentity ?? ResolveVolume(path);
        var pins = new List<SafeFileHandle>();
        try
        {
            pins.Add(Open(null, "\\??\\" + volumePath[4..], directory: true));
            var fs = new StringBuilder(64);
            if (!GetVolumeInformationByHandle(pins[0], null, 0, out _, out _, out _, fs, (uint)fs.Capacity)) NativeError();
            if (!string.Equals(fs.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase)) Denied("Caller file access requires a local NTFS volume.");
            var expected = volumePath.TrimEnd('\\');
            VerifyHandle(pins[0], expected, directory: true);
            var parts = path[mount.Length..].Split('\\')[..^1];
            for (var index = 0; index < parts.Length; index++)
            {
                VerifyTraversal(pins[^1], expected);
                var last = index == parts.Length - 1;
                var next = OpenNative(pins[^1], parts[index], last ? 0x1000A0u : 0x100020u, true,
                    disposition: createMissingParents ? 3u : 1u, dontReparse: true);
                pins.Add(next); expected += "\\" + parts[index];
                if (last) VerifyHandle(next, expected, directory: true); else VerifyTraversal(next, expected);
            }
            return (pins, expected);
        }
        catch
        {
            for (var index = pins.Count - 1; index >= 0; index--) pins[index].Dispose();
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Denied(string reason) => throw new UnauthorizedAccessException(reason);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void NativeError()
    {
        var error = Marshal.GetLastPInvokeError();
        if (error is 5 or 1314) throw new UnauthorizedAccessException("The caller source could not be verified.", new Win32Exception(error));
        throw new IOException("The caller source could not be verified.", new Win32Exception(error));
    }

    private sealed class PinnedSourceStream(FileStream input, List<SafeFileHandle> pins) : Stream
    {
        private int disposed;
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => input.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => input.ReadAsync(buffer, token);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => input.ReadAsync(buffer, offset, count, token);
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() => input.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
                try { input.Dispose(); } finally { ReleasePins(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                try { await input.DisposeAsync().ConfigureAwait(false); } finally { ReleasePins(); }
            GC.SuppressFinalize(this);
        }
        private void ReleasePins() { for (var index = pins.Count - 1; index >= 0; index--) pins[index].Dispose(); pins.Clear(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { internal int Length; internal IntPtr RootDirectory, ObjectName; internal uint Attributes; internal IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct CaseSensitive { internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo { internal long AllocationSize, EndOfFile; internal uint NumberOfLinks; internal byte DeletePending, Directory; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatus status,
        IntPtr allocationSize, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatus status, out long identity, int length, int kind);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder name, uint capacity);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, StringBuilder? name, uint capacity,
        out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder fileSystem, uint fileSystemCapacity);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag value, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out StandardInfo value, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out CaseSensitive value, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
