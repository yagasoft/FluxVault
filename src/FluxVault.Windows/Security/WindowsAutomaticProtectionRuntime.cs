using System.Runtime.Versioning;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Policies;
using FluxVault.Core.Security;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Windows.Capture;
using FluxVault.Windows.ChangeTracking;
using Npgsql;

namespace FluxVault.Windows.Security;

/// <summary>One fenced, serial capture owner; source observation is always under the creator logon.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutomaticProtectionRuntime : IAsyncDisposable
{
    private readonly PostgreSqlVaultCatalogue catalogue;
    private readonly VaultBinding binding;
    private readonly NpgsqlDataSource source;
    private readonly SemaphoreSlim mutationGate;
    private readonly IAutomaticProtectionSessionProvider sessions;
    private readonly AutomaticProtectionRuntimePolicy policy;
    private readonly WindowsVaultStorageGuard storage = new();
    private readonly Lock hintsGate = new();
    private readonly Dictionary<string, DateTimeOffset> hints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string ScopeId, FileSystemWatcher Watcher, IDisposable Pin)> watchers = [];
    private readonly HashSet<string> dirtyHints = new(StringComparer.Ordinal);
    private readonly HashSet<string> invalidWatchers = new(StringComparer.Ordinal);
    private readonly AutomaticSourceActivity sourceActivity = new();
    private string? extensionUnavailable;
    private string? reconciliationBlocked;
    private PostgreSqlVaultCatalogue.AutomaticRuntimeOwner? owner;
    private AutomaticProtectionSession? session;
    private string? selection;
    private DateTimeOffset lastReconciliation;
    private readonly Dictionary<string,DateTimeOffset> journalFallbackByVolume = new(StringComparer.OrdinalIgnoreCase);
    private string discoveryMode = "Native watcher and durable reconciliation";
    private AutomaticProtectionRuntimeStatus status = new(new(), AutomaticProtectionState.Disabled, "Automatic protection is off.");
    public AutomaticProtectionRuntimeStatus Status => Volatile.Read(ref status);
    // Owned native-fixture barriers only; neither requests nor saved configuration can supply them.
    internal Func<CancellationToken, Task>? SourceEntryOpened { get; set; }
    internal Func<CancellationToken, Task>? CaptureOpened { get; set; }
    internal Func<FileVersionManifest,CancellationToken,Task>? PublicationCommitted { get; set; }
    internal PostgreSqlVaultCatalogue.AutomaticRuntimeOwner OwnerForFixture => owner ?? throw new InvalidOperationException("No automatic owner exists.");

    public WindowsAutomaticProtectionRuntime(PostgreSqlVaultCatalogue catalogue, VaultBinding binding,
        NpgsqlDataSource source, SemaphoreSlim mutationGate, IAutomaticProtectionSessionProvider? sessions = null,
        AutomaticProtectionRuntimePolicy? policy = null)
    {
        this.catalogue = catalogue; this.binding = binding; this.source = source; this.mutationGate = mutationGate;
        this.sessions = sessions ?? new WindowsAutomaticProtectionSessionProvider(); this.policy = policy ?? new();
        this.policy.Validate();
    }

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        try { await catalogue.EnsureAutomaticSchemaAsync(binding, ct); }
        catch (AutomaticProtectionUnavailableException exception)
        {
            extensionUnavailable = exception.Message;
            Volatile.Write(ref status, Status with { State = AutomaticProtectionState.Blocked, Reason = exception.Message,CanChangePreference=false });
            return;
        }
        owner = await catalogue.OpenAutomaticRuntimeOwnerAsync(binding, policy, ct);
        await catalogue.ActivateAutomaticRuntimeAsync(owner, ct);
        // Before pipe admission: an interrupted publication is never blindly captured a second time.
        try { await ReconcileClaimsAsync(ct); }
        catch (IOException exception)
        {
            await catalogue.VerifyRuntimeFenceAsync(ct); // A lost owner is fatal, not a manual-recovery fallback.
            reconciliationBlocked = exception.Message;
            Volatile.Write(ref status, Status with { State = AutomaticProtectionState.OutcomeUncertain, Reason = exception.Message });
        }
    }

    public async ValueTask<IAsyncDisposable?> PrepareMutationAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken ct)
    {
        // Never let an ungranted client stop the creator's source generation.
        if (await catalogue.VerifyMutationAuthorityAsync(caller, request, ct)) return null;
        var recovery=request.Command is FluxVaultIpcCommand.RestoreVersion or FluxVaultIpcCommand.RestoreVersionPreview or FluxVaultIpcCommand.RunRestoreSelection;
        if (extensionUnavailable is not null && !recovery)
            throw new VaultCatalogueException(VaultCatalogueFailure.Unavailable,
                extensionUnavailable +
                " History and recovery remain available; changes are blocked until reconciliation succeeds.");
        if (extensionUnavailable is not null) return null;
        var held = await sourceActivity.SuspendAsync(ct);
        try
        {
            if(reconciliationBlocked is not null && !recovery)
            {
                try { await ReconcileClaimsAsync(ct); reconciliationBlocked=null; }
                catch(IOException exception)
                {
                    throw new VaultCatalogueException(VaultCatalogueFailure.Unavailable,
                        "An interrupted automatic result is uncertain. History and recovery remain available. "+exception.Message);
                }
            }
            await mutationGate.WaitAsync(ct);
            try { await RetireSessionAsync(); }
            finally { mutationGate.Release(); }
            return held;
        }
        catch { await held.DisposeAsync(); throw; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (extensionUnavailable is not null) await Task.Delay(Timeout.Infinite, ct);
            else await SupervisedRuntime.RunAsync(DispatchLoopAsync, ObserveAuthorityAsync, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Volatile.Write(ref status, Status with { State = AutomaticProtectionState.Failed, Reason = exception.Message });
            throw;
        }
        finally { await RetireSessionAsync(); }
    }

    private async Task ObserveAuthorityAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Deliberately outside mutationGate: a running capture must not prevent its own cancellation.
            if (owner is not null) await owner.VerifyAliveAsync(ct);
            if (Volatile.Read(ref session) is { } observed)
            {
                try { await observed.VerifyAsync(ct); }
                catch (UnauthorizedAccessException) { }
            }
            await Task.Delay(TimeSpan.FromSeconds(policy.SessionObservationSeconds), ct);
        }
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var cycle = sourceActivity.TryBegin(ct);
            if (cycle is null) { await Task.Delay(TimeSpan.FromSeconds(policy.SessionObservationSeconds), ct); continue; }
            AutomaticCatalogueState? state = null;
            try
            {
            state = await catalogue.ReadAutomaticStateAsync(binding, cycle.Token);
            Publish(state, AutomaticProtectionState.Reconciling, "Checking saved automatic work.");
            if (reconciliationBlocked is not null)
            {
                await ReconcileClaimsAsync(cycle.Token);
                reconciliationBlocked = null;
            }
            if (!state.Policy.Enabled || !state.Vault.Configuration.IsEnabled || state.RequiresCreatorApproval)
            {
                await RetireSessionAsync();
                Publish(state, !state.Vault.Configuration.IsEnabled ? AutomaticProtectionState.Paused :
                    state.RequiresCreatorApproval && state.Policy.Enabled ? AutomaticProtectionState.Blocked : AutomaticProtectionState.Disabled,
                    !state.Vault.Configuration.IsEnabled ? "Protection is paused; pending work and history are kept." :
                    state.Policy.Enabled ? "Creator approval is required for the saved source selection." : "Automatic protection is off.");
            }
            else
            {
                try
                {
                    if (session?.AuthorityEnded.IsCancellationRequested == true || selection != state.ScopeFingerprint)
                        await RetireSessionAsync();
                    if (session is null)
                    {
                        session = await sessions.AcquireAsync(state.Vault.Access.OwnerSid, cycle.Token);
                        if (session is not null)
                        {
                            selection = state.ScopeFingerprint;
                            await BuildWatchersAsync(state, cycle.Token);
                            await MarkAllScopesAsync(state, "Startup/session reconciliation", cycle.Token);
                            lastReconciliation = DateTimeOffset.UtcNow;
                        }
                    }
                    if (session is null) Publish(state, AutomaticProtectionState.Blocked, "Creator session unavailable. Sign in as the vault creator to continue.");
                    else
                    {
                        using var generation = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token, session.AuthorityEnded);
                        await session.VerifyAsync(generation.Token);
                        await BuildWatchersAsync(state,generation.Token);
                        await AdmitHintsAsync(state, generation.Token);
                        await AdmitJournalAsync(state,session,generation.Token);
                        if (DateTimeOffset.UtcNow - lastReconciliation >= state.Vault.Configuration.CaptureCadencePolicy.PeriodicReconciliationInterval)
                        {
                            await MarkAllScopesAsync(state, "Periodic source reconciliation", generation.Token);
                            lastReconciliation = DateTimeOffset.UtcNow;
                        }
                        await ReconcileClaimsAsync(generation.Token);
                        await catalogue.SupersedeObsoleteAutomaticWorkAsync(binding, generation.Token);
                        await ScanDirtyScopesAsync(state, session, generation.Token);
                        for (var count = 0; count < policy.DispatchBatchSize; count++)
                            if (!await CaptureOneAsync(session, generation.Token)) break;
                        state = await catalogue.ReadAutomaticStateAsync(binding, generation.Token);
                        if (Status.State is not (AutomaticProtectionState.Blocked or AutomaticProtectionState.OutcomeUncertain))
                            Publish(state, state.PendingCount > 0 ? AutomaticProtectionState.Pending : AutomaticProtectionState.WaitingForChanges,
                                state.PendingCount > 0 ? "Saved changes are pending capture." : "Waiting for saved file changes.");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && session?.AuthorityEnded.IsCancellationRequested == true)
                { Publish(state, AutomaticProtectionState.Blocked, "The creator session ended. Pending work is retained."); await RetireSessionAsync(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                { Publish(state, reconciliationBlocked is not null || exception is InvalidDataException ? AutomaticProtectionState.OutcomeUncertain : AutomaticProtectionState.Blocked, exception.Message); }
            }
            if (reconciliationBlocked is null)
            {
                try { await DrainOutboxAsync(cycle.Token); }
                catch (IOException exception) { Publish(state,AutomaticProtectionState.Blocked,"Metadata export remains pending: "+exception.Message); }
                await RetryMirrorsAsync(cycle.Token);
            }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && cycle.Token.IsCancellationRequested)
            { await RetireSessionAsync(); }
            catch (IOException exception)
            {
                if (state is not null) Publish(state, AutomaticProtectionState.OutcomeUncertain, exception.Message);
            }
            finally { cycle.Dispose(); }
            await Task.Delay(state?.Vault.Configuration.CaptureCadencePolicy.WatcherPollInterval ?? TimeSpan.FromSeconds(policy.SessionObservationSeconds), ct);
        }
    }

    private async Task BuildWatchersAsync(AutomaticCatalogueState state, CancellationToken ct)
    {
        var caller = session!.Caller;
        string[] invalid;
        lock (hintsGate) { invalid=invalidWatchers.ToArray(); invalidWatchers.Clear(); }
        foreach (var old in watchers.Where(item=>invalid.Contains(item.ScopeId,StringComparer.Ordinal)).ToArray())
        { old.Watcher.Dispose(); old.Pin.Dispose(); watchers.Remove(old); }
        foreach (var folder in state.Vault.Configuration.WatchedFolders.Where(folder => folder.IsEnabled))
        {
            if (watchers.Any(item=>item.ScopeId==folder.Id)) continue;
            WindowsCallerFileAccess.OpenedSource? pin = null;
            FileSystemWatcher? watcher = null;
            try
            {
                pin = await WindowsCallerFileAccess.OpenValidatedSourceAsync(caller, folder.Path, folder.Path, true,
                    0x1000A1, false, null, ct);
                watcher = await caller.RunAsCallerAsync(() => Task.FromResult(new FileSystemWatcher(folder.Path)
                { IncludeSubdirectories = folder.Recursive, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size }));
                watcher.Created += (_, args) => Hint(folder.Id, args.FullPath, state);
                watcher.Changed += (_, args) => Hint(folder.Id, args.FullPath, state);
                watcher.Deleted += (_, args) => { Dirty(folder.Id); Hint(folder.Id,args.FullPath,state); };
                watcher.Renamed += (_, args) => { Dirty(folder.Id); Hint(folder.Id, args.OldFullPath, state); Hint(folder.Id, args.FullPath, state); };
                watcher.Error += (_, _) => { lock (hintsGate) { dirtyHints.Add(folder.Id); invalidWatchers.Add(folder.Id); } };
                await caller.RunAsCallerAsync(() => { watcher.EnableRaisingEvents = true; return Task.FromResult(true); });
                watchers.Add((folder.Id,watcher, pin)); watcher = null; pin = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { Dirty(folder.Id); }
            finally { watcher?.Dispose(); pin?.Dispose(); }
        }
    }
    private void Dirty(string scope) { lock (hintsGate) dirtyHints.Add(scope); }
    private void Hint(string scope, string path, AutomaticCatalogueState state)
    {
        lock (hintsGate)
        {
            if (hints.Count >= state.Vault.Configuration.CaptureCadencePolicy.WatcherEventBacklogLimit) { dirtyHints.Add(scope); return; }
            hints[path] = DateTimeOffset.UtcNow;
        }
    }
    private async Task MarkAllScopesAsync(AutomaticCatalogueState state, string reason, CancellationToken ct)
    {
        foreach (var folder in state.Vault.Configuration.WatchedFolders.Where(folder => folder.IsEnabled))
            await catalogue.MarkAutomaticScopeDirtyAsync(binding, folder.Id, state.ScopeFingerprint, reason, ct);
    }
    private async Task AdmitHintsAsync(AutomaticCatalogueState state, CancellationToken ct)
    {
        KeyValuePair<string, DateTimeOffset>[] accepted; string[] dirty;
        lock (hintsGate) { accepted = hints.ToArray(); hints.Clear(); dirty = dirtyHints.ToArray(); dirtyHints.Clear(); }
        foreach (var scope in dirty) await catalogue.MarkAutomaticScopeDirtyAsync(binding, scope, state.ScopeFingerprint, "Watcher overflow, error, rename or directory change", ct);
        foreach (var hint in accepted)
        {
            if (!ProtectionSourceSelection.TryFindFolder(state.Vault.Configuration, hint.Key, out var folder)) continue;
            if (!await ObserveAndQueueAsync(state,folder.Id,hint.Key,hint.Value,"Saved source change",ct))
                await catalogue.MarkAutomaticScopeDirtyAsync(binding, folder.Id, state.ScopeFingerprint, "Pending capacity reached; coverage retained", ct);
        }
    }

    private async Task AdmitJournalAsync(AutomaticCatalogueState state, AutomaticProtectionSession active, CancellationToken ct)
    {
        var checkpoints = await catalogue.ReadAutomaticJournalCheckpointsAsync(binding,state.ScopeFingerprint,ct);
        foreach (var volume in state.Vault.Configuration.WatchedFolders.Where(folder=>folder.IsEnabled)
            .GroupBy(folder=>Path.GetPathRoot(Path.GetFullPath(folder.Path)),StringComparer.OrdinalIgnoreCase))
        {
            var representative = volume.First();
            var saved = checkpoints.FirstOrDefault(item=>string.Equals(item.VolumeRoot,volume.Key,StringComparison.OrdinalIgnoreCase));
            var result = await active.Caller.RunAsCallerAsync(()=>new WindowsUsnChangeJournalReader().ReadChangesAsync(
                [new(representative.Id,representative.Path,representative.Recursive)],
                saved is null ? [] : [saved with { WatchedFolderId=representative.Id }],ct));
            await active.VerifyAsync(ct);
            if (result.Checkpoints.Count>0)
                await catalogue.AdmitAutomaticJournalCoverageAsync(binding,state.ScopeFingerprint,result.Checkpoints,
                    "Journal cursor admitted with full volume-scope reconciliation",ct);
            discoveryMode = result.IsAvailable ? "Native watcher, durable USN cursor and reconciliation" :
                "Native watcher and durable full reconciliation; journal unavailable: "+result.FallbackReason;
            var cooldown = state.Vault.Configuration.CaptureCadencePolicy.UsnFallbackFullScanCooldown;
            if (cooldown<=TimeSpan.Zero) cooldown=FluxVault.Abstractions.Policies.CaptureCadencePolicy.DefaultUsnFallbackFullScanCooldown;
            journalFallbackByVolume.TryGetValue(volume.Key!,out var lastFallback);
            if (result.RequiresFullScan && DateTimeOffset.UtcNow-lastFallback>=cooldown)
            {
                foreach (var folder in volume) await catalogue.MarkAutomaticScopeDirtyAsync(binding,folder.Id,state.ScopeFingerprint,
                    result.FallbackReason ?? "Journal reconciliation required",ct);
                journalFallbackByVolume[volume.Key!]=DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task<bool> ObserveAndQueueAsync(AutomaticCatalogueState state, string scopeId, string path,
        DateTimeOffset observedAt, string reason, CancellationToken ct, bool retainedSource = false)
    {
        var folder = state.Vault.Configuration.WatchedFolders.Single(item=>item.Id==scopeId);
        var inspection = new WindowsProtectionSourceAccess(session!.Caller).Inspect(folder.Path,path,RepositoryEntryKind.File,ct);
        if (inspection.Availability==ProtectionSourceAvailability.Unavailable)
        {
            await catalogue.MarkAutomaticScopeDirtyAsync(binding,scopeId,state.ScopeFingerprint,"Source inspection remains unavailable",ct);
            throw new IOException(inspection.FailureReason ?? "The source could not be authoritatively inspected.");
        }
        if (inspection.Availability==ProtectionSourceAvailability.Present && WorkloadPolicyResolver.Resolve(state.Vault.Configuration,folder,
            path,inspection.Length!.Value,false).IsExcluded)
        {
            await catalogue.ExcludeAutomaticPathAsync(binding,scopeId,path,state.ScopeFingerprint,ct);
            return true;
        }
        var missing = inspection.Availability==ProtectionSourceAvailability.Missing;
        var value = missing ? "missing" : inspection.SourceObservation ?? throw new IOException("No verified source observation is available.");
        await mutationGate.WaitAsync(ct);
        try
        {
            await session.VerifyAsync(ct);
            var existing = await catalogue.FindAutomaticPathAsync(binding,scopeId,path,ct);
            if (missing && existing is null && !retainedSource) return true;
            var observation = new AutomaticPathObservation(value,observedAt,missing,
                RetainedResultVerified:missing && existing?.LastSourceObservation=="missing");
            var accepted = false;
            if (!missing && existing?.LastSourceObservation==value && existing.LastVersionId is { } retained)
            {
                await WithRepositoryAsync(state.Vault,async repository =>
                {
                    await repository.VerifyAutomaticResultAsync(existing with { ReservedVersionId=retained,IsDeletion=false,DeletedFromVersionId=null },ct,
                        async (result,token)=> accepted=await catalogue.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,
                            observedAt,reason,policy,token,observation with { RetainedResultVerified=result is not null && CanReuseResult(state,result) }));
                },ct);
            }
            else accepted=await catalogue.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,observedAt,reason,policy,ct,observation);
            return accepted;
        }
        finally { mutationGate.Release(); }
    }

    private async ValueTask VerifyPublicationAsync(AutomaticProtectionSession active, AutomaticPathWork work, CancellationToken ct)
    {
        await active.VerifyAsync(ct);
        await catalogue.VerifyRuntimeFenceAsync(ct);
        var current = await catalogue.ReadAutomaticStateAsync(binding,ct);
        if (!current.Policy.Enabled || !current.Vault.Configuration.IsEnabled || current.RequiresCreatorApproval || current.ScopeFingerprint!=work.ScopeFingerprint)
            throw new UnauthorizedAccessException("Automatic source consent changed before publication.");
    }
    private static void RequireConfirmedAbsence(AutomaticProtectionSession active, AutomaticPathWork work, CancellationToken ct)
    {
        var inspection = new WindowsProtectionSourceAccess(active.Caller).Inspect(work.RootPath,work.SourcePath,RepositoryEntryKind.File,ct);
        if (inspection.Availability!=ProtectionSourceAvailability.Missing)
            throw new IOException(inspection.Availability==ProtectionSourceAvailability.Present
                ? "The source reappeared; its current save remains pending." : "Source absence cannot be confirmed: "+inspection.FailureReason);
    }

    private async Task ScanDirtyScopesAsync(AutomaticCatalogueState state, AutomaticProtectionSession active, CancellationToken ct)
    {
        var access = new WindowsProtectionSourceAccess(active.Caller);
        foreach (var scope in await catalogue.ListAutomaticScopesAsync(binding, ct))
        {
            if (scope.ScopeFingerprint != state.ScopeFingerprint) continue;
            var folder = state.Vault.Configuration.WatchedFolders.Single(item => item.Id == scope.ScopeId);
            var stack = new Stack<string>(); stack.Push(folder.Path); var count = 0; var complete = true;
            try
            {
                while (stack.TryPop(out var directory))
                {
                    foreach (var entry in access.EnumerateDirectory(folder.Path, directory, ct))
                    {
                        if (SourceEntryOpened is { } opened) await opened(ct);
                        ct.ThrowIfCancellationRequested();
                        if (++count > policy.MaximumReconciliationEntries) { complete = false; break; }
                        if (entry.Kind == RepositoryEntryKind.Folder)
                        {
                            if (folder.Recursive && !ProtectionExclusionMatcher.IsFolderExcluded(entry.Path, state.Vault.Configuration.ExclusionRules)) stack.Push(entry.Path);
                        }
                        else if (ProtectionSourceSelection.TryFindFolder(state.Vault.Configuration, entry.Path, out _))
                        {
                            if (!await ObserveAndQueueAsync(state,folder.Id,entry.Path,DateTimeOffset.UtcNow,"Source reconciliation",ct))
                            { complete = false; break; }
                        }
                    }
                    if (!complete) break;
                }
                if (complete)
                {
                    RepositoryCurrentEntriesCursor? cursor = null;
                    do
                    {
                        RepositoryCurrentEntriesPage? page = null;
                        await WithRepositoryAsync(state.Vault,async repository => page=await repository.ListCurrentEntriesPageAsync(new(binding.Id,256,cursor),ct),ct);
                        foreach (var current in page!.Entries)
                        {
                            if (++count>policy.MaximumReconciliationEntries) { complete=false; break; }
                            if (current.Version.EntryKind!=RepositoryEntryKind.File || current.Version.IsDeleted ||
                                !ProtectionSourceSelection.TryFindFolder(state.Vault.Configuration,current.Version.SourcePath,out var selected) || selected.Id!=folder.Id) continue;
                            if (!await ObserveAndQueueAsync(state,folder.Id,current.Version.SourcePath,DateTimeOffset.UtcNow,
                                "Retained repository source reconciliation",ct,retainedSource:true)) { complete=false; break; }
                        }
                        cursor=page.NextCursor;
                    } while (complete && cursor is not null);
                }
                if (complete)
                {
                    var after = "";
                    while (true)
                    {
                        var known = await catalogue.ListAutomaticScopePathsAsync(binding,scope.ScopeId,state.ScopeFingerprint,after,256,ct);
                        if (known.Count==0) break;
                        foreach (var work in known)
                        {
                            if (++count>policy.MaximumReconciliationEntries ||
                                !await ObserveAndQueueAsync(state,folder.Id,work.SourcePath,DateTimeOffset.UtcNow,"Known source reconciliation",ct))
                            { complete=false; break; }
                        }
                        if (!complete) break;
                        after=known[^1].PathKey;
                    }
                }
                if (complete) await catalogue.CompleteAutomaticScopeAsync(binding, scope, ct);
                else Publish(state, AutomaticProtectionState.Blocked, "Source reconciliation reached its configured bound. Coverage remains pending.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Publish(state, AutomaticProtectionState.Blocked, "Source reconciliation is incomplete: " + exception.Message); }
        }
    }

    private async Task<bool> CaptureOneAsync(AutomaticProtectionSession active, CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            await active.VerifyAsync(ct);
            var state = await catalogue.ReadAutomaticStateAsync(binding, ct);
            if (!state.Policy.Enabled || !state.Vault.Configuration.IsEnabled || state.RequiresCreatorApproval || selection != state.ScopeFingerprint) return false;
            var work = await catalogue.ClaimAutomaticPathAsync(binding, ct);
            if (work is null) return false;
            reconciliationBlocked = "A reserved automatic result must be reconciled before new changes are admitted.";
            Publish(state, AutomaticProtectionState.Capturing, "Capturing " + work.SourcePath);
            // Every exception leaves its reserved identity intact for the same reconciliation path.
            await WithRepositoryAsync(state.Vault, async repository =>
            {
                await active.VerifyAsync(ct);
                if (work.IsDeletion)
                {
                    await VerifyPublicationAsync(active,work,ct);
                    RequireConfirmedAbsence(active,work,ct);
                    var deleted = await repository.RecordDeletionAsync(new(work.ScopeId,work.RootPath,work.SourcePath,false,
                        DateTimeOffset.UtcNow,ReservedVersionId:work.ReservedVersionId,
                        BeforePublication:async (parent,token)=>
                        {
                            await catalogue.ObserveAutomaticDeletionClaimAsync(binding,work,parent,token);
                            await VerifyPublicationAsync(active,work,token);
                            RequireConfirmedAbsence(active,work,token);
                        }),ct);
                    await active.VerifyAsync(ct);
                    if (deleted is null) await catalogue.CompleteAutomaticNoChangeAsync(binding,work,ct);
                    else await catalogue.CompleteAutomaticPathAsync(binding,work,deleted.Manifest.VersionId,ct);
                    reconciliationBlocked=null;
                    if (deleted?.MirrorWarnings.Count>0) Publish(state,AutomaticProtectionState.Blocked,"Primary deletion recorded; mirror warning: "+string.Join(" ",deleted.MirrorWarnings));
                    return;
                }
                await using var capture = await new WindowsCallerCaptureProvider(active.Caller, [work.RootPath])
                    .CaptureAsync(new(work.SourcePath), ct);
                if (!capture.Success || capture.Content is null || capture.SourceObservation is null)
                    throw new IOException(capture.Message);
                if (CaptureOpened is { } opened) await opened(ct);
                ct.ThrowIfCancellationRequested();
                await catalogue.ObserveAutomaticClaimAsync(binding, work, capture.SourceObservation, ct);
                var reused = false;
                if (work.LastSourceObservation == capture.SourceObservation && work.LastVersionId is { } retained)
                {
                    await repository.VerifyAutomaticResultAsync(work with { ReservedVersionId=retained },ct,async (result,token)=>
                    {
                        if (result is null) return;
                        if (!CanReuseResult(state,result)) return;
                        await VerifyPublicationAsync(active,work,token); await capture.VerifySourceUnchangedAsync(token);
                        await catalogue.CompleteAutomaticPathAsync(binding,work,retained,token); reused=true;
                    });
                }
                if (reused) { reconciliationBlocked=null; return; }
                var folder = state.Vault.Configuration.WatchedFolders.Single(item => item.Id == work.ScopeId);
                var resolved = WorkloadPolicyResolver.Resolve(state.Vault.Configuration, folder, work.SourcePath, capture.SourceLength!.Value, false);
                if (resolved.IsExcluded)
                {
                    await repository.VerifyAutomaticResultAsync(work,ct,async (result,token)=>
                    {
                        if (result is not null) throw new InvalidDataException("An excluded unstarted capture unexpectedly has a published result.");
                        await catalogue.RequeueAbsentAutomaticClaimAsync(binding,work,"Excluded source; no capture was published.",TimeSpan.Zero,token);
                        await catalogue.ExcludeAutomaticPathAsync(binding,work.ScopeId,work.SourcePath,work.ScopeFingerprint,token);
                    });
                    reconciliationBlocked=null; return;
                }
                var committed = await repository.CommitAsync(new(work.ScopeId, work.SourcePath, DateTimeOffset.UtcNow,
                    capture.Consistency, resolved.Compression, resolved.MinimumCompressionBytes, capture.Content,
                    WatchedFolderPath: work.RootPath, SourceLastWriteUtc: capture.SourceLastWriteUtc,
                    ReservedVersionId: work.ReservedVersionId, BeforePublication: async token =>
                    {
                        await VerifyPublicationAsync(active,work,token);
                        await capture.VerifySourceUnchangedAsync(token);
                    }), ct);
                if (PublicationCommitted is { } published) await published(committed.Manifest,ct);
                ct.ThrowIfCancellationRequested();
                await catalogue.CompleteAutomaticPathAsync(binding, work, committed.Manifest.VersionId, ct);
                reconciliationBlocked = null;
                if (committed.MirrorWarnings.Count > 0)
                    Publish(state, AutomaticProtectionState.Blocked, "Primary capture committed; mirror warning: " + string.Join(" ", committed.MirrorWarnings));
            }, ct);
            return true;
        }
        finally { mutationGate.Release(); }
    }

    private async Task ReconcileClaimsAsync(CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            var vault = (await catalogue.ReadAutomaticStateAsync(binding, ct)).Vault;
            while (true)
            {
                var claims = await catalogue.ListAutomaticClaimsAsync(binding, policy.DispatchBatchSize, ct);
                if (claims.Count == 0) break;
                await WithRepositoryAsync(vault, async repository =>
                {
                    foreach (var claim in claims)
                    {
                        await repository.VerifyAutomaticResultAsync(claim,ct,async (result,token)=>
                        {
                            if (result is null) await catalogue.RequeueAbsentAutomaticClaimAsync(binding,claim,"Reserved publication absent; retry retained",TimeSpan.FromSeconds(policy.RetrySeconds),token);
                            else await catalogue.CompleteAutomaticPathAsync(binding,claim,result.VersionId,token);
                        });
                    }
                }, ct);
            }
        }
        catch (IOException exception) { reconciliationBlocked = exception.Message; throw; }
        finally { mutationGate.Release(); }
    }

    private async Task DrainOutboxAsync(CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            var state = await catalogue.ReadAutomaticStateAsync(binding, ct);
            if (state.Policy.Enabled && state.Vault.Configuration.IsEnabled && !state.RequiresCreatorApproval)
                await WithRepositoryAsync(state.Vault, async repository => { await repository.DrainMetadataOutboxAsync(ct); }, ct);
        }
        finally { mutationGate.Release(); }
    }

    private static bool CanReuseResult(AutomaticCatalogueState state,FileVersionManifest manifest)
    {
        var interval=state.Vault.Configuration.CaptureCadencePolicy.SourceDeepVerificationInterval;
        if(interval<=TimeSpan.Zero)interval=FluxVault.Abstractions.Policies.CaptureCadencePolicy.DefaultSourceDeepVerificationInterval;
        return manifest.CapturedAtUtc+interval>DateTimeOffset.UtcNow;
    }

    private async Task WithRepositoryAsync(VaultCatalogueEntry vault, Func<FileSystemChunkRepository, Task> action, CancellationToken ct)
        => await WithRepositoryAsync(vault,(repository,_)=>action(repository),ct);

    private async Task WithRepositoryAsync(VaultCatalogueEntry vault,Func<FileSystemChunkRepository,IReadOnlyList<string>,Task> action,CancellationToken ct)
    {
        using var pins = storage.OpenAutomatic(vault,out var availableMirrors,out var warnings,ct);
        await using var store = new PostgreSqlRepositoryMetadataStore(binding, vault.Configuration.Sync.LocalDevice.DeviceId, sharedWindowsDataSource: source);
        var repository = new FileSystemChunkRepository(binding, new FastCdcChunker(new()), new Blake3ContentHasher(), new ZstdChunkCodec(),
            availableMirrors, store, runtimeFence: catalogue.VerifyRuntimeFenceAsync);
        await action(repository,warnings);
    }

    private async Task RetryMirrorsAsync(CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            var state=await catalogue.ReadAutomaticStateAsync(binding,ct);
            // Retained history needs no source authority. Pause/off still stop background writes.
            if(!state.Policy.Enabled || !state.Vault.Configuration.IsEnabled)return;
            foreach(var work in await catalogue.ListAutomaticMirrorWorkAsync(binding,policy.DispatchBatchSize,ct))
            {
                try
                {
                    var target=await catalogue.FindNextAutomaticMirrorVersionAsync(binding,work,ct);
                    await WithRepositoryAsync(state.Vault,async(repository,unavailable)=>
                    {
                        await repository.RetryAutomaticMirrorVersionAsync(work.ScopeId,work.SourcePath,target?.VersionId,async(warnings,token)=>
                        {
                            if(warnings.Count==0)await catalogue.AdvanceAutomaticMirrorWorkAsync(binding,work,target?.OutboxId ?? work.ThroughOutboxId,token);
                            else await catalogue.DelayAutomaticMirrorWorkAsync(binding,work,string.Join(" ",warnings),TimeSpan.FromSeconds(policy.RetrySeconds),token);
                        },ct,unavailable);
                    },ct);
                }
                catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
                {await catalogue.DelayAutomaticMirrorWorkAsync(binding,work,exception.Message,TimeSpan.FromSeconds(policy.RetrySeconds),ct);}
            }
        }
        finally {mutationGate.Release();}
    }

    private void Publish(AutomaticCatalogueState state, AutomaticProtectionState observed, string reason) =>
        Volatile.Write(ref status, new(state.Policy, observed, reason, state.RequiresCreatorApproval, state.PendingCount,
            state.UncertainCount, state.LastSuccessfulVersionId, state.LastSuccessfulUtc, DateTimeOffset.UtcNow, session?.SessionId, discoveryMode,
            MirrorPendingCount:state.MirrorPendingCount,MirrorReason:state.MirrorReason));

    private async Task RetireSessionAsync()
    {
        foreach (var item in watchers) { item.Watcher.Dispose(); item.Pin.Dispose(); }
        watchers.Clear();
        var previous = Interlocked.Exchange(ref session, null);
        if (previous is not null) await previous.DisposeAsync();
        selection = null;
        lock (hintsGate) { hints.Clear(); dirtyHints.Clear(); invalidWatchers.Clear(); }
    }
    public async ValueTask DisposeAsync()
    {
        await RetireSessionAsync();
        if (owner is not null) { await owner.DisposeAsync(); owner = null; }
    }
}
