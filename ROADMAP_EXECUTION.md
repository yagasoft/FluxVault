# FluxVault roadmap execution log

Pinned execution instructions:

- Execute the roadmap end to end using TDD.
- Do not modify anything outside current project folders, Codex worktree folders, or Codex workspace folders without explicit literal permission.
- Do not stop after each roadmap item for confirmation.
- After each item, run relevant tests, update this file with status/evidence/blockers/assumptions, commit/checkpoint, then continue.
- Before each next item, internally simulate Plan Mode, build a plan without asking questions, then implement it.
- Automatically squash-merge, push, delete branch/worktree, prune, and verify clean synced `main` after every successful TDD item.
- Stop only for a real blocker, destructive action requiring approval, completely unclear requirement, undiagnosable failing test, or an external dependency the user must provide.

## R2-002d: mirror drain/remove execution

Status: complete.

Plan:

- Add drain operation metadata to mirror placement reports so preview/run evidence is distinguishable from normal placement rebalance.
- Add `PreviewMirrorDrain` and `RunMirrorDrain` IPC commands carrying the selected `MirrorNodeId`.
- Add repository preview/run drain behaviour that copies required chunk/metadata artefacts from the healthy primary to remaining enabled target mirrors before deleting chunk/metadata from the selected mirror.
- Preserve selected mirror artefacts when remaining target copies are unresolved or unavailable.
- Persist drain report evidence in repository health/status and disable the selected mirror configuration only after a safe, healthy drain.
- Surface selected-node drain preview/run actions in the Mirrors workspace while keeping ordinary configuration removal separate.
- Update README and roadmap docs for `R2-002d`; mark `R2-002` implemented only if all planned R2-002 scope is covered by R2-002a through R2-002d.

Evidence:

- Red evidence: `dotnet test tests\FluxVault.Core.Tests\FluxVault.Core.Tests.csproj --filter "FullyQualifiedName~IpcSerializationTests|FullyQualifiedName~RepositoryMaintenanceTests"` initially failed because `FluxVaultIpcCommand.PreviewMirrorDrain` and `RunMirrorDrain` did not exist.
- Green targeted evidence: core targeted tests passed, 42 total.
- Green targeted evidence: integration targeted tests passed, 40 total.
- Green targeted evidence: app targeted tests passed, 72 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 115 App, 122 Core, 72 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- Drain execution is selected-node only.
- Drain/rebalance operates on chunk and metadata placement; manifests remain mirrored by normal manifest mirroring.
- A successful drain disables the selected mirror node in configuration; simple configuration removal remains a separate unsaved edit path.

## R3-001: stable device identity and trusted device records

Status: complete.

Plan:

- Add typed sync identity configuration with a stable local device id, display name, created timestamp, and trusted-device records.
- Default and legacy configurations must normalise to one local trusted-device record without breaking existing config files.
- Expose local device identity and trusted-device records through service status/IPC.
- Show compact local identity and trusted-device summary in Diagnostics.
- Update README and docs/tracker for `R3-001`.

Evidence:

- Red evidence: `dotnet test tests\FluxVault.Core.Tests\FluxVault.Core.Tests.csproj --filter "FullyQualifiedName~ConfigurationStoreTests|FullyQualifiedName~IpcSerializationTests"` initially failed because `Sync`, `DeviceTrustState`, `DeviceIdentityRuntimeStatus`, and `FluxVaultServiceStatus.DeviceIdentity` did not exist.
- Green targeted evidence: core configuration/IPC tests passed, 32 total.
- Green targeted evidence: service status tests passed in the ServiceOperationsTests filter, 34 total.
- Green targeted evidence: app view-model/XAML tests passed, 73 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 116 App, 124 Core, 73 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- `R3-001` is a local identity/trust foundation only; peer operation logs, mapping gates, and sync hydration remain later R3 items.
- Device identity is shown in Diagnostics, not editable in Options yet, because trust editing has no safe runtime workflow before the later sync slices.

