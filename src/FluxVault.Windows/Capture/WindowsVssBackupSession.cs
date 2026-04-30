using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FluxVault.Windows.Capture;

internal interface IWindowsVssBackupSession : IAsyncDisposable
{
    void InitializeForBackup();

    void SetContext(int context);

    void SetBackupState(
        bool selectComponents,
        bool backupBootableSystemState,
        int backupType,
        bool partialFileSupport);

    Task GatherWriterMetadataAsync(CancellationToken cancellationToken);

    IReadOnlyList<VssWriterEvidence> GetWriterMetadata();

    void StartSnapshotSet();

    Guid AddToSnapshotSet(string volumeRoot);

    Task PrepareForBackupAsync(CancellationToken cancellationToken);

    Task DoSnapshotSetAsync(CancellationToken cancellationToken);

    Task GatherWriterStatusAsync(CancellationToken cancellationToken);

    IReadOnlyList<VssWriterStatus> GetWriterStatuses();

    string? GetSnapshotDeviceObject(Guid snapshotId);

    Task BackupCompleteAsync(CancellationToken cancellationToken);

    void DeleteSnapshot(Guid snapshotId);

    void AbortBackup();

    void FreeWriterMetadata();

    void FreeWriterStatus();
}

internal sealed record VssWriterStatus(string WriterName, int State, int Failure)
{
    public const int StableState = 1;
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsVssBackupSession : IWindowsVssBackupSession
{
    private const int VssObjectSnapshot = 3;
    private const uint Infinite = 0xffffffff;

    private IVssBackupComponents? components;

    public WindowsVssBackupSession()
    {
        ThrowIfFailed(CreateVssBackupComponentsInternal(out components), "CreateVssBackupComponents");
    }

    public void InitializeForBackup()
    {
        ThrowIfFailed(Components.InitializeForBackup(null), "InitializeForBackup");
    }

    public void SetContext(int context)
    {
        ThrowIfFailed(Components.SetContext(context), "SetContext");
    }

    public void SetBackupState(
        bool selectComponents,
        bool backupBootableSystemState,
        int backupType,
        bool partialFileSupport)
    {
        ThrowIfFailed(
            Components.SetBackupState(
                selectComponents,
                backupBootableSystemState,
                backupType,
                partialFileSupport),
            "SetBackupState");
    }

    public Task GatherWriterMetadataAsync(CancellationToken cancellationToken)
    {
        return WaitForAsyncTokenAsync(
            (out IVssAsync token) => Components.GatherWriterMetadata(out token),
            "GatherWriterMetadata",
            cancellationToken);
    }

    public IReadOnlyList<VssWriterEvidence> GetWriterMetadata()
    {
        ThrowIfFailed(Components.GetWriterMetadataCount(out var count), "GetWriterMetadataCount");
        var writers = new List<VssWriterEvidence>();
        for (uint index = 0; index < count; index++)
        {
            IVssExamineWriterMetadata? metadata = null;
            try
            {
                ThrowIfFailed(Components.GetWriterMetadata(index, out _, out metadata), "GetWriterMetadata");
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

    public void StartSnapshotSet()
    {
        ThrowIfFailed(Components.StartSnapshotSet(out _), "StartSnapshotSet");
    }

    public Guid AddToSnapshotSet(string volumeRoot)
    {
        ThrowIfFailed(Components.AddToSnapshotSet(volumeRoot, Guid.Empty, out var snapshotId), "AddToSnapshotSet");
        return snapshotId;
    }

    public Task PrepareForBackupAsync(CancellationToken cancellationToken)
    {
        return WaitForAsyncTokenAsync(
            (out IVssAsync token) => Components.PrepareForBackup(out token),
            "PrepareForBackup",
            cancellationToken);
    }

    public Task DoSnapshotSetAsync(CancellationToken cancellationToken)
    {
        return WaitForAsyncTokenAsync(
            (out IVssAsync token) => Components.DoSnapshotSet(out token),
            "DoSnapshotSet",
            cancellationToken);
    }

    public Task GatherWriterStatusAsync(CancellationToken cancellationToken)
    {
        return WaitForAsyncTokenAsync(
            (out IVssAsync token) => Components.GatherWriterStatus(out token),
            "GatherWriterStatus",
            cancellationToken);
    }

    public IReadOnlyList<VssWriterStatus> GetWriterStatuses()
    {
        ThrowIfFailed(Components.GetWriterStatusCount(out var count), "GetWriterStatusCount");
        var statuses = new List<VssWriterStatus>();
        for (uint index = 0; index < count; index++)
        {
            ThrowIfFailed(
                Components.GetWriterStatus(
                    index,
                    out _,
                    out _,
                    out var writerName,
                    out var state,
                    out var failure),
                "GetWriterStatus");
            statuses.Add(new VssWriterStatus(writerName, state, failure));
        }

        return statuses;
    }

    public string? GetSnapshotDeviceObject(Guid snapshotId)
    {
        var properties = default(VssSnapshotProperties);
        try
        {
            ThrowIfFailed(Components.GetSnapshotProperties(snapshotId, ref properties), "GetSnapshotProperties");
            return Marshal.PtrToStringUni(properties.SnapshotDeviceObject);
        }
        finally
        {
            VssFreeSnapshotPropertiesInternal(ref properties);
        }
    }

    public Task BackupCompleteAsync(CancellationToken cancellationToken)
    {
        return WaitForAsyncTokenAsync(
            (out IVssAsync token) => Components.BackupComplete(out token),
            "BackupComplete",
            cancellationToken);
    }

    public void DeleteSnapshot(Guid snapshotId)
    {
        _ = Components.DeleteSnapshots(snapshotId, VssObjectSnapshot, true, out _, out _);
    }

    public void AbortBackup()
    {
        _ = Components.AbortBackup();
    }

    public void FreeWriterMetadata()
    {
        _ = Components.FreeWriterMetadata();
    }

    public void FreeWriterStatus()
    {
        _ = Components.FreeWriterStatus();
    }

    public ValueTask DisposeAsync()
    {
        if (components is not null)
        {
            Marshal.FinalReleaseComObject(components);
            components = null;
        }

        return ValueTask.CompletedTask;
    }

    private IVssBackupComponents Components
    {
        get
        {
            if (components is null)
            {
                throw new ObjectDisposedException(nameof(WindowsVssBackupSession));
            }

            return components;
        }
    }

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

    internal static void ThrowIfFailed(int hresult, string operation)
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

            return recursive ? Path.Combine(path, "**", filespec) : Path.Combine(path, filespec);
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
