namespace FluxVault.Windows.Capture;

internal sealed class WindowsVssSnapshotCoordinator : IVssSnapshotCoordinator
{
    private const int VssContextBackup = 0;
    private const int VssBackupTypeCopy = 5;

    private readonly Func<IWindowsVssBackupSession> sessionFactory;

#pragma warning disable CA1416
    public WindowsVssSnapshotCoordinator()
        : this(() => new WindowsVssBackupSession())
    {
    }
#pragma warning restore CA1416

    internal WindowsVssSnapshotCoordinator(Func<IWindowsVssBackupSession> sessionFactory)
    {
        this.sessionFactory = sessionFactory;
    }

    public async Task<VssSnapshotResult> CreateSnapshotAsync(
        VssSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VssSnapshotResult.Failed("VSS snapshots are only available on Windows.");
        }

        IWindowsVssBackupSession? session = null;
        var snapshotId = Guid.Empty;
        var released = false;
        var metadataFreed = false;
        var statusFreed = false;
        var backupShouldComplete = false;

        async ValueTask CleanupAsync()
        {
            if (released)
            {
                return;
            }

            try
            {
                if (session is not null)
                {
                    if (snapshotId != Guid.Empty)
                    {
                        if (backupShouldComplete)
                        {
                            await TryBackupCompleteAsync(session).ConfigureAwait(false);
                        }
                        else
                        {
                            TryAbortBackup(session);
                        }

                        TryDeleteSnapshot(session, snapshotId);
                    }
                    else
                    {
                        TryAbortBackup(session);
                    }
                }
            }
            finally
            {
                if (session is not null)
                {
                    FreeWriterCollections(session, ref metadataFreed, ref statusFreed);
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                released = true;
            }
        }

        try
        {
            session = sessionFactory();
            session.InitializeForBackup();
            session.SetContext(VssContextBackup);
            session.SetBackupState(
                selectComponents: false,
                backupBootableSystemState: false,
                backupType: VssBackupTypeCopy,
                partialFileSupport: false);

            await session.GatherWriterMetadataAsync(cancellationToken).ConfigureAwait(false);
            var writers = session.GetWriterMetadata();

            session.StartSnapshotSet();
            snapshotId = session.AddToSnapshotSet(request.VolumeRoot);

            await session.PrepareForBackupAsync(cancellationToken).ConfigureAwait(false);
            await session.DoSnapshotSetAsync(cancellationToken).ConfigureAwait(false);
            await session.GatherWriterStatusAsync(cancellationToken).ConfigureAwait(false);
            VerifyWriterStatuses(session.GetWriterStatuses());

            var snapshotDeviceObject = session.GetSnapshotDeviceObject(snapshotId);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupAsync().ConfigureAwait(false);
            return VssSnapshotResult.Failed($"VSS requester failed: {ex.Message}");
        }
    }

    private static void VerifyWriterStatuses(IReadOnlyList<VssWriterStatus> statuses)
    {
        foreach (var status in statuses)
        {
            if (status.State != VssWriterStatus.StableState || status.Failure != 0)
            {
                throw new InvalidOperationException(
                    $"VSS writer {status.WriterName} reported state {status.State} with HRESULT 0x{status.Failure:X8}.");
            }
        }
    }

    private static async Task TryBackupCompleteAsync(IWindowsVssBackupSession session)
    {
        try
        {
            await session.BackupCompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void TryDeleteSnapshot(IWindowsVssBackupSession session, Guid snapshotId)
    {
        try
        {
            session.DeleteSnapshot(snapshotId);
        }
        catch
        {
        }
    }

    private static void TryAbortBackup(IWindowsVssBackupSession session)
    {
        try
        {
            session.AbortBackup();
        }
        catch
        {
        }
    }

    private static void FreeWriterCollections(
        IWindowsVssBackupSession session,
        ref bool metadataFreed,
        ref bool statusFreed)
    {
        if (!metadataFreed)
        {
            try
            {
                session.FreeWriterMetadata();
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
                session.FreeWriterStatus();
            }
            catch
            {
            }

            statusFreed = true;
        }
    }
}