## R3-005: peer heads, immutable operation records, and local cursors

Status: complete.

Plan:

- Add sync abstractions for immutable peer operation records, compact peer heads, and local per-peer cursors.
- Add a repository-owned file sync journal under the repository path with atomic writes and append-only operation records.
- Expose peer heads and cursors through service status/IPC.
- Show compact sync peer-head/cursor status in Diagnostics.
- Update README and docs/tracker for `R3-005`.

Evidence:

- Red evidence: `dotnet test tests\FluxVault.Core.Tests\FluxVault.Core.Tests.csproj --filter "FullyQualifiedName~PeerSyncJournalTests|FullyQualifiedName~IpcSerializationTests"` initially failed because `FluxVault.Abstractions.Sync` and `FluxVault.Core.Sync` did not exist.
- Green targeted evidence: peer journal/IPC tests passed, 22 total.
- Green targeted evidence: service status tests passed in the ServiceOperationsTests filter, 35 total.
- Green targeted evidence: app view-model/XAML tests passed, 74 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 117 App, 127 Core, 74 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- `R3-005` is metadata discovery foundation only; mapping confirmation, loop prevention, hydration, blocked targets, and conflict actions remained later R3 items at the time it was implemented.
- Operation records reference versions and metadata but do not duplicate chunk payloads.

## R3-002: sync mapping confirmation gate

Status: complete.

Plan:

- Add repository-owned sync mapping records with pending, confirmed, and blocked states.
- Add a pure mapping gate that only allows hydration for confirmed mappings with a local target path.
- Persist mappings under repository sync metadata with atomic JSON writes.
- Expose mapping records through service status/IPC and show pending mapping count in Diagnostics.
- Update README and roadmap docs/tracker for `R3-002`.

Evidence:

- Red evidence: `dotnet test tests\FluxVault.Core.Tests\FluxVault.Core.Tests.csproj --filter "FullyQualifiedName~SyncMappingStoreTests|FullyQualifiedName~IpcSerializationTests"` initially failed because `SyncMappingRecord`, `SyncMappingStatus`, `SyncMappingGate`, `FileSyncMappingStore`, and `SyncRuntimeStatus.Mappings` did not exist.
- Green targeted evidence: core mapping/IPC tests passed, 22 total.
- Green targeted evidence: integration service status mapping tests passed, 2 total.
- Green targeted evidence: app sync summary/XAML tests passed, 34 total.
- Green focused evidence after integration: core sync/IPC tests passed, 25 total; integration status tests passed, 3 total; app sync/XAML tests passed, 34 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 117 App, 130 Core, 75 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- `R3-002` is a confirmation gate and status foundation only; actual chunk hydration, blocked target handling, and conflict actions remain `R3-003`.
- Mapping confirmation is represented in repository metadata and Diagnostics, not as an editable Options setting.

## R3-004: sync idempotency and loop-prevention metadata

Status: complete.

Plan:

- Add sync-origin metadata for remote-applied versions: source device, source operation, source version, optional mapping, and applied timestamp.
- Mark remote-applied repository commits distinctly and preserve sync-origin metadata in manifests and version summaries.
- Add repository-owned applied remote-version records under sync metadata, keyed by source operation for deduplication.
- Add a publish gate that suppresses remote-applied manifests from being republished as fresh local captures.
- Expose applied remote-version records through service status/IPC and show compact applied count in Diagnostics.
- Update README and roadmap docs/tracker for `R3-004`.

Evidence:

