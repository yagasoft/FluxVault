# Roadmap

FluxVault is being built as a configurable, low-friction protection platform
for large files on Windows. The roadmap deliberately separates one-PC
reliability, mirror fabric, multi-PC sync, write-path optimisation, shell
integration, and direct cloud adapters.

Implementation status is tracked separately in
[`docs/roadmap-tracker.md`](roadmap-tracker.md). Keep this roadmap
product-facing; update the tracker in every PR that changes roadmap status.

## MVP+ control bundle

Expected user-visible result: FluxVault feels controllable and professional for
one Windows 11 PC.

- Configurable watcher polling, reconciliation, debounce, maximum hot-file
  delay, same-file capture interval, and capture concurrency.
- Forced hot-file snapshots when a file keeps changing past the configured
  maximum delay.
- Capture runtime states: waiting for quiet window, forced hot-file snapshot,
  capturing, captured, blocked, and failed.
- Non-interference contract: read-only source opens with compatible sharing,
  VSS reads from the shadow path, and repository writes stay outside source
  folders unless explicitly configured.
- Blocked-file status in diagnostics, dashboard activity, and tray activity.
- FluxVault icon in the executable, taskbar, and tray.
- Left-navigation dashboard with Protection, File browser, Repository, Mirrors,
  Activity, Options, and Diagnostics.
- File browser tab with three panes: drives/folders, files in the selected
  folder, and pending unsaved selection changes.
- File browser folder selection cycles through recursive selected, immediate
  files only, and not selected. Manual child selections are retained when a
  parent recursive selection is removed, and parent folders show child-selection
  indicators. Folder state is shown with compact checkbox-style visual
  indicators rather than long state labels.
- File browser panes use equal space with horizontal and vertical scrolling, so
  long folder paths do not force the whole dashboard wider.
- Protection-tab folder browse/add controls are removed once the File browser
  becomes the primary watched-selection workflow. Selection changes apply only
  after Save. Unsaved selection edits are preserved across refresh and app
  focus changes until the user saves or explicitly discards them.
- Scoped include/exclude regex rules attach to selected File browser folders or
  files. Recursive folder rules apply recursively, immediate folder rules apply
  only to direct files, and child rules are additive with inherited parent rules
  before capture.
- File browser context menus open folders in File Explorer, open files in the
  default app, and show files selected in File Explorer.
- Health tiles live in the footer so the header remains focused on service
  status and commands.
- About window with Yagasoft branding, project and website links, app version,
  copyright/licence text, and brief roadmap milestones. The FluxVault icon
  remains the executable, taskbar, and tray icon.
- Concept A, the operational cockpit, is the selected runtime dashboard
  direction: stable header commands, left navigation, first-tab startup, and
  footer health tiles.
- Helpful Options tooltips and compact health-strip details for durable-change
  fallback reasons.
- Tray activity pane with recent events, pending work, blocked files, retention
  result, and quick actions.
- Adaptive compression policy with zstd default, lz4 for hot files, Brotli for
  ratio-oriented data, LZMA for cold archive, and off for compressed formats.
- Feature PRs wait only for `build-test`; CodeQL runs after main pushes,
  scheduled scans, or manual dispatch.

## V1 production hardening

Expected user-visible result: safe local production use on Windows 11.

- Installer polish, service recovery, clean uninstall, and upgrade/rollback
  testing. The first hardening slice adds dashboard service availability
  warnings, Start/Stop service control, Windows Event Log wiring, SCM restart
  recovery, delayed automatic service start, and safer developer install and
  uninstall scripts. The completion slice adds a WiX per-machine MSI, a WiX
  Burn bootstrapper, stable major-upgrade metadata, ProgramData preservation,
  and release-package validation separate from the feature PR gate. The default
  consumer release is one unsigned bootstrapper that chains only the MSI and
  warns about Unknown publisher and SmartScreen prompts.
- Better VSS writer handling and app-consistency reporting. The first
  implementation replaces the `vssadmin.exe` fallback with a FluxVault VSS
  requester that coordinates writers, creates the snapshot through COM, reads
  from the shadow path, reports `AppConsistent` only when writer metadata covers
  the source path, and otherwise reports successful VSS captures as
  `CrashConsistent`. The completion slice adds CI-friendly requester adapter
  tests for call order, writer metadata interpretation, writer-status failure,
  cleanup, and failed-capture no-commit behaviour; FluxVault does not claim
  certification for every third-party VSS writer workload.
