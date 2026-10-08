using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsProtectionSourceAccess(FluxVaultCallerContext caller) : IProtectionSourceAccess
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    public ProtectionSourceInspection Inspect(string protectionRoot, string path, RepositoryEntryKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (kind is not (RepositoryEntryKind.File or RepositoryEntryKind.Folder))
                return new(ProtectionSourceAvailability.Unavailable, FailureReason: "Unsupported source type.");
            using var source = WindowsCallerFileAccess.OpenValidatedSourceAsync(caller, protectionRoot, path,
                kind == RepositoryEntryKind.Folder, kind == RepositoryEntryKind.Folder ? 0x1000A0u : 0x100080u,
                asynchronous: false, beforeOpen: null, cancellationToken).GetAwaiter().GetResult();
            return caller.RunAsCallerAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                WindowsCallerFileAccess.VerifyHandle(source.Handle, source.PhysicalPath, kind == RepositoryEntryKind.Folder);
                if (kind == RepositoryEntryKind.Folder) return Task.FromResult(new ProtectionSourceInspection(ProtectionSourceAvailability.Present, kind));
                if (!GetFileInformationByHandleEx(source.Handle, 1, out StandardInfo standard, Marshal.SizeOf<StandardInfo>())) NativeError();
                if (!GetFileInformationByHandleEx(source.Handle, 0, out BasicInfo basic, Marshal.SizeOf<BasicInfo>())) NativeError();
                if (standard.EndOfFile < 0) throw new IOException("Source length is invalid.");
                var written = DateTimeOffset.FromFileTime(basic.LastWriteTime).ToUniversalTime();
                return Task.FromResult(new ProtectionSourceInspection(ProtectionSourceAvailability.Present, kind, standard.EndOfFile, written));
            }).GetAwaiter().GetResult();
        }
        catch (WindowsCallerFileAccess.ConfirmedSourceMissingException) { return new(ProtectionSourceAvailability.Missing); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(ProtectionSourceAvailability.Unavailable, FailureReason: exception.Message); }
    }

    public IEnumerable<ProtectionSourceCandidate> EnumerateDirectory(string protectionRoot, string directory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = WindowsCallerFileAccess.OpenValidatedSourceAsync(caller, protectionRoot, directory,
            directory: true, 0x1000A1, asynchronous: false, beforeOpen: null, cancellationToken).GetAwaiter().GetResult();
        // One independent synchronous LIST_DIRECTORY handle per live enumerator. No SYSTEM
        // path enumeration and no directory cursor shared with a metadata/capture handle.
        var buffer = new byte[64 * 1024];
        var first = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = caller.RunAsCallerAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                WindowsCallerFileAccess.VerifyHandle(source.Handle, source.PhysicalPath, directory: true);
                var status = NtQueryDirectoryFile(source.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    out var result, buffer, buffer.Length, 1, false, IntPtr.Zero, first);
                if (unchecked((uint)status) == 0x80000006) return Task.FromResult<DirectoryName[]?>(null);
                if (status != 0) throw NativeException(status);
                var length = result.Information.ToInt64();
                if (length is <= 0 || length > buffer.Length) throw new IOException("Source enumeration returned an incomplete batch.");
                WindowsCallerFileAccess.VerifyHandle(source.Handle, source.PhysicalPath, directory: true);
                return Task.FromResult<DirectoryName[]?>(ParseBatch(buffer.AsSpan(0, checked((int)length))));
            }).GetAwaiter().GetResult();
            first = false;
            if (batch is null) yield break;
            foreach (var entry in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(Path.Combine(directory, entry.Name), entry.Kind);
            }
        }
    }

    internal sealed record DirectoryName(string Name, RepositoryEntryKind Kind);
    internal static DirectoryName[] ParseBatch(ReadOnlySpan<byte> bytes)
    {
        var entries = new List<DirectoryName>(); var offset = 0;
        while (true)
        {
            if (bytes.Length - offset < 64) throw new IOException("Source enumeration contains a truncated entry.");
            var record = bytes[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(record[60..]);
            var extent = next == 0 ? record.Length : next <= int.MaxValue ? (int)next : -1;
            if (extent < 64 || extent > record.Length || next != 0 && next % 8 != 0 || nameLength is 0 or > 510 ||
                nameLength % 2 != 0 || nameLength > extent - 64) throw new IOException("Source enumeration contains an invalid entry.");
            string name;
            try { name = StrictUnicode.GetString(record.Slice(64, (int)nameLength)); }
            catch (DecoderFallbackException exception)
            { throw new IOException("Source enumeration contains an unsupported UTF-16 name.", exception); }
            if (name is not ("." or ".."))
            {
                if (name.Any(character => character < 32 || "\\/:<>\"|?*".Contains(character)))
                    throw new IOException("Source enumeration contains an invalid name.");
                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[56..]);
                entries.Add(new(name, (attributes & 0x10) != 0 ? RepositoryEntryKind.Folder : RepositoryEntryKind.File));
            }
            if (next == 0) return entries.ToArray();
            offset += extent;
        }
    }

    private static Exception NativeException(int status)
    {
        var code = RtlNtStatusToDosError(status);
        return code is 5 or 1314 ? new UnauthorizedAccessException("The caller cannot enumerate this source.", new Win32Exception((int)code)) :
            new IOException("Source enumeration is unavailable.", new Win32Exception((int)code));
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void NativeError() => throw new IOException("Source metadata is unavailable.", new Win32Exception(Marshal.GetLastPInvokeError()));
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo { internal long AllocationSize, EndOfFile; internal uint NumberOfLinks; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; internal uint Attributes; }
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll")] private static extern int NtQueryDirectoryFile(SafeFileHandle handle, IntPtr @event, IntPtr apc, IntPtr context,
        out IoStatus result, [Out] byte[] buffer, int length, int kind, [MarshalAs(UnmanagedType.U1)] bool singleEntry, IntPtr name,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out StandardInfo value, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out BasicInfo value, int size);
}