- Red evidence: `dotnet test tests\FluxVault.Core.Tests\FluxVault.Core.Tests.csproj --filter "FullyQualifiedName~SyncLoopPreventionTests|FullyQualifiedName~IpcSerializationTests"` initially failed because `SyncOriginMetadata`, `SyncAppliedVersionRecord`, `FileSyncApplicationStore`, `SyncPublishGate`, `VersionOperationType.RemoteSync`, `FileCommitRequest.SyncOrigin`, `FileVersionManifest.SyncOrigin`, `RepositoryVersionSummary.SyncOrigin`, and `SyncRuntimeStatus.AppliedRemoteVersions` did not exist.
- Green targeted evidence: core loop-prevention/IPC tests passed, 21 total.
- Green targeted evidence: integration service status tests passed, 2 total.
- Green targeted evidence: app sync summary/XAML tests passed, 34 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 117 App, 132 Core, 76 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- `R3-004` provides loop-prevention metadata and gates only; actual remote hydration and watcher suppression are completed in `R3-003`.
- Applied remote-version records are repository metadata, not user-editable Options configuration.

## R3-003: chunk-level sync, blocked targets, and conflicts

Status: complete.

Plan:

- Add sync hydration records, blocked state, conflict records, and conflict actions.
- Add a local repository-to-repository sync hydrator that copies missing chunk/metadata artefacts from a peer repository and commits safe targets as `RemoteSync`.
- Detect locked/unavailable targets and record blocked hydration without overwriting target files.
- Detect local-content conflicts, preserve the existing target, and record conflict actions.
- Add resolve-conflict IPC metadata that records the selected resolution action without destructive file changes.
- Expose hydration and conflict records through service status/IPC and show compact blocked/conflict counts in Diagnostics.
- Update README and roadmap docs/tracker for `R3-003`.

Evidence:

- Red evidence: `dotnet test tests\FluxVault.Core.Tests\FluxVault.Core.Tests.csproj --filter "FullyQualifiedName~SyncHydrationTests|FullyQualifiedName~IpcSerializationTests"` initially failed because `SyncHydrationRecord`, `SyncHydrationState`, `SyncConflictRecord`, `SyncConflictStatus`, `SyncConflictAction`, `FileSyncHydrator`, `FileSyncHydrationStore`, `FileSyncConflictStore`, `FluxVaultIpcRequest.ResolveConflict`, and `SyncRuntimeStatus.Hydrations/Conflicts` did not exist.
- Green targeted evidence: core hydration/conflict/IPC tests passed, 24 total.
- Green targeted evidence: integration sync status and conflict command tests passed, 3 total.
- Green targeted evidence: app sync summary/XAML tests passed, 34 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 117 App, 137 Core, 78 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- `R3-003` implements local sync hydration contracts and safety behaviour; live peer transport/background polling remains future polish.
- Conflict resolution actions are recorded safely first; this slice does not destructively apply keep-remote or restore-as-copy file operations through the UI.

## R4-001: WinFsp performance workspace foundation

Status: complete.

Plan:

- Add disabled-by-default typed WinFsp performance-workspace configuration with workspace path, cache budget, and mount name.
- Expose prepared/deferred WinFsp workspace status through IPC/service status and Diagnostics.
- Add repo-owned static WinFsp setup scripts and manifest for review/static tests only.
- Ensure scripts/manifests do not install drivers, mount file systems, or mutate machine-wide state in tests or normal app flows.
- Update README and roadmap docs/tracker for `R4-001`.

Evidence:

- Red evidence: `dotnet test tests\FluxVault.Core.Tests\FluxVault.Core.Tests.csproj --filter "FullyQualifiedName~Default_configuration_has_disabled_winfsp_performance_workspace|FullyQualifiedName~Save_and_load_round_trips_winfsp_performance_workspace|FullyQualifiedName~IpcSerializationTests"` initially failed because `PerformanceWorkspaceConfiguration`, `PerformanceWorkspaceMode`, `PerformanceWorkspaceRuntimeStatus`, `FluxVaultConfiguration.PerformanceWorkspace`, and `FluxVaultServiceStatus.PerformanceWorkspace` did not exist.
- Green targeted evidence: core configuration/IPC tests passed, 22 total.
- Green targeted evidence: integration service status and static WinFsp asset tests passed, 2 total.
- Green targeted evidence: app Diagnostics status/XAML tests passed, 2 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 117 App, 139 Core, 80 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- R4-001 prepares the WinFsp workspace contract and repo assets only; no driver installation, mount registration, or machine-wide mutation is run.
- The configuration is not exposed in Options yet because the real WinFsp write path is not active in this safe foundation slice.

