using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FluxVault.Abstractions.ChangeTracking;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.ChangeTracking;

public sealed class WindowsUsnChangeJournalReader : IUsnChangeJournalReader
{
    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint DefaultReasonMask = 0xffffffff;
    private const int ErrorHandleEof = 38;
    private const int JournalBufferLength = 1024 * 1024;
    private const int MaxDiagnosticDetails = 50;

    public Task<UsnChangeJournalReadResult> ReadChangesAsync(
        IReadOnlyList<UsnWatchedFolderScope> watchedFolders,
        IReadOnlyList<UsnJournalCheckpoint> checkpoints,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(ReadChanges(watchedFolders, checkpoints, cancellationToken));
        }
        catch (UsnJournalOperationException ex)
        {
            return Task.FromResult(UsnChangeJournalReadResult.Unavailable(ex.Detail.Reason, [ex.Detail]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = UsnJournalDiagnosticFormatter.UnexpectedFailure(
                watchedFolderId: null,
                path: null,
                volumeRoot: null,
                operation: "USN catch-up",
                exception: ex);
            return Task.FromResult(UsnChangeJournalReadResult.Unavailable(detail.Reason, [detail]));
        }
    }

    private static UsnChangeJournalReadResult ReadChanges(
        IReadOnlyList<UsnWatchedFolderScope> watchedFolders,
        IReadOnlyList<UsnJournalCheckpoint> checkpoints,
        CancellationToken cancellationToken)
    {
        if (watchedFolders.Count == 0)
        {
            return UsnChangeJournalReadResult.Active("USN active; no watched folders.", [], checkpoints);
        }

        var changed = new List<UsnChangedFile>();
        var nextCheckpoints = new List<UsnJournalCheckpoint>();
        var continuityFallbackReasons = new List<string>();
        var unavailableReasons = new List<string>();
        var details = new List<DurableChangeDetail>();

        foreach (var scope in watchedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderPath = Path.GetFullPath(scope.Path);
            var volumeRoot = Path.GetPathRoot(folderPath);
            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                var detail = new DurableChangeDetail(
                    scope.WatchedFolderId,
                    scope.Path,
                    null,
                    "Resolve volume root",
                    $"{scope.Path}: volume root could not be resolved.",
                    null);
                unavailableReasons.Add(detail.Reason);
                AddDetail(details, detail);
                continue;
            }

            try
            {
                using var volume = OpenVolume(scope, volumeRoot);
                var state = QueryJournal(volume, scope, volumeRoot);
                var checkpoint = checkpoints
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.WatchedFolderId, scope.WatchedFolderId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.VolumeRoot, state.VolumeRoot, StringComparison.OrdinalIgnoreCase));
                var continuity = UsnJournalContinuity.Evaluate(scope.WatchedFolderId, state, checkpoint, DefaultReasonMask);
                nextCheckpoints.Add(continuity.Checkpoint);
                if (continuity.RequiresFullScan)
                {
                    var detail = new DurableChangeDetail(
                        scope.WatchedFolderId,
                        scope.Path,
                        state.VolumeRoot,
                        "Evaluate USN continuity",
                        $"{scope.Path}: {continuity.FallbackReason}",
                        null);
                    continuityFallbackReasons.Add(detail.Reason);
                    AddDetail(details, detail);
                    continue;
                }

                changed.AddRange(ReadChangedFiles(volume, scope, state, continuity.StartUsn, details, cancellationToken));
            }
            catch (UsnJournalOperationException ex)
            {
                unavailableReasons.Add(ex.Detail.Reason);
                AddDetail(details, ex.Detail);
            }
        }

        if (unavailableReasons.Count > 0)
        {
            return UsnChangeJournalReadResult.Unavailable(string.Join(" ", unavailableReasons), details) with
            {
                Checkpoints = nextCheckpoints
            };
        }

        if (continuityFallbackReasons.Count > 0)
        {
            return UsnChangeJournalReadResult.FullScanRequired(string.Join(" ", continuityFallbackReasons), nextCheckpoints, details);
        }

        return UsnChangeJournalReadResult.Active(
            $"USN active. Found {changed.Count} changed file(s).",
            changed,
            nextCheckpoints,
            details);
    }

    private static IEnumerable<UsnChangedFile> ReadChangedFiles(
        SafeFileHandle volume,
        UsnWatchedFolderScope scope,
        UsnJournalState state,
        long startUsn,
        ICollection<DurableChangeDetail> details,
        CancellationToken cancellationToken)
    {
        var currentUsn = startUsn;
        var buffer = new byte[JournalBufferLength];
        while (currentUsn < state.NextUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ReadUsnJournalDataV0
            {
                StartUsn = currentUsn,
                ReasonMask = DefaultReasonMask,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = state.JournalId
            };

            var bytesReturned = DeviceIoControl(
                volume,
                FsctlReadUsnJournal,
                request,
                buffer,
                scope,
                state.VolumeRoot,
                "FSCTL_READ_USN_JOURNAL");
            if (bytesReturned <= sizeof(long))
            {
                break;
            }

            var nextUsn = BitConverter.ToInt64(buffer, 0);
            var offset = sizeof(long);
            while (offset + sizeof(int) <= bytesReturned)
            {
                var recordLength = BitConverter.ToInt32(buffer, offset);
                if (recordLength <= 0 || offset + recordLength > bytesReturned)
                {
                    break;
                }

                var record = TryParseRecord(buffer, offset, recordLength);
                if (record is not null)
                {
                    var path = ResolvePath(volume, scope, state.VolumeRoot, record.Value, details);
                    if (path is not null && IsWithinScope(scope, path))
                    {
                        yield return new UsnChangedFile(
                            scope.WatchedFolderId,
                            path,
                            record.Value.Reason,
                            (record.Value.FileAttributes & FileAttributeDirectory) == FileAttributeDirectory);
                    }
                }

                offset += recordLength;
            }

            if (nextUsn <= currentUsn)
            {
                break;
            }

            currentUsn = nextUsn;
        }
    }

    private static UsnRecord? TryParseRecord(byte[] buffer, int offset, int recordLength)
    {
        var majorVersion = BitConverter.ToUInt16(buffer, offset + 4);
        if (majorVersion == 2)
        {
            var fileReferenceNumber = BitConverter.ToUInt64(buffer, offset + 8);
            return new UsnRecord(
                FileReferenceNumber: fileReferenceNumber,
                ExtendedFileReferenceNumber: null,
                Reason: BitConverter.ToUInt32(buffer, offset + 40),
                FileAttributes: BitConverter.ToUInt32(buffer, offset + 52));
        }

        if (majorVersion == 3)
        {
            var id = new byte[16];
            Buffer.BlockCopy(buffer, offset + 8, id, 0, id.Length);
            return new UsnRecord(
                FileReferenceNumber: null,
                ExtendedFileReferenceNumber: id,
                Reason: BitConverter.ToUInt32(buffer, offset + 56),
                FileAttributes: BitConverter.ToUInt32(buffer, offset + 68));
        }

        return null;
    }

    private static string? ResolvePath(
        SafeFileHandle volume,
        UsnWatchedFolderScope scope,
        string volumeRoot,
        UsnRecord record,
        ICollection<DurableChangeDetail> details)
    {
        var descriptor = record.ExtendedFileReferenceNumber is null
            ? FileIdDescriptor.FromFileId((long)record.FileReferenceNumber.GetValueOrDefault())
            : FileIdDescriptor.FromExtendedFileId(record.ExtendedFileReferenceNumber);
        using var file = OpenFileById(
            volume,
            ref descriptor,
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            FileFlagBackupSemantics);
        if (file.IsInvalid)
        {
            AddDetail(details, UsnJournalDiagnosticFormatter.FileIdPathResolutionFailed(
                scope.WatchedFolderId,
                scope.Path,
                volumeRoot,
                FormatFileReference(record),
                Marshal.GetLastWin32Error()));
            return null;
        }

        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (length == 0)
        {
            AddDetail(details, UsnJournalDiagnosticFormatter.FileIdPathResolutionFailed(
                scope.WatchedFolderId,
                scope.Path,
                volumeRoot,
                FormatFileReference(record),
                Marshal.GetLastWin32Error()));
            return null;
        }

        return NormaliseFinalPath(path.ToString());
    }

    private static void AddDetail(ICollection<DurableChangeDetail> details, DurableChangeDetail detail)
    {
        if (details.Count < MaxDiagnosticDetails)
        {
            details.Add(detail);
        }
    }

    private static bool IsWithinScope(UsnWatchedFolderScope scope, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(scope.Path);
        if (!scope.Recursive)
        {
            return string.Equals(
                Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseFinalPath(string path)
    {
        const string dosPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(dosPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[dosPrefix.Length..]
            : path;
    }

    private static string FormatFileReference(UsnRecord record)
    {
        if (record.FileReferenceNumber is not null)
        {
            return $"0x{record.FileReferenceNumber.Value:X16}";
        }

        return record.ExtendedFileReferenceNumber is null
            ? "unknown"
            : $"0x{Convert.ToHexString(record.ExtendedFileReferenceNumber)}";
    }

    private static SafeFileHandle OpenVolume(UsnWatchedFolderScope scope, string volumeRoot)
    {
        var root = volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumePath = root.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? root
            : $@"\\.\{root}";
        var handle = CreateFile(
            volumePath,
            GenericRead,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new UsnJournalOperationException(UsnJournalDiagnosticFormatter.OpenVolumeFailed(
                scope.WatchedFolderId,
                scope.Path,
                volumeRoot,
                volumePath,
                Marshal.GetLastWin32Error()));
        }

        return handle;
    }

    private static UsnJournalState QueryJournal(SafeFileHandle volume, UsnWatchedFolderScope scope, string volumeRoot)
    {
        var buffer = new byte[Marshal.SizeOf<UsnJournalDataV0>()];
        DeviceIoControl(
            volume,
            FsctlQueryUsnJournal,
            IntPtr.Zero,
            0,
            buffer,
            buffer.Length,
            scope,
            volumeRoot,
            "FSCTL_QUERY_USN_JOURNAL");
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var data = Marshal.PtrToStructure<UsnJournalDataV0>(handle.AddrOfPinnedObject());
            return new UsnJournalState(volumeRoot, data.UsnJournalId, data.FirstUsn, data.NextUsn);
        }
        finally
        {
            handle.Free();
        }
    }

    private static int DeviceIoControl<TInput>(
        SafeFileHandle handle,
        uint controlCode,
        TInput input,
        byte[] output,
        UsnWatchedFolderScope scope,
        string volumeRoot,
        string operation)
        where TInput : struct
    {
        var size = Marshal.SizeOf<TInput>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(input, pointer, false);
            return DeviceIoControl(handle, controlCode, pointer, size, output, output.Length, scope, volumeRoot, operation);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static int DeviceIoControl(
        SafeFileHandle handle,
        uint controlCode,
        IntPtr input,
        int inputLength,
        byte[] output,
        int outputLength,
        UsnWatchedFolderScope scope,
        string volumeRoot,
        string operation)
    {
        if (NativeDeviceIoControl(
                handle,
                controlCode,
                input,
                inputLength,
                output,
                outputLength,
                out var bytesReturned,
                IntPtr.Zero))
        {
            return bytesReturned;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorHandleEof)
        {
            return sizeof(long);
        }

        throw new UsnJournalOperationException(UsnJournalDiagnosticFormatter.DeviceIoControlFailed(
            scope.WatchedFolderId,
            scope.Path,
            volumeRoot,
            operation,
            error));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool NativeDeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        [Out] byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle hFile,
        ref FileIdDescriptor lpFileId,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public FileIdType Type;

        [FieldOffset(8)]
        public long FileId;

        [FieldOffset(8)]
        public FileId128 ExtendedFileId;

        public static FileIdDescriptor FromFileId(long fileId)
        {
            return new FileIdDescriptor
            {
                Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
                Type = FileIdType.FileId,
                FileId = fileId
            };
        }

        public static FileIdDescriptor FromExtendedFileId(byte[] fileId)
        {
            return new FileIdDescriptor
            {
                Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
                Type = FileIdType.ExtendedFileId,
                ExtendedFileId = new FileId128(BitConverter.ToInt64(fileId, 0), BitConverter.ToInt64(fileId, 8))
            };
        }
    }

    private enum FileIdType : uint
    {
        FileId = 0,
        ExtendedFileId = 2
    }

    private readonly record struct FileId128(long LowPart, long HighPart);

    private readonly record struct UsnRecord(
        ulong? FileReferenceNumber,
        byte[]? ExtendedFileReferenceNumber,
        uint Reason,
        uint FileAttributes);

    private sealed class UsnJournalOperationException(DurableChangeDetail detail) : Exception(detail.Reason)
    {
        public DurableChangeDetail Detail { get; } = detail;
    }
}