- Restore browser with safer overwrite workflows and Explorer entry points.
  The first implementation adds app-side destination picking, overwrite
  confirmation before restore IPC, safe restore failure status, per-user HKCU
  Explorer file/folder actions managed from Options, ordered Add/Show FluxVault
  versions/Remove full-menu verbs, immediate Add/Remove configuration saves, and
  second-launch `--restore-path`/`--show-versions` handoff to the running
  dashboard. Windows 11 compact-menu registration appears through the same
  Options status. A sparse package manifest and native `IExplorerCommand`
  handler scaffold provide the compact-menu packaging path for developer/future
  signed package profiles, while unpackaged and unsigned consumer builds still
  use the full menu.
- Per-file version graph for restore and inherited-copy lineage. Same-path
  captures record parent versions. Restoring an old version writes a pending
  lineage hint, and the next capture records the restored version as the fork
  origin while newer versions remain available.
- Restore history shows linear versions, restored-from links, inherited-copy
  links, forks, and conflict groups without hiding later versions.
- Health dashboard for repository integrity, USN state, mirror state, blocked
  files, and last successful restore rehearsal. The first implementation expands
  Diagnostics into the health dashboard and adds manual scrub/rehearsal actions.
- Repository scrubber for bit-rot detection and missing-chunk repair from a
  healthy mirror. Scrub checks only artefacts referenced by remaining manifests,
  repairs primary artefacts from a healthy mirror, repairs mirror artefacts from
  a healthy primary copy, and reports critical unresolved issues when neither
  side is healthy.
- Scheduled restore rehearsal to a temporary location. Rehearsal restores recent
  versions into FluxVault-owned service state, verifies logical length, deletes
  the temporary output, and does not overwrite user files or create versions.
- Workload policy presets for general purpose, Office, CAD/BIM, Adobe/video,
  developer workspaces, and generic large files. The implemented conservative
  catalogue is selectable per protected File browser item, Options controls the
  default for new selections, generated/cache folders are skipped where safe,
  and global skip extensions remain the final no-compression guardrail.

## R2 distributed mirror fabric

Expected user-visible result: multiple local or cloud-sync folders behave like
one storage fabric.

- Replace the single mirror path with a `MirrorSet`. The first implemented
  slice migrates legacy `mirrorPath` config into one enabled full-copy mirror
  node and adds a dedicated Mirrors workspace for node label, path, and enabled
  state.
- Primary repository commits remain authoritative. Mirror nodes receive
  committed artefacts according to the active placement profile after the
  primary commit succeeds; unavailable mirrors report node-specific warnings
  without failing the backup.
- Mirror nodes now carry capacity budget, priority, health, online/offline
  state, and placement profile inputs.
- Implemented placement profiles are `FullCopy`, `CapacityBalanced`, and
  `Redundant`.
- Capacity mode uses deterministic weighted rendezvous hashing with capacity
  budget and priority. With two mirrors, chunks for a file may live on either
  mirror; with more mirrors, chunks distribute across selected eligible nodes.
- Redundancy mode overrides capacity-only placement by requiring a configured
  minimum mirror copy count.
- The repair foundation provides explicit preview and repair actions for
  primary/mirror drift and per-node mirror health. The placement foundation
  applies the configured policy to new commits, previews existing placement
  movement without writing files, executes safe copy/delete rebalancing, and
  drains a selected mirror by copying required artefacts elsewhere before
  disabling the node. Ordinary mirror row removal remains a separate
  configuration edit.

## R3 multi-PC sync

Expected user-visible result: several PCs share protected folders without
downloading or rewriting whole files unnecessarily.

- Implemented: stable local device identity plus typed trusted-device records.
  Legacy configurations normalise to one local trusted device, and Diagnostics
  shows the device id and trust summary.
- Future sync UI can add safe trust editing once peer exchange workflows exist.
- Implemented: each PC can own a sync-control area in the repository with a
  compact peer head, immutable operation records, and local per-peer cursors.
- Implemented: repository-owned mapping records keep newly protected remote
  folders or files pending until each peer confirms the same path or chooses a
  per-PC override. The sync mapping gate blocks hydration unless the mapping is
  confirmed and has a local target path.
- Implemented: remote-applied versions can carry source version id, source
  operation id, origin device id, mapping id, and applied-version metadata.
  A publish gate can suppress those remote-applied manifests so FluxVault does
  not republish its own sync hydration writes as new local changes.
- Peers will poll compact peer heads first, then fetch only missing operation
  records since their local per-peer cursor. Operation records reference
  FluxVault versions, manifests, chunks, operation ids, source device ids, and
  source version metadata rather than duplicating file content.