## R5-001: Cloud Files API / ProjFS shell integration foundation

Status: complete.

Plan:

- Add disabled-by-default typed shell integration configuration for Cloud Files API and ProjFS modes, including sync-root path, display name, hydration policy, and placeholder state root.
- Expose prepared/deferred Cloud Files / ProjFS status through IPC/service status and Diagnostics.
- Add repo-owned static registration manifests and setup/unsetup scripts for review/static tests only.
- Ensure scripts/manifests do not register sync roots, register providers, create placeholders, install drivers, or mutate machine-wide state in tests or normal app flows.
- Keep Explorer context menu behaviour unchanged; R5 prepares the namespace/hydration contract without changing the unsigned consumer release profile.
- Update README and roadmap docs/tracker for `R5-001`.

Evidence:

- Baseline evidence: `dotnet test` passed before R5 changes across App/Core/Integration/Windows test projects: 117 App, 139 Core, 80 Integration, 17 Windows.
- Red evidence: targeted core, integration, and app tests initially failed because `ShellIntegrationConfiguration`, `ShellIntegrationRuntimeStatus`, `ShellIntegrationMode`, `ShellHydrationPolicy`, `FluxVaultConfiguration.ShellIntegration`, `FluxVaultServiceStatus.ShellIntegration`, and `MainWindowViewModel.ShellIntegrationStatus` did not exist.
- Green targeted evidence: core configuration/IPC tests passed, 3 total.
- Green targeted evidence: integration service status and static shell-integration asset tests passed, 2 total.
- Green targeted evidence: app Diagnostics status/XAML/native-configuration preservation tests passed, 3 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 118 App, 141 Core, 82 Integration, 17 Windows.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Notes:

- A parallel targeted `dotnet test` retry hit transient compiler DLL locks from simultaneous builds. The root cause was concurrent project builds in the same worktree; sequential reruns passed, and subsequent FluxVault test runs for this item were kept serial.

Assumptions:

- R5-001 is a safe shell-integration foundation only; no sync-root registration, ProjFS provider registration, placeholder creation, or machine-wide mutation is run.
- The configuration is not exposed in Options yet because the real shell write/hydration path is not active in this safe foundation slice.

## R6-001: direct cloud adapters

Status: complete.

Plan:

- Add typed direct-cloud configuration for Azure Blob, S3-compatible storage, Dropbox, Google Drive, and OneDrive adapters, including credential references, endpoint/container/root metadata, bandwidth/metered policy fields, and disabled-by-default compatibility.
- Add cloud object adapter contracts and a provider-neutral adapter implementation that can be tested with fake clients without credentials or live provider calls.
- Add provider SDK descriptors bound to official SDK package/client types so adapter wiring is real and compile-checked while live credential flows remain deferred.
- Expose direct-cloud provider/adaptor readiness through IPC/service status and Diagnostics, clearly reporting that live validation is deferred.
- Update README and roadmap docs/tracker for `R6-001`; note that live provider validation, resumable transfer execution, and credential configuration require explicit future permission.

Evidence:

- Baseline evidence: `dotnet test` passed before R6 changes across App/Core/Integration/Windows test projects: 118 App, 141 Core, 82 Integration, 17 Windows. Restore/test used NuGet package/cache/scratch paths under the R6 worktree to honour the no-outside-boundaries instruction.
- Red evidence: targeted core tests failed because `FluxVault.Abstractions.Cloud`, `FluxVault.Core.Cloud`, direct-cloud configuration, provider enums, adapter contracts, SDK catalogue, and direct-cloud IPC status did not exist.
- Red evidence: targeted integration and app tests failed because `FluxVaultConfiguration.DirectCloud`, `FluxVaultServiceStatus.DirectCloud`, direct-cloud runtime status rows, and `MainWindowViewModel.DirectCloudStatus` did not exist.
- Green targeted evidence: core configuration, SDK catalogue, fake-client adapter, and IPC tests passed, 5 total.
- Green targeted evidence: integration service status and package declaration tests passed, 2 total.
- Green targeted evidence: app Diagnostics/native-configuration/About roadmap tests passed, 4 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 118 App, 145 Core, 84 Integration, 17 Windows. Restore/test used NuGet package/cache/scratch paths under the R6 worktree.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- R6-001 is a safe direct-adapter foundation: SDK packages and client-type bindings are compile-checked, fake clients exercise behaviour, and no credentials or live provider calls are used.
- Provider credentials are represented as references only; no secret values are stored in configuration or tests.

