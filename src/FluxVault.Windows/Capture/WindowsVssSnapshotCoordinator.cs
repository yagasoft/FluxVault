using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FluxVault.Windows.Capture;

internal sealed class WindowsVssSnapshotCoordinator : IVssSnapshotCoordinator
{
    private const int VssContextBackup = 0;
    private const int VssBackupTypeCopy = 5;
    private const int VssObjectSnapshot = 3;
    private const int VssWriterStateStable = 1;
    private const uint Infinite = 0xffffffff;

    public async Task<VssSnapshotResult> CreateSnapshotAsync(
        VssSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VssSnapshotResult.Failed("VSS snapshots are only available on Windows.");
        }

        return await CreateSnapshotCoreAsync(request, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<VssSnapshotResult> CreateSnapshotCoreAsync(
        VssSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        IVssBackupComponents? components = null;
        var snapshotId = Guid.Empty;
        var metadataFreed = false;
        var statusFreed = false;
        var released = false;
        var backupShouldComplete = false;

        async ValueTask CleanupAsync()
        {
            if (released)
            {
                return;
            }

            try
            {
                if (snapshotId != Guid.Empty)
                {
                    if (backupShouldComplete)
                    {
                        TryBackupComplete(components);
                    }
                    else
                    {
                        TryAbortBackup(components);
                    }

                    TryDeleteSnapshot(components, snapshotId);
                }
                else
                {
                    TryAbortBackup(components);
                }
            }
            finally
            {
                FreeWriterCollections(components, ref metadataFreed, ref statusFreed);
                Release(components);
                released = true;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        try
        {
            ThrowIfFailed(CreateVssBackupComponentsInternal(out components), "CreateVssBackupComponents");
            ThrowIfFailed(components.InitializeForBackup(null), "InitializeForBackup");
            ThrowIfFailed(components.SetContext(VssContextBackup), "SetContext");
            ThrowIfFailed(
                components.SetBackupState(false, false, VssBackupTypeCopy, false),
                "SetBackupState");

            await WaitForAsyncTokenAsync(
                    (out IVssAsync token) => components.GatherWriterMetadata(out token),
                    "GatherWriterMetadata",
                    cancellationToken)
                .ConfigureAwait(false);
            var writers = CollectWriterMetadata(components);

            ThrowIfFailed(components.StartSnapshotSet(out _), "StartSnapshotSet");
            ThrowIfFailed(components.AddToSnapshotSet(request.VolumeRoot, Guid.Empty, out snapshotId), "AddToSnapshotSet");

            await WaitForAsyncTokenAsync(
                    (out IVssAsync token) => components.PrepareForBackup(out token),
                    "PrepareForBackup",
                    cancellationToken)
                .ConfigureAwait(false);
            await WaitForAsyncTokenAsync(
                    (out IVssAsync token) => components.DoSnapshotSet(out token),
                    "DoSnapshotSet",
                    cancellationToken)
                .ConfigureAwait(false);
            await WaitForAsyncTokenAsync(
                    (out IVssAsync token) => components.GatherWriterStatus(out token),
                    "GatherWriterStatus",
                    cancellationToken)
                .ConfigureAwait(false);
            statusFreed = false;
            VerifyWriterStatus(components);

            var properties = default(VssSnapshotProperties);
            try
            {
                ThrowIfFailed(components.GetSnapshotProperties(snapshotId, ref properties), "GetSnapshotProperties");
                var snapshotDeviceObject = Marshal.PtrToStringUni(properties.SnapshotDeviceObject);
                if (string.IsNullOrWhiteSpace(snapshotDeviceObject))
                {
                    await CleanupAsync().ConfigureAwait(false);
                    return VssSnapshotResult.Failed("VSS snapshot properties did not include a snapshot device path.");
                }

                backupShouldComplete = true;
                return VssSnapshotResult.Created(
                    snapshotId,
                    snapshotDeviceObject,
                    writers,
                    "Writer-aware VSS requester snapshot created.",
                    CleanupAsync);
            }
            finally
            {
                VssFreeSnapshotPropertiesInternal(ref properties);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupAsync().ConfigureAwait(false);
            return VssSnapshotResult.Failed($"VSS requester failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VssWriterEvidence> CollectWriterMetadata(IVssBackupComponents components)
    {
        ThrowIfFailed(components.GetWriterMetadataCount(out var count), "GetWriterMetadataCount");
        var writers = new List<VssWriterEvidence>();
        for (uint index = 0; index < count; index++)
        {
            IVssExamineWriterMetadata? metadata = null;
            try
            {
                ThrowIfFailed(components.GetWriterMetadata(index, out _, out metadata), "GetWriterMetadata");
                ThrowIfFailed(
                    metadata.GetIdentity(out _, out _, out var writerName, out _, out _),
                    "GetWriterMetadata identity");
                ThrowIfFailed(
                    metadata.GetFileCounts(out var includeCount, out _, out _),
                    "GetWriterMetadata file counts");
                var coveredPaths = new List<string>();
                for (uint includeIndex = 0; includeIndex < includeCount; includeIndex++)
                {
                    var fileDescription = IntPtr.Zero;
                    try
                    {
                        ThrowIfFailed(metadata.GetIncludeFile(includeIndex, out fileDescription), "GetWriterMetadata include file");
                        var coveredPath = VssFileDescriptionReader.ReadCoveredPath(fileDescription);
                        if (!string.IsNullOrWhiteSpace(coveredPath))
                        {
                            coveredPaths.Add(coveredPath);
                        }
                    }
                    finally
                    {
                        if (fileDescription != IntPtr.Zero)
                        {
                            Marshal.Release(fileDescription);
                        }
                    }
                }

                writers.Add(new VssWriterEvidence(writerName, coveredPaths));
            }
            finally
            {
                if (metadata is not null)
                {
                    Marshal.FinalReleaseComObject(metadata);
                }
            }
        }

        return writers;
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWriterStatus(IVssBackupComponents components)
    {
        ThrowIfFailed(components.GetWriterStatusCount(out var count), "GetWriterStatusCount");
        for (uint index = 0; index < count; index++)
        {
            ThrowIfFailed(
                components.GetWriterStatus(
                    index,
                    out _,
                    out _,
                    out var writerName,
                    out var state,
                    out var failure),
                "GetWriterStatus");
            if (state != VssWriterStateStable || failure != 0)
            {
                throw new InvalidOperationException(
                    $"VSS writer {writerName} reported state {state} with HRESULT 0x{failure:X8}.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForAsyncTokenAsync(
        StartVssAsync start,
        string operation,
        CancellationToken cancellationToken)
    {
        IVssAsync? asyncToken = null;
        ThrowIfFailed(start(out asyncToken), operation);
        try
        {
            await Task.Run(
                    () =>
                    {
                        ThrowIfFailed(asyncToken!.Wait(Infinite), operation);
                        var reserved = 0;
                        ThrowIfFailed(asyncToken.QueryStatus(out var result, ref reserved), operation);
                        ThrowIfFailed(result, operation);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (asyncToken is not null)
            {
                Marshal.FinalReleaseComObject(asyncToken);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryBackupComplete(IVssBackupComponents? components)
    {
        if (components is null)
        {
            return;
        }

        try
        {
            WaitForAsyncTokenAsync(
                    (out IVssAsync token) => components.BackupComplete(out token),
                    "BackupComplete",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryDeleteSnapshot(IVssBackupComponents? components, Guid snapshotId)
    {
        if (components is null)
        {
            return;
        }

        try
        {
            _ = components.DeleteSnapshots(snapshotId, VssObjectSnapshot, true, out _, out _);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryAbortBackup(IVssBackupComponents? components)
    {
        if (components is null)
        {
            return;
        }

        try
        {
            _ = components.AbortBackup();
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void FreeWriterCollections(
        IVssBackupComponents? components,
        ref bool metadataFreed,
        ref bool statusFreed)
    {
        if (components is null)
        {
            return;
        }

        if (!metadataFreed)
        {
            try
            {
                _ = components.FreeWriterMetadata();
            }
            catch
            {
            }

            metadataFreed = true;
        }

        if (!statusFreed)
        {
            try
            {
                _ = components.FreeWriterStatus();
            }
            catch
            {
            }

            statusFreed = true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object? comObject)
    {
        if (comObject is not null)
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult >= 0)
        {
            return;
        }

        var message = Marshal.GetExceptionForHR(hresult)?.Message ?? "Unknown COM error.";
        throw new InvalidOperationException($"{operation} failed with HRESULT 0x{hresult:X8}: {message}");
    }

    private delegate int StartVssAsync(out IVssAsync asyncToken);

    [DllImport("vssapi.dll", PreserveSig = true)]
    private static extern int CreateVssBackupComponentsInternal(out IVssBackupComponents components);

    [DllImport("vssapi.dll", PreserveSig = true)]
    private static extern void VssFreeSnapshotPropertiesInternal(ref VssSnapshotProperties properties);

    [ComImport]
    [Guid("507C37B4-CF5B-4E95-B0AF-14EB9767467E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssAsync
    {
        [PreserveSig]
        int Cancel();

        [PreserveSig]
        int Wait(uint milliseconds);

        [PreserveSig]
        int QueryStatus(out int result, ref int reserved);
    }

    [ComImport]
    [Guid("902FCF7F-B7FD-42F8-81F1-B2E400B1E5BD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssExamineWriterMetadata
    {
        [PreserveSig]
        int GetIdentity(
            out Guid instanceId,
            out Guid writerId,
            [MarshalAs(UnmanagedType.BStr)] out string writerName,
            out int usage,
            out int source);

        [PreserveSig]
        int GetFileCounts(out uint includeFiles, out uint excludeFiles, out uint components);

        [PreserveSig]
        int GetIncludeFile(uint index, out IntPtr fileDescription);
    }

    [ComImport]
    [Guid("665C1D5F-C218-414D-A05D-7FEF5F9D5C86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVssBackupComponents
    {
        [PreserveSig]
        int GetWriterComponentsCount(out uint components);

        [PreserveSig]
        int GetWriterComponents(uint writerIndex, out IntPtr writer);

        [PreserveSig]
        int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? xml);

        [PreserveSig]
        int SetBackupState(
            [MarshalAs(UnmanagedType.Bool)] bool selectComponents,
            [MarshalAs(UnmanagedType.Bool)] bool backupBootableSystemState,
            int backupType,
            [MarshalAs(UnmanagedType.Bool)] bool partialFileSupport);

        [PreserveSig]
        int InitializeForRestore([MarshalAs(UnmanagedType.BStr)] string xml);

        [PreserveSig]
        int SetRestoreState(int restoreType);

        [PreserveSig]
        int GatherWriterMetadata(out IVssAsync asyncToken);

        [PreserveSig]
        int GetWriterMetadataCount(out uint writers);

        [PreserveSig]
        int GetWriterMetadata(uint writerIndex, out Guid instanceId, out IVssExamineWriterMetadata metadata);

        [PreserveSig]
        int FreeWriterMetadata();

        [PreserveSig]
        int AddComponent(
            Guid instanceId,
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName);

        [PreserveSig]
        int PrepareForBackup(out IVssAsync asyncToken);

        [PreserveSig]
        int AbortBackup();

        [PreserveSig]
        int GatherWriterStatus(out IVssAsync asyncToken);

        [PreserveSig]
        int GetWriterStatusCount(out uint writers);

        [PreserveSig]
        int FreeWriterStatus();

        [PreserveSig]
        int GetWriterStatus(
            uint writerIndex,
            out Guid instanceId,
            out Guid writerId,
            [MarshalAs(UnmanagedType.BStr)] out string writerName,
            out int state,
            out int failure);

        [PreserveSig]
        int SetBackupSucceeded(
            Guid instanceId,
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.Bool)] bool succeeded);

        [PreserveSig]
        int SetBackupOptions(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.LPWStr)] string backupOptions);

        [PreserveSig]
        int SetSelectedForRestore(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.Bool)] bool selectedForRestore);

        [PreserveSig]
        int SetRestoreOptions(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.LPWStr)] string restoreOptions);

        [PreserveSig]
        int SetAdditionalRestores(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.Bool)] bool additionalRestores);

        [PreserveSig]
        int SetPreviousBackupStamp(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.LPWStr)] string previousBackupStamp);

        [PreserveSig]
        int SaveAsXML([MarshalAs(UnmanagedType.BStr)] out string xml);

        [PreserveSig]
        int BackupComplete(out IVssAsync asyncToken);

        [PreserveSig]
        int AddAlternativeLocationMapping(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            [MarshalAs(UnmanagedType.LPWStr)] string filespec,
            [MarshalAs(UnmanagedType.Bool)] bool recursive,
            [MarshalAs(UnmanagedType.LPWStr)] string destination);

        [PreserveSig]
        int AddRestoreSubcomponent(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.LPWStr)] string subComponentLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string subComponentName,
            [MarshalAs(UnmanagedType.Bool)] bool repair);

        [PreserveSig]
        int SetFileRestoreStatus(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            int status);

        [PreserveSig]
        int AddNewTarget(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            [MarshalAs(UnmanagedType.Bool)] bool recursive,
            [MarshalAs(UnmanagedType.LPWStr)] string alternatePath);

        [PreserveSig]
        int SetRangesFilePath(
            Guid writerId,
            int componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string componentName,
            uint partialFile,
            [MarshalAs(UnmanagedType.LPWStr)] string rangesFile);

        [PreserveSig]
        int PreRestore(out IVssAsync asyncToken);

        [PreserveSig]
        int PostRestore(out IVssAsync asyncToken);

        [PreserveSig]
        int SetContext(int context);

        [PreserveSig]
        int StartSnapshotSet(out Guid snapshotSetId);

        [PreserveSig]
        int AddToSnapshotSet(
            [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
            Guid providerId,
            out Guid snapshotId);

        [PreserveSig]
        int DoSnapshotSet(out IVssAsync asyncToken);

        [PreserveSig]
        int DeleteSnapshots(
            Guid sourceObjectId,
            int sourceObjectType,
            [MarshalAs(UnmanagedType.Bool)] bool forceDelete,
            out int deletedSnapshots,
            out Guid nonDeletedSnapshotId);

        [PreserveSig]
        int ImportSnapshots(out IVssAsync asyncToken);

        [PreserveSig]
        int BreakSnapshotSet(Guid snapshotSetId);

        [PreserveSig]
        int GetSnapshotProperties(Guid snapshotId, ref VssSnapshotProperties properties);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VssSnapshotProperties
    {
        public Guid SnapshotId;
        public Guid SnapshotSetId;
        public int SnapshotsCount;
        public IntPtr SnapshotDeviceObject;
        public IntPtr OriginalVolumeName;
        public IntPtr OriginatingMachine;
        public IntPtr ServiceMachine;
        public IntPtr ExposedName;
        public IntPtr ExposedPath;
        public Guid ProviderId;
        public int SnapshotAttributes;
        public long CreationTimestamp;
        public int Status;
    }

    private static class VssFileDescriptionReader
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBstrDelegate(IntPtr thisPtr, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBoolDelegate(IntPtr thisPtr, [MarshalAs(UnmanagedType.Bool)] out bool value);

        public static string? ReadCoveredPath(IntPtr fileDescription)
        {
            var path = ReadBstr(fileDescription, methodIndex: 3);
            var filespec = ReadBstr(fileDescription, methodIndex: 4);
            var recursive = ReadBool(fileDescription, methodIndex: 5);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(filespec) || filespec == "*" || filespec == "*.*")
            {
                return recursive ? path : Path.Combine(path, "*");
            }

            return Path.Combine(path, filespec);
        }

        private static string? ReadBstr(IntPtr thisPtr, int methodIndex)
        {
            var method = GetMethod<GetBstrDelegate>(thisPtr, methodIndex);
            var hr = method(thisPtr, out var bstr);
            ThrowIfFailed(hr, "Read VSS writer file metadata");
            try
            {
                return Marshal.PtrToStringBSTR(bstr);
            }
            finally
            {
                if (bstr != IntPtr.Zero)
                {
                    Marshal.FreeBSTR(bstr);
                }
            }
        }

        private static bool ReadBool(IntPtr thisPtr, int methodIndex)
        {
            var method = GetMethod<GetBoolDelegate>(thisPtr, methodIndex);
            ThrowIfFailed(method(thisPtr, out var value), "Read VSS writer file metadata");
            return value;
        }

        private static TDelegate GetMethod<TDelegate>(IntPtr thisPtr, int methodIndex)
            where TDelegate : Delegate
        {
            var vtable = Marshal.ReadIntPtr(thisPtr);
            var method = Marshal.ReadIntPtr(vtable, methodIndex * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(method);
        }
    }
}
