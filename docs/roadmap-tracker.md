# Roadmap tracker

This is the authoritative implementation tracker for the FluxVault roadmap.
`docs/roadmap.md` describes the product direction; this file records what has
actually been implemented, what is planned, and what evidence supports that
status.

## Status values

- `Implemented`: shipped in `main` with source and verification evidence.
- `Partially implemented`: usable foundation exists, but the roadmap capability
  is not complete.
- `Planned`: accepted on the roadmap, not implemented yet.
- `Deferred`: intentionally later than the current roadmap stage.
- `Blocked`: cannot proceed until an explicit dependency is resolved.

## Process rule

Every future PR that implements, removes, or materially changes a roadmap
capability must update this tracker in the same PR. PR descriptions should
include a short `Tracker` line naming the changed item ids. Docs-only roadmap
additions should create `Planned` rows; implementation PRs should update the
affected rows to `Implemented` or `Partially implemented` with PR, commit, and
verification evidence. CodeQL remains outside the PR gate unless explicitly
requested.

## MVP and MVP+ tracker

| Item id | Stage | Capability | Status | Implementation evidence | Verification evidence | Notes / next step |
| --- | --- | --- | --- | --- | --- | --- |
| MVP-001 | MVP | CLI backup/list/inspect/restore harness | Implemented | PR #1 `cf5ea62`; `src/FluxVault.Cli`; `src/FluxVault.Core/Storage` | `tests/FluxVault.Integration.Tests/CliHarnessTests.cs`; `tests/FluxVault.Core.Tests/RepositoryTests.cs` | Developer-visible repository loop is available for normal readable files. |
| MVP-002 | MVP | WPF dashboard, tray app, and Windows service | Implemented | PR #2 `c5a6cd8`; `src/FluxVault.App`; `src/FluxVault.Service` | `tests/FluxVault.Integration.Tests/ServiceOperationsTests.cs`; app tests under `tests/FluxVault.App.Tests` | Unsigned local install remains the MVP packaging path. |
| MVP-003 | MVP | Named-pipe JSON IPC | Implemented | PR #2 `c5a6cd8`; `src/FluxVault.Core/Ipc`; `src/FluxVault.Abstractions/Ipc` | `tests/FluxVault.Core.Tests/IpcSerializationTests.cs` | Current app/service API is request/response IPC. |
| MVP-004 | MVP | Persisted service configuration | Implemented | PR #2 `c5a6cd8`; `src/FluxVault.Core/Configuration`; `src/FluxVault.Abstractions/Configuration` | `tests/FluxVault.Core.Tests/ConfigurationStoreTests.cs`; `tests/FluxVault.Integration.Tests/WorkflowConfigurationTests.cs` | Config path remains `C:\ProgramData\FluxVault\config.json`. |
| MVP-005 | MVP | Watched-folder backup loop with notifications, USN catch-up, and reconciliation | Implemented | PR #4 `6ca9942`; PR #9 `6a5173e`; `src/FluxVault.Core/Service/FileSystemProtectionLoop.cs`; `src/FluxVault.Core/ChangeTracking`; `src/FluxVault.Windows/ChangeTracking` | `tests/FluxVault.Integration.Tests/ProtectionLoopUsnTests.cs`; `tests/FluxVault.Core.Tests/UsnCatchUpServiceTests.cs`; `tests/FluxVault.Windows.Tests` | USN is durable catch-up where available; reconciliation remains fallback. |
| MVP-006 | MVP | Normal readable-file capture with VSS fallback | Implemented | PR #2 `c5a6cd8`; PR #6 `734ea42`; `src/FluxVault.Core/Capture`; `src/FluxVault.Windows/Capture` | `tests/FluxVault.Core.Tests/CaptureProviderTests.cs`; `tests/FluxVault.Windows.Tests/CapturePlanningTests.cs` | VSS writer-aware app-consistency is still V1 hardening. |
| MVP-007 | MVP | Streaming chunk repository, manifests, BLAKE3, and compression policy | Implemented | PR #1 `cf5ea62`; PR #6 `734ea42`; `src/FluxVault.Core/Chunking`; `src/FluxVault.Core/Storage`; `src/FluxVault.Core/Content`; `src/FluxVault.Core/Policies` | `tests/FluxVault.Core.Tests/StreamingChunkingTests.cs`; `RepositoryTests.cs`; `ContentCodecTests.cs`; `CodecPolicySelectorTests.cs` | zstd remains default; lz4, Brotli, LZMA, and off are policy choices. |
| MVP-008 | MVP | Optional single cloud-folder mirror | Implemented | PR #1 `cf5ea62`; `src/FluxVault.Core/Storage/FileSystemChunkRepository.cs` | `tests/FluxVault.Integration.Tests/RepositoryMirrorTests.cs` | R2 expands this into multi-node mirror fabric. |
| MVP-009 | MVP | Dashboard auto-refresh | Implemented | PR #3 `16b9381`; `src/FluxVault.App/ViewModels/MainWindowViewModel.cs`; `src/FluxVault.Core/Ipc/IFluxVaultServiceClient.cs` | `tests/FluxVault.App.Tests/MainWindowViewModelRefreshTests.cs` | Polling refresh is the MVP approach; push notifications remain optional later. |
| MVP-010 | MVP+ | Retention engine and Options dialog | Implemented | PR #5 `c6160d7`; `src/FluxVault.Core/Retention`; `src/FluxVault.App/OptionsWindow.xaml`; `src/FluxVault.App/ViewModels/OptionsViewModel.cs` | `tests/FluxVault.Core.Tests/RetentionPlannerTests.cs`; `RepositoryTests.cs`; `tests/FluxVault.App.Tests/OptionsViewModelTests.cs` | Conservative default retention is enabled by default. |
| MVP-011 | MVP+ | App icon, activity pane, status tiles, tooltip wrapping, and USN diagnostics | Implemented | PR #6 `734ea42`; PR #7 `4e0433b`; PR #8 `91d4809`; PR #9 `6a5173e`; `src/FluxVault.App`; `src/FluxVault.Windows/ChangeTracking` | `tests/FluxVault.App.Tests/XamlQualityTests.cs`; `TrayPanePlacementTests.cs`; `tests/FluxVault.Windows.Tests/UsnJournalDiagnosticFormatterTests.cs` | Covers the current UI polish and diagnostics bundle. |
| MVP-012 | MVP+ | PR gate rule: feature PRs wait for build-test only | Implemented | PR #6 `734ea42`; `.github/workflows` | `docs/packaging-release.md`; CI checks on PRs #7-#10 | CodeQL is async on main/manual/schedule unless explicitly requested. |
| MVP-020 | MVP+ | Three-pane File browser selection UX | Implemented | PR #12; `src/FluxVault.App/ViewModels/FileBrowserViewModel.cs`; `src/FluxVault.Core/Configuration/ProtectionSelectionCompiler.cs`; `src/FluxVault.Abstractions/Configuration/ProtectionSelectionRule.cs` | `tests/FluxVault.App.Tests/FileBrowserViewModelTests.cs`; `tests/FluxVault.App.Tests/XamlQualityTests.cs`; `tests/FluxVault.Integration.Tests/ServiceOperationsTests.cs` | File browser selections compile into existing watched-folder rules; repository/mirror path browsing remains in Protection. |

