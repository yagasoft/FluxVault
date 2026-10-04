using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Testing;
using FluxVault.Windows.Security;
using Npgsql;

namespace FluxVault.TestHost;

/// <summary>Native single-installation command flow in the explicitly owned Windows/SSPI fixture.</summary>
internal static class WindowsSingleVaultProbe
{
    internal static async Task<int> RunAsync(string configurationPath, string actor)
    {
        var fixture = JsonSerializer.Deserialize<WindowsDatabaseProbeConfiguration>(await File.ReadAllTextAsync(configurationPath))!;
        fixture.Validate();
        using var identity = WindowsIdentity.GetCurrent();
        if (!fixture.Actors.TryGetValue(actor, out var expected) || identity.User?.Value != expected) throw new UnauthorizedAccessException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var pipe = "FluxVault.Tests." + fixture.FixtureId;
        if (actor == "System") return await RunServerAsync(fixture, pipe, deadline);
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(pipe, fixture.Actors["System"]));
        if (actor == "B")
        {
            var denied = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), deadline.Token);
            if (denied.Success || denied.ErrorCode != FluxVaultIpcErrorCode.Denied || denied.VaultId is not null || denied.Status is not null)
                throw new InvalidOperationException("Ungrant user received installed identity or data.");
            var commands = new[] { FluxVaultIpcCommand.GetRepositoryHealth, FluxVaultIpcCommand.PreviewRetention, FluxVaultIpcCommand.RunRetentionNow,
                FluxVaultIpcCommand.RunRepositoryScrub, FluxVaultIpcCommand.RunRestoreRehearsal, FluxVaultIpcCommand.PreviewMirrorRepair,
                FluxVaultIpcCommand.RunMirrorRepair, FluxVaultIpcCommand.PreviewMirrorRebalance, FluxVaultIpcCommand.RunMirrorRebalance,
                FluxVaultIpcCommand.PreviewMirrorDrain, FluxVaultIpcCommand.RunMirrorDrain, FluxVaultIpcCommand.ExportDiagnostics,
                FluxVaultIpcCommand.PreviewRestoreSelection, FluxVaultIpcCommand.RunRestoreSelection,
                FluxVaultIpcCommand.SetProtectionPaused, FluxVaultIpcCommand.GetSyncStatus };
            foreach (var command in commands)
            {
                var response = await client.SendAsync(new(command, null, null, null, Path.Combine(fixture.Root, "output-B"), MirrorNodeId: "first",
                    VaultId: new(Guid.ParseExact(fixture.FixtureId, "N")), ExpectedVaultRevision: 1, OperationId: Guid.NewGuid()), deadline.Token);
                if (response.Success || response.ErrorCode != FluxVaultIpcErrorCode.Denied ||
                    response.VaultId is { } disclosed && disclosed.Value != Guid.ParseExact(fixture.FixtureId, "N") ||
                    response.RepositoryHealth is not null || response.RepositoryScrub is not null || response.RestoreRehearsal is not null ||
                    response.MirrorRepair is not null || response.MirrorRebalance is not null || response.RetentionPreview is not null || response.RetentionResult is not null ||
                    response.DiagnosticsExport is not null || response.OutputPath is not null || response.RestoreResult is not null || response.RestoreSelection is not null || response.Status is not null)
                    throw new InvalidOperationException("Ungrant user received maintenance data or admission.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Denied = true, NoIdentityOrHistory = true, MaintenanceDenied = commands.Length }));
            return 0;
        }
        var checks = new List<string>();
        var output = Path.Combine(fixture.Root, "output-A");
        var source = Path.Combine(output, "office"); var cad = Path.Combine(output, "cad");
        var destination = Path.Combine(output, "destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(cad); Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var file = Path.Combine(source, "document.docx");
        var drawing = Path.Combine(cad, "drawing.dwg");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 251)).ToArray(), deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "notes.docx"), "generated nested working bytes", deadline.Token);
        await File.WriteAllBytesAsync(drawing, Enumerable.Range(0, 512 * 1024).Select(index => (byte)(index % 239)).ToArray(), deadline.Token);
        var initial = await Send(FluxVaultIpcRequest.GetStatus());
        if (initial.VaultId is not { IsValid: true } id || initial.VaultRevision is not > 0 || initial.Status is null)
            throw new InvalidOperationException("Initial authorised status did not return the installed binding.");
        var revision = initial.VaultRevision.Value;
        Check(initial.Status.LastMessage.Contains("Automatic protection unavailable; manual backup available", StringComparison.Ordinal),
            "product composition truthfully reports blocked automatic protection");
        checks.Add("native creator receives the installed identity and configuration");
        var baseline = initial.Status.Configuration;
        var draft = baseline with
        {
            WatchedFolders = [new("office", source, true, ["*.docx"], [], CompressionPreference.Off, ResourceProfile.Balanced, true),
                new("cad", cad, true, ["*.dwg"], [], CompressionPreference.Off, ResourceProfile.Balanced, true)]
        };
        var save = Bind(FluxVaultIpcRequest.SaveConfiguration(draft));
        var saved = await Send(save); revision = saved.VaultRevision!.Value;
        var accepted = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        Check(JsonSerializer.Serialize(accepted.Status!.Configuration with { WatchedFolders = baseline.WatchedFolders }) == JsonSerializer.Serialize(baseline),
            "real PostgreSQL save preserves every untouched configuration property");
        Check(accepted.Status.Configuration.WatchedFolders.Count == 2, "multiple protected folders and file types persist in the single vault");
        var stale = await client.SendAsync(save with { OperationId = Guid.NewGuid() }, deadline.Token);
        Check(!stale.Success && stale.ErrorCode == FluxVaultIpcErrorCode.StaleRevision, "stale save cannot replace accepted configuration");
        var wrong = await client.SendAsync(Bind(FluxVaultIpcRequest.ListVersions()) with { VaultId = VaultId.New() }, deadline.Token);
        Check(!wrong.Success && wrong.ErrorCode == FluxVaultIpcErrorCode.Denied && wrong.Versions is null, "wrong repository identity cannot read history");
        var backupRequest = Bind(FluxVaultIpcRequest.RunBackupNow());
        var backedUp = await Send(backupRequest);
        Check(backedUp.Backup is { Success: true, CapturedFileCount: 3 }, "native caller files are captured through the authenticated executor");
        var repeated = await Send(backupRequest);
        Check(repeated.OperationId == backedUp.OperationId && JsonSerializer.Serialize(repeated.Backup) == JsonSerializer.Serialize(backedUp.Backup),
            "duplicate backup returns its durable receipt without repeated execution");
        var history = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        var fileVersion = history.Versions!.Single(item => item.SourcePath == file && item.EntryKind == RepositoryEntryKind.File);
        var folderVersion = history.Versions!.Where(item => item.SourcePath == source && item.EntryKind == RepositoryEntryKind.Folder)
            .OrderByDescending(item => item.CapturedAtUtc).First();
        Check(history.Versions!.Count(item => item.EntryKind == RepositoryEntryKind.File &&
            (item.SourcePath.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
             item.SourcePath.StartsWith(cad + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) == 3,
            "history contains all three captured files from both source folders");
        Check(history.Versions!.Count(item => item.EntryKind == RepositoryEntryKind.File &&
            item.SourcePath.StartsWith(Path.Combine(output, "historical-fallback") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == 2,
            "legacy fallback fixture retains exactly two independent file histories");
        var inspection = await Send(Bind(FluxVaultIpcRequest.InspectVersion(fileVersion.VersionId)));
        Check(inspection.Inspection is not null, "authorised history inspection succeeds");
        var previewCache = new WindowsUserPreviewCache(Path.Combine(output, "preview-cache"));
        var previewFile = previewCache.Allocate(file, new());
        var previewRequest = Bind(FluxVaultIpcRequest.RestoreVersionPreview(fileVersion.VersionId) with { OutputPath = previewFile });
        var preview = await Send(previewRequest);
        Check(preview.RestoreResult?.VerifiedLogicalBytes == new FileInfo(file).Length && Hash(file) == Hash(previewFile),
            "preview uses caller-authorised publication and independently verified bytes");
        previewCache.MakeReadOnly(previewFile);
        Check(File.GetAttributes(previewFile).HasFlag(FileAttributes.ReadOnly), "the ordinary caller prepares a read-only preview copy");
        var previewReplay = await Send(previewRequest);
        Check(previewReplay.OperationId == preview.OperationId && previewReplay.OutputPath == previewFile,
            "preview replay returns the original durable result");
        var existingPreview = await client.SendAsync(previewRequest with { OperationId = Guid.NewGuid() }, deadline.Token);
        Check(!existingPreview.Success && Hash(file) == Hash(previewFile), "preview refuses an existing destination without replacing it");
        var folderPreviewPath = Path.Combine(destination, "folder-preview");
        var folderPreview = await client.SendAsync(Bind(FluxVaultIpcRequest.RestoreVersionPreview(folderVersion.VersionId) with
            { OutputPath = folderPreviewPath }), deadline.Token);
        Check(!folderPreview.Success && folderPreview.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest &&
            !Directory.Exists(folderPreviewPath) && !File.Exists(folderPreviewPath), "folder preview is refused before output preparation");
        var recoveredFile = Path.Combine(destination, "recovered.docx");
        var restored = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(fileVersion.VersionId, recoveredFile)));
        Check(restored.RestoreResult?.VerifiedLogicalBytes == new FileInfo(file).Length && Hash(file) == Hash(recoveredFile),
            "standalone recovery publishes independently verified bytes to the caller");
        await File.AppendAllTextAsync(recoveredFile, " edited", deadline.Token);
        var recoveredFolder = Path.Combine(destination, "recovered-folder");
        var folderRestore = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(folderVersion.VersionId, recoveredFolder)));
        Check(folderRestore.RestoreResult is { RestoredFileCount: 2 } && Hash(file) == Hash(Path.Combine(recoveredFolder, "document.docx")) &&
            Hash(Path.Combine(source, "nested", "notes.docx")) == Hash(Path.Combine(recoveredFolder, "nested", "notes.docx")),
            "nested folder recovery preserves independently verified bytes");
        var after = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        Check(after.Status?.LastCaptureUtc is not null && after.Status.BackupRuntime?.CapturedFileCount == 3, "new status requests retain completed backup state");
        var officeOnly = accepted.Status.Configuration with
        {
            WatchedFolders = accepted.Status.Configuration.WatchedFolders.Where(folder => folder.Id == "office").ToArray()
        };
        var purgeRequest = Bind(FluxVaultIpcRequest.SaveConfiguration(officeOnly, purgeRemovedSelections: true,
            removedSelections: [new(cad, RepositoryPurgeScopeKind.RecursiveFolder)],
            preservedSelections: [new(source, RepositoryPurgeScopeKind.RecursiveFolder)]));
        var invalidDraft = officeOnly with { IsEnabled = !officeOnly.IsEnabled };
        foreach (var malformed in new[]
        {
            purgeRequest with { RemovedSelections = [] },
            purgeRequest with { PreservedSelections = null },
            purgeRequest with { RemovedSelections = [new("relative-cad", RepositoryPurgeScopeKind.RecursiveFolder)] },
            purgeRequest with { PreservedSelections = [new(source, (RepositoryPurgeScopeKind)999)] },
            purgeRequest with { RemovedSelections = [null!] }
        })
        {
            var invalid = malformed with { Configuration = invalidDraft, OperationId = Guid.NewGuid() };
            var refusal = await client.SendAsync(invalid, deadline.Token);
            var unchanged = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
            var noReceipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
                VaultId: id, OperationId: invalid.OperationId), deadline.Token);
            Check(!refusal.Success && refusal.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest && unchanged.VaultRevision == revision &&
                JsonSerializer.Serialize(unchanged.Status!.Configuration) == JsonSerializer.Serialize(accepted.Status.Configuration) &&
                noReceipt.ErrorCode == FluxVaultIpcErrorCode.Denied,
                "malformed purge scopes are refused before configuration CAS and receipt admission");
        }
        var purged = await Send(purgeRequest);
        Check(purged.Purge is { Success: true, PurgedVersionCount: > 0 } && purged.VaultRevision == revision + 1,
            "combined save commits once and removes only confirmed CAD history");
        revision = purged.VaultRevision!.Value;
        var purgeReplay = await Send(purgeRequest);
        var purgeReceipt = await Send(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null, VaultId: id, OperationId: purgeRequest.OperationId));
        Check(JsonSerializer.Serialize(purged) == JsonSerializer.Serialize(purgeReplay) && JsonSerializer.Serialize(purged) == JsonSerializer.Serialize(purgeReceipt),
            "save-purge replay and receipt lookup return the original retained result");
        var remaining = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        Check(!remaining.Versions!.Any(version => version.SourcePath == drawing) && remaining.Versions!.Any(version => version.VersionId == fileVersion.VersionId),
            "CAD history is gone and preserved Office history remains");
        var recoveredAfterPurge = Path.Combine(destination, "after-purge.docx");
        var verifiedAfterPurge = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(fileVersion.VersionId, recoveredAfterPurge)));
        Check(verifiedAfterPurge.RestoreResult?.VerifiedLogicalBytes == new FileInfo(file).Length && Hash(file) == Hash(recoveredAfterPurge),
            "preserved Office bytes remain independently recoverable after purge");

        // Reintroduce CAD explicitly, then capture a working copy that inherits its
        // history. This exercises preservation against the real PostgreSQL closure.
        var reintroduced = await Send(Bind(FluxVaultIpcRequest.SaveConfiguration(accepted.Status.Configuration)));
        revision = reintroduced.VaultRevision!.Value;
        await Send(Bind(FluxVaultIpcRequest.RunBackupNow()));
        var inheritedCopy = Path.Combine(source, "drawing-copy.docx");
        File.Copy(drawing, inheritedCopy);
        var copyBackup = await Send(Bind(FluxVaultIpcRequest.RunBackupNow()));
        Check(copyBackup.Backup is { Success: true, CapturedFileCount: 1 }, "caller working copy is captured after its CAD parent");
        var beforeRefusal = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        var copyVersion = beforeRefusal.Versions!.Single(version => version.SourcePath == inheritedCopy && version.EntryKind == RepositoryEntryKind.File);
        var conflicting = Bind(FluxVaultIpcRequest.SaveConfiguration(officeOnly, purgeRemovedSelections: true,
            removedSelections: [new(cad, RepositoryPurgeScopeKind.RecursiveFolder)],
            preservedSelections: [new(source, RepositoryPurgeScopeKind.RecursiveFolder)]));
        var refusedPurge = await Send(conflicting);
        Check(refusedPurge.Purge is { Success: false } && refusedPurge.Purge.ErrorMessage!.Contains("preserved", StringComparison.OrdinalIgnoreCase) &&
            refusedPurge.VaultRevision == revision + 1, "preservation conflict reports saved configuration and failed purge");
        revision = refusedPurge.VaultRevision!.Value;
        var afterRefusal = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        Check(beforeRefusal.Versions!.Select(version => version.VersionId).Order().SequenceEqual(afterRefusal.Versions!.Select(version => version.VersionId).Order()),
            "preservation refusal leaves all PostgreSQL history unchanged");
        var refusedReplay = await Send(conflicting);
        Check(JsonSerializer.Serialize(refusedReplay) == JsonSerializer.Serialize(refusedPurge), "failed purge replay never executes the destructive effect again");
        var copyRecovered = Path.Combine(destination, "preserved-copy.docx");
        var copyRecovery = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(copyVersion.VersionId, copyRecovered)));
        Check(copyRecovery.RestoreResult?.VerifiedLogicalBytes == new FileInfo(drawing).Length && Hash(drawing) == Hash(copyRecovered),
            "preserved inherited working bytes remain independently recoverable after refusal");

        var beforeMaintenance = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        foreach (var malformed in new[]
        {
            FluxVaultIpcRequest.PreviewMirrorRepair("unknown"), FluxVaultIpcRequest.RunMirrorRepair("unknown"),
            FluxVaultIpcRequest.PreviewMirrorRepair(" "), FluxVaultIpcRequest.RunMirrorRepair(" "),
            FluxVaultIpcRequest.PreviewMirrorDrain("unknown"), FluxVaultIpcRequest.PreviewMirrorDrain("first") with { MirrorNodeId = null }
        })
        {
            var invalid = Bind(malformed);
            var refusal = await client.SendAsync(invalid, deadline.Token);
            var unchanged = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
            var unchangedHistory = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
            var noReceipt = invalid.OperationId is { } operation ? await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus,
                null, null, null, null, VaultId: id, OperationId: operation), deadline.Token) : null;
            Check(!refusal.Success && refusal.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest &&
                refusal.ErrorMessage!.Contains("Choose an enabled mirror", StringComparison.Ordinal) && unchanged.VaultRevision == revision &&
                JsonSerializer.Serialize(unchanged.Status!.Configuration) == JsonSerializer.Serialize(beforeMaintenance.Status!.Configuration) &&
                afterRefusal.Versions!.Select(version => version.VersionId).Order().SequenceEqual(unchangedHistory.Versions!.Select(version => version.VersionId).Order()) &&
                (noReceipt is null || noReceipt.ErrorCode == FluxVaultIpcErrorCode.Denied),
                "invalid mirror selections are refused before receipt admission without changing configuration or history");
        }
        var healthBefore = await Send(Bind(FluxVaultIpcRequest.GetRepositoryHealth()));
        Check(healthBefore.RepositoryHealth is not null, "authorised repository health loads its protected state");
        var scrub = await Maintenance(FluxVaultIpcRequest.RunRepositoryScrub());
        Check(scrub.RepositoryScrub is { HealthState: RepositoryHealthState.Healthy, IssueCount: 0, CheckedChunkCount: > 0 },
            "native scrub verifies real PostgreSQL repository chunks");
        var rehearsal = await Maintenance(FluxVaultIpcRequest.RunRestoreRehearsal());
        Check(rehearsal.RestoreRehearsal is { HealthState: RepositoryHealthState.Healthy, RehearsedVersionCount: > 0, FailedVersionCount: 0 },
            "native rehearsal verifies recovery in protected temporary storage");
        var repairPreview = await Send(Bind(FluxVaultIpcRequest.PreviewMirrorRepair("FIRST")));
        Check(repairPreview.MirrorRepair is { IsPreview: true, HealthState: RepositoryHealthState.Healthy, RequestedMirrorNodeId: "FIRST" } &&
            repairPreview.MirrorRepair.Nodes.Select(node => node.NodeId).Order().SequenceEqual(new[] { "first", "second" }),
            "repair preview accepts the configured selector case-insensitively and reports both mirrors");
        var repair = await Maintenance(FluxVaultIpcRequest.RunMirrorRepair());
        Check(repair.MirrorRepair is { IsPreview: false, HealthState: RepositoryHealthState.Healthy } && repair.MirrorRepair.Nodes.Count == 2,
            "explicit all-mirror repair completes through the authorised service");
        var rebalancePreview = await Send(Bind(new(FluxVaultIpcCommand.PreviewMirrorRebalance, null, null, null, null)));
        Check(rebalancePreview.MirrorRebalance is { IsPreview: true, HealthState: RepositoryHealthState.Healthy }, "mirror placement preview remains available");
        var rebalance = await Maintenance(new(FluxVaultIpcCommand.RunMirrorRebalance, null, null, null, null));
        Check(rebalance.MirrorRebalance is { IsPreview: false, HealthState: RepositoryHealthState.Healthy }, "mirror placement executes through its durable receipt");
        var drainPreview = await Send(Bind(FluxVaultIpcRequest.PreviewMirrorDrain("first")));
        Check(drainPreview.MirrorRebalance is { IsPreview: true, Operation: MirrorRebalanceOperation.Drain, RequestedMirrorNodeId: "first" },
            "mirror drain preview reports the selected destination without disabling it");
        var retainedHealth = await Send(Bind(FluxVaultIpcRequest.GetRepositoryHealth()));
        Check(retainedHealth.RepositoryHealth is { LastScrub: not null, LastRestoreRehearsal: not null, LastMirrorRepair: not null, LastMirrorRebalance: not null },
            "health retains each report after concurrent-capable snapshot publication");

        // Only disposable fixture history is pruned. A new working version makes
        // retention's real effect observable; the final recovery uses independent SHA-256.
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 256 * 1024).Select(index => (byte)((index * 7 + 19) % 253)).ToArray(), deadline.Token);
        await Send(Bind(FluxVaultIpcRequest.RunBackupNow()));
        var beforeRetention = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        var latest = beforeRetention.Versions!.Where(version => version.SourcePath == file).OrderByDescending(version => version.CapturedAtUtc).First();
        var retentionConfiguration = beforeMaintenance.Status!.Configuration with
            { RetentionPolicy = new(true, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1) };
        var retentionSave = await Send(Bind(FluxVaultIpcRequest.SaveConfiguration(retentionConfiguration))); revision = retentionSave.VaultRevision!.Value;
        var retentionPreview = await Send(Bind(FluxVaultIpcRequest.PreviewRetention()));
        Check(retentionPreview.RetentionPreview is { PrunableVersionCount: > 0 }, "retention preview identifies disposable superseded versions");
        await Send(Bind(FluxVaultIpcRequest.GetStatus())); // Populate the actual runtime history cache before deletion.
        var retained = await Maintenance(FluxVaultIpcRequest.RunRetentionNow());
        Check(retained.RetentionResult is { PrunedVersionCount: > 0 }, "retention actually prunes only owned fixture history");
        var retainedHistory = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        var retainedStatus = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        Check(retainedHistory.Versions!.Any(version => version.VersionId == latest.VersionId) &&
            retainedHistory.Versions!.Count < beforeRetention.Versions!.Count &&
            retainedStatus.Status!.RecentVersions.All(version => retainedHistory.Versions.Any(kept => kept.VersionId == version.VersionId)),
            "history and cached status reflect retention while retaining the latest working version");
        var recoveredAfterRetention = Path.Combine(destination, "after-retention.docx");
        var verifiedAfterRetention = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(latest.VersionId, recoveredAfterRetention)));
        Check(verifiedAfterRetention.RestoreResult?.VerifiedLogicalBytes == new FileInfo(file).Length && Hash(file) == Hash(recoveredAfterRetention),
            "latest working bytes remain independently recoverable after maintenance and retention");

        var beforeDrain = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        var drained = await Maintenance(FluxVaultIpcRequest.RunMirrorDrain("FIRST"));
        Check(drained.MirrorRebalance is { IsCompletedDrain: true, RequestedMirrorNodeId: "FIRST" } && drained.VaultRevision == revision + 1,
            "native mirror drain publishes its completed result and incremented configuration revision");
        revision = drained.VaultRevision!.Value;
        var afterDrain = await Send(Bind(FluxVaultIpcRequest.GetStatus()));
        var expectedAfterDrain = beforeDrain.Status!.Configuration with { MirrorSet = new(
            beforeDrain.Status.Configuration.MirrorSet.Nodes.Select(node => node.Id == "first" ? node with { IsEnabled = false } : node).ToArray(),
            beforeDrain.Status.Configuration.MirrorSet.PlacementPolicy) };
        Check(afterDrain.VaultRevision == revision && JsonSerializer.Serialize(expectedAfterDrain) == JsonSerializer.Serialize(afterDrain.Status!.Configuration),
            "native drain changes only the selected mirror enablement and preserves every untouched setting");
        var staleAfterDrain = await client.SendAsync(Bind(FluxVaultIpcRequest.SaveConfiguration(beforeDrain.Status.Configuration)) with
            { ExpectedVaultRevision = revision - 1 }, deadline.Token);
        Check(!staleAfterDrain.Success && staleAfterDrain.ErrorCode == FluxVaultIpcErrorCode.StaleRevision,
            "an old save cannot re-enable the drained mirror");
        var finalDrain = Bind(FluxVaultIpcRequest.RunMirrorDrain("second"));
        var finalRefusal = await client.SendAsync(finalDrain, deadline.Token);
        var finalReceipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
            VaultId: id, OperationId: finalDrain.OperationId), deadline.Token);
        Check(!finalRefusal.Success && finalRefusal.ErrorCode == FluxVaultIpcErrorCode.InvalidRequest && finalReceipt.ErrorCode == FluxVaultIpcErrorCode.Denied,
            "the final enabled mirror is refused before new receipt admission");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 256 * 1024).Select(index => (byte)((index * 11 + 23) % 251)).ToArray(), deadline.Token);
        var backupAfterDrain = await Send(Bind(FluxVaultIpcRequest.RunBackupNow()));
        Check(backupAfterDrain.Backup is { Success: true, CapturedFileCount: 1 }, "backup uses the remaining enabled mirror after the drain revision");
        var historyAfterDrain = await Send(Bind(FluxVaultIpcRequest.ListVersions()));
        var latestAfterDrain = historyAfterDrain.Versions!.Where(version => version.SourcePath == file).OrderByDescending(version => version.CapturedAtUtc).First();
        var recoveredAfterDrain = Path.Combine(destination, "after-drain.docx");
        var verifiedAfterDrain = await Send(Bind(FluxVaultIpcRequest.RestoreVersion(latestAfterDrain.VersionId, recoveredAfterDrain)));
        Check(verifiedAfterDrain.RestoreResult?.VerifiedLogicalBytes == new FileInfo(file).Length && Hash(file) == Hash(recoveredAfterDrain),
            "save and drain revisions still permit backup, history and independent verified recovery");
        var diagnosticsRequest = Bind(FluxVaultIpcRequest.ExportDiagnostics(destination));
        var diagnostics = await Send(diagnosticsRequest);
        var diagnosticsPath = Path.Combine(destination, $"fluxvault-diagnostics-{diagnosticsRequest.OperationId:N}.json");
        Check(diagnostics.OutputPath == diagnosticsPath && diagnostics.DiagnosticsExport?.OutputPath == diagnosticsPath &&
            diagnostics.DiagnosticsExport.Warnings is not null, "native diagnostics returns its exact operation-specific destination");
        using (var report = JsonDocument.Parse(await File.ReadAllBytesAsync(diagnosticsPath, deadline.Token)))
        {
            var document = report.RootElement;
            Check(document.GetProperty("formatVersion").GetInt32() == 1 && document.GetProperty("vaultId").GetString() == id.ToString() &&
                document.GetProperty("configurationRevision").GetInt64() == revision, "diagnostics JSON retains its admitted repository identity and revision");
            Check(document.GetProperty("status").GetProperty("lastMessage").GetString()!.Contains("Automatic protection unavailable", StringComparison.Ordinal),
                "diagnostics truthfully explains unavailable automatic protection");
            Check(document.GetProperty("status").GetProperty("recentVersions").GetArrayLength() > 0, "diagnostics contains actual repository status and inventory");
        }
        await File.WriteAllTextAsync(diagnosticsPath, "caller edited report", deadline.Token);
        var diagnosticsReplay = await Send(diagnosticsRequest);
        var diagnosticsReceipt = await Send(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null, VaultId: id, OperationId: diagnosticsRequest.OperationId));
        Check(JsonSerializer.Serialize(diagnostics) == JsonSerializer.Serialize(diagnosticsReplay) &&
            JsonSerializer.Serialize(diagnostics) == JsonSerializer.Serialize(diagnosticsReceipt) && await File.ReadAllTextAsync(diagnosticsPath, deadline.Token) == "caller edited report",
            "caller can edit published report and exact replay/status does not overwrite it");
        File.Delete(diagnosticsPath);
        await Send(diagnosticsRequest);
        Check(!File.Exists(diagnosticsPath), "exact replay does not recreate a deleted diagnostics report");
        var another = await Send(Bind(FluxVaultIpcRequest.ExportDiagnostics(destination)));
        Check(another.OutputPath != diagnosticsPath && File.Exists(another.OutputPath), "distinct diagnostics operations create separate reports");
        var privateDirectory = Path.Combine(fixture.Root, "catalogue", "single", "state");
        var deniedDiagnosticsRequest = Bind(FluxVaultIpcRequest.ExportDiagnostics(privateDirectory));
        var deniedDiagnostics = await client.SendAsync(deniedDiagnosticsRequest, deadline.Token);
        Check(!deniedDiagnostics.Success, "SYSTEM service cannot export into a directory denied to the native caller");
        var deniedDiagnosticsReceipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null,
            VaultId: id, OperationId: deniedDiagnosticsRequest.OperationId), deadline.Token);
        Check(!deniedDiagnosticsReceipt.Success && deniedDiagnosticsReceipt.ErrorCode == FluxVaultIpcErrorCode.OutcomeUnknown,
            "failed admitted diagnostics does not report a completed export or repeat its effect");
        await WindowsSelectionRecoveryProbe.RunAsync(client, Bind, Send, output, checks, deadline.Token);
        revision = await WindowsProtectionStateProbe.RunAsync(client, Bind, Send, file, destination, checks, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Passed = checks.Count, Checks = checks, SingleVault = true, VaultId = id,
            CallerCanEditPublished = true, IndependentSha256 = true, ActualCatalogueAndExecutor = true,
            MaintenanceCommandsVerified = true, DrainCommandsVerified = true, DiagnosticsCommandsVerified = true, SelectionCommandsVerified = true,
            ProtectionStateCommandsVerified = true }));
        return 0;

        FluxVaultIpcRequest Bind(FluxVaultIpcRequest request) => request with { VaultId = id,
            ExpectedVaultRevision = PostgreSqlVaultCatalogue.IsMutation(request.Command) ? revision : null,
            OperationId = PostgreSqlVaultCatalogue.IsMutation(request.Command) ? Guid.NewGuid() : null };
        async Task<FluxVaultIpcResponse> Send(FluxVaultIpcRequest request)
        {
            var response = await client.SendAsync(request, deadline.Token);
            if (!response.Success) throw new InvalidOperationException($"{request.Command} failed: {response.ErrorCode}: {response.ErrorMessage}");
            return response;
        }
        async Task<FluxVaultIpcResponse> Maintenance(FluxVaultIpcRequest command)
        {
            var request = Bind(command);
            var result = await Send(request);
            var replay = await Send(request);
            var receipt = await Send(new(FluxVaultIpcCommand.GetOperationStatus, null, null, null, null, VaultId: id, OperationId: request.OperationId));
            Check(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(replay) && JsonSerializer.Serialize(result) == JsonSerializer.Serialize(receipt),
                $"{request.Command} replay and status return its exact durable outcome");
            return result;
        }
        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Single-vault contract failed: " + name); checks.Add(name); }
        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    }

    private static async Task<int> RunServerAsync(WindowsDatabaseProbeConfiguration fixture, string pipe, CancellationTokenSource deadline)
    {
        var endpoint = new VaultCatalogueEndpoint(Guid.ParseExact(fixture.FixtureId, "N"), "127.0.0.1", fixture.Port, fixture.Database, fixture.Role);
        await using (var source = WindowsDatabaseProbe.CreateDataSource(WindowsDatabaseProbe.CreateConnectionSettings(fixture, "127.0.0.1", "System", Guid.NewGuid())))
        await using (var connection = await source.OpenConnectionAsync(deadline.Token))
        await using (var query = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance'), current_database(), current_user", connection))
        await using (var result = await query.ExecuteReaderAsync(deadline.Token))
            if (!await result.ReadAsync(deadline.Token) || result.GetString(0) != fixture.FixtureId || result.GetString(1) != fixture.Database || result.GetString(2) != fixture.Role)
                throw new InvalidOperationException("Single-vault pipeline does not own this database.");
        var parent = Path.Combine(fixture.Root, "catalogue", "single"); CreateFreshPrivateRoot(parent);
        var repositoryPath = Path.Combine(parent, "repository"); var statePath = Path.Combine(parent, "state");
        var mirrorOne = Path.Combine(parent, "mirror-one"); var mirrorTwo = Path.Combine(parent, "mirror-two");
        foreach (var path in new[] { repositoryPath, statePath, mirrorOne, mirrorTwo }) CreateFreshPrivateRoot(path);
        var metadata = MetadataStoreConfiguration.CreateDefault(statePath) with
        { Host = endpoint.Host, Port = endpoint.Port, DatabaseName = endpoint.Database, Username = endpoint.ServiceRole };
        var binding = new VaultBinding(new(endpoint.InstanceId), repositoryPath, statePath, metadata);
        var configuration = FluxVaultConfiguration.CreateDefault(statePath) with { RepositoryPath = repositoryPath, MetadataStore = metadata,
            RetentionPolicy = RetentionPolicy.CreateDefault() with { IsEnabled = false },
            MirrorSet = new([new("first", "First", mirrorOne, true), new("second", "Second", mirrorTwo, true)]) };
        await using var catalogue = new PostgreSqlVaultCatalogue(endpoint);
        await catalogue.ProvisionAsync(deadline.Token);
        await using (var store = new PostgreSqlRepositoryMetadataStore(binding))
        {
            await store.ProvisionVaultAsync(deadline.Token);
            var repository = new FileSystemChunkRepository(binding, new FastCdcChunker(new()), new Blake3ContentHasher(), new ZstdChunkCodec(), configuration.MirrorSet, store);
            await repository.ProvisionVaultStorageAsync(deadline.Token);
            foreach (var item in new[] { ("a.txt", WindowsSelectionRecoveryProbe.HistoricalFirst),
                (Path.Combine("nested", "b.txt"), WindowsSelectionRecoveryProbe.HistoricalSecond) })
            {
                // Generated trusted fixture content; this is not a caller-file capture shortcut.
                await using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Item2));
                await repository.CommitAsync(new("historical-fixture", Path.Combine(fixture.Root, "output-A", "historical-fallback", item.Item1),
                    DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, CompressionPreference.Off, 1024, bytes), deadline.Token);
            }
        }
        await using var handler = new ProvisionedFixtureHandler(fixture, endpoint, binding, configuration, catalogue);
        var server = new NamedPipeFluxVaultServer(handler, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
        var serving = server.RunAsync(deadline.Token);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "runtime", "caller-files-ready.json"), JsonSerializer.Serialize(new { Pipe = pipe }), deadline.Token);
            while (!File.Exists(Path.Combine(fixture.Root, "runtime", "caller-files-stop"))) await Task.Delay(50, deadline.Token);
        }
        finally { await deadline.CancelAsync(); await serving; }
        var rehearsalRoot = Path.Combine(statePath, "restore-rehearsal");
        if (Directory.Exists(rehearsalRoot) && Directory.EnumerateFileSystemEntries(rehearsalRoot).Any())
            throw new InvalidOperationException("Protected rehearsal output was not cleaned before service exit.");
        if (Directory.EnumerateFiles(Path.Combine(mirrorOne, "chunks"), "*", SearchOption.AllDirectories).Any() ||
            !Directory.EnumerateFiles(Path.Combine(mirrorTwo, "chunks"), "*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Drained mirror still contains artefacts or the remaining mirror has no protected copy.");
        if (!handler.CreatorVerified || !handler.UngrantDenied || handler.Completed < 8) throw new InvalidOperationException("Native command pipeline evidence is incomplete.");
        if (Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "output-A"), ".FluxVault-recovery-*", SearchOption.AllDirectories).Any() ||
            Directory.EnumerateFiles(statePath, "fluxvault-diagnostics-*", SearchOption.AllDirectories).Any() ||
            File.Exists(Path.Combine(statePath, "forbidden-selection.docx")))
            throw new InvalidOperationException("Diagnostics left staging output or wrote into a caller-denied directory.");
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = handler.Completed, NativeCallerTokens = true, SingleVault = true, ProtectedProductComposition = true,
            ActualCatalogueAndExecutor = true, ProtectedRehearsalOutputCleaned = true, DrainEffectVerified = true,
            DiagnosticsOutputCleaned = true, SelectionOutputCleaned = true, handler.CreatorVerified, handler.UngrantDenied, ExactInstalledBinding = binding.Id }));
        return 0;
    }

    // Explicitly owned fixture provisioning, not a product request/lifecycle API. The
    // trusted mission names the creator; owner SID is taken from the native pipe token.
    // All commands after that one-time provision use the actual catalogue dispatcher.
    private sealed class ProvisionedFixtureHandler(WindowsDatabaseProbeConfiguration fixture, VaultCatalogueEndpoint endpoint, VaultBinding binding,
        FluxVaultConfiguration configuration, PostgreSqlVaultCatalogue catalogue)
        : IAuthenticatedFluxVaultRequestHandler, IAsyncDisposable
    {
        private readonly SemaphoreSlim initialisation = new(1, 1);
        internal bool CreatorVerified; internal bool UngrantDenied; internal int Completed;
        private WindowsSingleVaultService? service;
        public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token)
        {
            await initialisation.WaitAsync(token);
            try
            {
                if (!CreatorVerified && caller.UserSid == fixture.Actors["A"] && caller.ImpersonationPermitted && request.Command == FluxVaultIpcCommand.GetStatus && request.VaultId is null)
                {
                    var installed = await catalogue.InitializeAsync(caller, binding, "FluxVault", configuration, token);
                    CreatorVerified = installed.Access.OwnerSid == caller.UserSid;
                    var installation = new FluxVaultInstallation(endpoint, binding, caller.UserSid);
                    try
                    {
                        await catalogue.VerifyInstallationAsync(installation with { CreatorSid = fixture.Actors["B"] }, token);
                        throw new InvalidOperationException("A different bootstrap creator was accepted.");
                    }
                    catch (VaultCatalogueException exception) when (exception.Failure == VaultCatalogueFailure.IdentityMismatch) { }
                    var bootstrap = Path.Combine(binding.StateRoot, "installation.json");
                    var temp = bootstrap + ".tmp";
                    await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(installation), token);
                    var fileAcl = new FileInfo(temp).GetAccessControl(); fileAcl.SetOwner(new SecurityIdentifier("S-1-5-18"));
                    new FileInfo(temp).SetAccessControl(fileAcl);
                    File.Move(temp, bootstrap, overwrite: false);
                    service = await WindowsSingleVaultService.OpenAsync(bootstrap, token);
                }
            }
            finally { initialisation.Release(); }
            var response = service is null
                ? FluxVaultIpcResponse.Failure("The installation is not initialised.") with { ErrorCode = FluxVaultIpcErrorCode.Denied }
                : await service.HandleAsync(caller, request, token);
            if (response.Success) Interlocked.Increment(ref Completed);
            if (caller.UserSid == fixture.Actors["B"] && response.ErrorCode == FluxVaultIpcErrorCode.Denied && response.Status is null && response.VaultId is null)
                UngrantDenied = true;
            return response;
        }
        public async ValueTask DisposeAsync()
        { try { if (service is not null) await service.DisposeAsync(); } finally { initialisation.Dispose(); } }
    }

    private static void CreateFreshPrivateRoot(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Fixture provisioning refuses existing storage.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
    }
}