## LATER-001: client-side encryption and enterprise/fleet foundations

Status: complete.

Plan:

- Add disabled-by-default typed security-posture configuration with client-side encryption mode, algorithm, metadata mode, active key-reference id, and key-reference records that carry references only, not raw key material.
- Add disabled-by-default typed enterprise/fleet configuration with local policy mode, policy source, policy assignments, and local fleet status records.
- Add pure security/fleet contract helpers that can plan encryption readiness and evaluate a local fleet policy document without touching repository payloads, credentials, remote services, or machine-wide state.
- Expose security posture and fleet policy state through IPC/service status and Diagnostics.
- Preserve non-editable security/fleet configuration during dashboard save/discard flows.
- Update README, roadmap docs, security docs, testing strategy, and tracker for `LATER-001`, clearly noting that encryption execution, key provider integration, and remote enterprise control remain future implementation.

Evidence:

- Baseline evidence: `dotnet test` passed before LATER-001 changes across App/Core/Integration/Windows test projects: 118 App, 145 Core, 84 Integration, 17 Windows. Restore/test used NuGet package/cache/scratch paths under the LATER worktree.
- Red evidence: targeted core tests failed because `FluxVault.Abstractions.Security`, `FluxVault.Core.Security`, security/fleet configuration, security/fleet IPC status, and related contracts did not exist.
- Red evidence: targeted app/docs tests failed because `LATER-001` was still `Deferred` in `docs/roadmap-tracker.md`.
- Green targeted evidence: core configuration, IPC, encryption planning, and local fleet policy tests passed, 26 total.
- Green targeted evidence: integration service status test passed, 1 total.
- Green targeted evidence: app Diagnostics, dashboard-save preservation, XAML, and tracker tests passed, 4 total.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects: 119 App, 151 Core, 85 Integration, 17 Windows. Restore/test used NuGet package/cache/scratch paths under the LATER worktree.
- Whitespace verification: `git diff --check` passed. Git reported LF-to-CRLF working-copy warnings only.

Blockers:

- None.

Assumptions:

- This is a safe foundation slice only; repository artefacts remain plain until a later explicit encryption execution item.
- Key material must not be stored in FluxVault configuration; configuration stores references to keys or credential locations only.
- Fleet policy work is local schema/status only; no remote management plane, tenant registration, or device enrolment calls are made.

## POST-ROADMAP-001: documentation consistency sweep

Status: complete.

Plan:

- Re-scan the tracker, roadmap, README, and requirements after the full roadmap pass.
- Add a docs consistency test for the stale requirements product-goal wording that still described direct cloud adapters as later work.
- Update the requirements product goal so it reflects the implemented disabled-by-default R6 direct-cloud adapter foundation without claiming live credentials or cloud calls.

Evidence:

- Red evidence: targeted app docs test failed because `docs/requirements.md` still contained `direct cloud adapters arrive later`.
- Green evidence: targeted app docs test passed after updating the requirements product goal.
- Full verification: `dotnet test` passed across App/Core/Integration/Windows test projects after the docs correction.
- Whitespace verification: `git diff --check` passed.

Blockers:

- None.

Assumptions:

- This item changes documentation consistency only. It does not change roadmap capability status, runtime behaviour, packaging, installer output, credentials, cloud calls, or machine-wide state.