## V1 tracker

| Item id | Stage | Capability | Status | Implementation evidence | Verification evidence | Notes / next step |
| --- | --- | --- | --- | --- | --- | --- |
| V1-001 | V1 | Installer polish, service recovery, clean uninstall, upgrade/rollback | Planned | `docs/roadmap.md`; `docs/packaging-release.md` | Planned packaging tests in `docs/testing-strategy.md` | MVP has unsigned scripts; production installer is still future work. |
| V1-002 | V1 | Better VSS writer handling and app-consistency reporting | Planned | `docs/roadmap.md`; `docs/architecture.md` | Planned capture consistency tests | Current VSS fallback is present, but writer-aware proof is not complete. |
| V1-003 | V1 | Restore browser hardening and Explorer entry points | Planned | `docs/roadmap.md` | Planned restore workflow tests | Basic restore exists; safer overwrite workflows and Explorer integration do not. |
| V1-004 | V1 | Per-file version graph and restore lineage | Planned | Roadmap PR #10 `0c72fd3`; `docs/storage-format.md`; `docs/architecture.md` | Planned restore lineage tests in `docs/testing-strategy.md` | Use Git-like metadata over FluxVault manifests, not a live `.git` store. |
| V1-005 | V1 | Health dashboard, repository scrubber, and restore rehearsal | Planned | `docs/roadmap.md` | Planned integrity and restore rehearsal tests | Current health strip exists, but full health dashboard and scrubber do not. |
| V1-006 | V1 | Workload policy presets | Planned | `docs/roadmap.md` | Planned policy-resolution tests | Presets for Office, CAD/BIM, Adobe/video, developer workspaces, and generic large files. |

## R2 to later tracker

| Item id | Stage | Capability | Status | Implementation evidence | Verification evidence | Notes / next step |
| --- | --- | --- | --- | --- | --- | --- |
| R2-001 | R2 | Distributed mirror fabric and `MirrorSet` | Planned | `docs/roadmap.md` | Planned mirror placement tests | Builds on MVP-008 single mirror. |
| R2-002 | R2 | Mirror capacity balancing, redundancy, drain, and repair | Planned | `docs/roadmap.md` | Planned mirror maintenance tests | Includes weighted placement, offline nodes, drain/remove, and repair. |
| R3-001 | R3 | Stable device identity and trusted device records | Planned | `docs/roadmap.md`; `docs/architecture.md` | Planned device identity tests | Required before multi-PC sync. |
| R3-002 | R3 | Sync mapping confirmation gate | Planned | Roadmap PR #10 `0c72fd3`; `docs/architecture.md`; `docs/requirements.md` | Planned sync mapping tests in `docs/testing-strategy.md` | Peers must confirm same-path or override mapping before hydration. |
| R3-003 | R3 | Multi-PC chunk-level sync, blocked targets, and conflicts | Planned | `docs/roadmap.md` | Planned sync, blocked-file, and conflict tests | Conflict resolution must preserve both versions. |
| R4-001 | R4 | WinFsp performance workspace | Deferred | `docs/roadmap.md` | Future WinFsp integration tests | Separate track because it changes the write path. |
| R5-001 | R5 | Cloud Files API / ProjFS shell integration | Deferred | `docs/roadmap.md` | Future shell integration tests | Separate track because it changes namespace and hydration behaviour. |
| R6-001 | R6 | Direct cloud adapters | Deferred | `docs/roadmap.md` | Future adapter integration tests | Azure Blob, S3-compatible storage, and later provider APIs. |
| LATER-001 | Later | Client-side encryption and enterprise/fleet features | Deferred | `docs/roadmap.md`; `docs/security-and-privacy.md` | Future security and policy tests | V1 stores repository artefacts in plain form. |