- Lineage content signatures from V1 let sync describe copied or restored
  byte-identical files as metadata/chunk references instead of republishing the
  same content payload.
- Implemented: a local sync hydrator can copy only missing chunk/metadata
  artefacts from a peer repository, reconstruct the remote version, and commit
  the local result as a remote-applied version with loop-prevention metadata.
- Implemented: open or locked target files become blocked rather than
  overwritten. Conflicts preserve the existing target and record keep-local,
  keep-remote, restore-as-copy, and mark-resolved actions.
- Sync status shows peer heads, cursors, pending mappings, applied remote
  versions, hydration progress, conflicts, and blocked targets.
- Live peer transport, background polling, file patch optimisation, and
  full conflict UI workflow remain future sync polish built on these contracts.
- Direct cloud adapters may later optimise discovery with provider-native
  change feeds, but R3 multi-PC sync must work with ordinary cloud-sync folders
  using repository-owned peer metadata.

## R4 WinFsp performance workspace

Expected user-visible result: best-in-class handling for huge, constantly
changing files.

- Optional FluxVault-managed workspace or drive for selected workloads.
- Implemented: typed performance-workspace configuration records WinFsp mode,
  workspace path, cache budget, and mount name with disabled-by-default
  compatibility.
- Implemented: service status and Diagnostics expose the prepared WinFsp
  workspace state and explicitly report that driver checks and registration are
  deferred.
- Implemented: repo-owned `eng/winfsp` setup scripts and a registration
  manifest are present for review and static tests only; they do not run during
  tests or normal app flows.
- Future work can make WinFsp control the write path without a FluxVault-owned
  kernel driver, add write-range journalling for hot large-file workloads, and
  add managed migration into or out of the workspace.
- The workspace is designed to use the same repository, mirror, retention, and
  sync metadata.

## R5 Cloud Files API / ProjFS shell integration

Expected user-visible result: Windows-native placeholder, hydration, and
Explorer behaviour.

- Implemented: typed shell-integration configuration records Cloud Files API
  or ProjFS mode, sync-root path, display name, hydration policy, and
  placeholder state path with disabled-by-default compatibility.
- Implemented: service status and Diagnostics expose the prepared shell
  integration state and explicitly report that registration and placeholder
  creation are deferred.
- Implemented: repo-owned `eng/shell-integration` setup scripts and a
  manifest are present for review and static tests only; they do not run during
  tests or normal app flows.
- Future work can perform signed/package-aware sync-root registration,
  placeholder creation, hydration status, Explorer badges, context menu
  restore/version actions, `Always keep on this device`, and `free up local
  space` workflows.
- R5 remains built on the R3 sync engine and R2 mirror fabric.

## R6 direct cloud adapters

Expected user-visible result: FluxVault can use object storage without relying
on a sync folder.

- Implemented: typed direct-cloud configuration records Azure Blob,
  S3-compatible, Dropbox, Google Drive, and OneDrive adapter metadata with
  credential references, root/container/prefix fields, bandwidth policy, and
  metered-network policy while defaulting disabled.
- Implemented: provider-neutral object adapter contracts can be exercised with
  fake clients, so tests cover object write/read/metadata/list/delete without
  credentials or live account calls.
- Implemented: provider SDK descriptors are compile-checked against official
  package/client types for Azure Blob, S3-compatible storage, Dropbox, Google
  Drive, and OneDrive.
- Implemented: service status and Diagnostics expose direct-cloud readiness and
  explicitly report that live validation is deferred.
- Future work can add credential acquisition, resumable upload execution,
  bandwidth windows, provider-native change feeds, metered-network runtime
  enforcement, object-lock support where available, lifecycle policy alignment,
  and live repository portability between cloud-folder and direct-adapter modes.

## Later be-all-end-all features

- Implemented foundation: disabled-by-default client-side encryption
  configuration, key-reference records, encryption planning contracts, and
  Diagnostics security status. Future work performs repository encryption,
  recovery-key flows, and key-provider integration.
- Implemented foundation: disabled-by-default enterprise/fleet policy
  configuration, local policy/status contracts, and Diagnostics fleet status.
  Future work adds remote policy distribution, device enrolment, and fleet
  monitoring.
- Per-application optimisers.
- Timeline restore with calendar/search view.
- `What changed?` chunk-level version analytics.
- Ransomware-like churn detection.
- Repository health score and disaster recovery wizard.
- Optional headless/server agent mode if Windows Server becomes in scope.
