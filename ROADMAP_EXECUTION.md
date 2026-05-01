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

- `R3-005` is metadata discovery foundation only; mapping confirmation, loop prevention, hydration, blocked targets, and conflict actions remain later R3 items.
- Operation records reference versions and metadata but do not duplicate chunk payloads.
