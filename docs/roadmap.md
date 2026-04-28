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
- Left-navigation dashboard with Protection, Repository, Activity, Options, and
  Diagnostics.
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
- Global file/folder exclusion regex rules complement tree selection and apply
  to full normalised paths before capture.
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
  uninstall scripts. Production installer technology, signing, upgrade packages,
  and full rollback semantics remain open V1 work.
- Better VSS writer handling and app-consistency reporting. The first
  implementation replaces the `vssadmin.exe` fallback with a FluxVault VSS
  requester that coordinates writers, creates the snapshot through COM, reads
  from the shadow path, reports `AppConsistent` only when writer metadata covers
  the source path, and otherwise reports successful VSS captures as
  `CrashConsistent`.
- Restore browser with safer overwrite workflows and Explorer entry points.
  The first implementation adds app-side destination picking, overwrite
  confirmation before restore IPC, safe restore failure status, per-user HKCU
  Explorer file/folder actions managed from Options, and second-launch
  `--restore-path` handoff to the running dashboard. Restore lineage metadata
  remains separate.
- Per-file version graph for restore lineage. Restoring an old version creates
  a new version event that points to the restored version as its fork origin,
  while newer versions remain available.
- Restore history shows linear versions, restored-from links, forks, and
  conflict groups without hiding later versions.
- Health dashboard for repository integrity, USN state, mirror state, blocked
  files, and last successful restore rehearsal.
- Repository scrubber for bit-rot detection and missing-chunk repair from a
  healthy mirror.
- Scheduled restore rehearsal to a temporary location.
- Policy presets for Office, CAD/BIM, Adobe/video, developer workspaces, and
  generic large files.

## R2 distributed mirror fabric

Expected user-visible result: multiple local or cloud-sync folders behave like
one storage fabric.

- Replace the single mirror path with a `MirrorSet`.
- Mirror nodes have id, path, label, capacity budget, priority, health,
  online/offline state, include/exclude constraints, and placement profile.
- Placement profiles: `CapacityBalanced`, `Redundant`, `FullCopy`, and
  `Custom`.
- Capacity mode uses weighted rendezvous hashing with capacity watermarks and
  a configurable excluded mirror fraction. With two mirrors, chunks for a file
  may live on either mirror; with more mirrors, chunks distribute across the
  selected set while excluding the configured fraction.
- Redundancy mode overrides capacity-only placement by requiring a configured
  minimum copy count.
- Mirror maintenance can take a node offline, drain/remove it after
  consolidation, repair missing chunks, and preview movement before applying.

## R3 multi-PC sync

Expected user-visible result: several PCs share protected folders without
downloading or rewriting whole files unnecessarily.

- Stable device identity from machine identity plus FluxVault installation id,
  with editable friendly names and repository trust records.
- Folder mappings use the same source path by default, with per-PC overrides.
- Newly protected folders or files published by one PC remain pending on other
  PCs until each peer confirms the same path or chooses a per-PC path override.
- Peer sync must not create, hydrate, or patch a newly mapped target before the
  mapping is confirmed.
- PCs publish manifests, device heartbeats, and folder mapping metadata into
  the shared repository.
- Other PCs hydrate only missing chunks and patch files at chunk level where
  the target file can be safely opened.
- Sync loop prevention is required: peers must use chunk existence checks plus
  source version id, operation id, sync origin, and device metadata so a
  remotely applied change is not republished forever as a new local version.
- Open or locked target files become blocked rather than overwritten.
- Conflicts preserve both versions and show a `Resolve conflict` action with
  keep local, keep remote, restore as copy, open both, and mark resolved.
- Sync status shows pending remote versions, missing chunks, hydration progress,
  conflicts, blocked files, and last peer seen.

## R4 WinFsp performance workspace

Expected user-visible result: best-in-class handling for huge, constantly
changing files.

- Optional FluxVault-managed workspace or drive for selected workloads.
- WinFsp controls the write path without a FluxVault-owned kernel driver.
- Better write-range journalling for CAD/BIM, video, database-like, and other
  hot large-file workloads.
- Configurable migration into or out of the managed workspace.
- Uses the same repository, mirror, retention, and sync metadata.

## R5 Cloud Files API / ProjFS shell integration

Expected user-visible result: Windows-native placeholder, hydration, and
Explorer behaviour.

- Sync-root registration, placeholder files, hydration status, Explorer badges,
  and context menu restore/version actions.
- `Always keep on this device` and `free up local space` workflows.
- Built on the R3 sync engine and R2 mirror fabric.

## R6 direct cloud adapters

Expected user-visible result: FluxVault can use object storage without relying
on a sync folder.

- Azure Blob, S3-compatible storage, and later OneDrive/Google Drive APIs.
- Resumable uploads, bandwidth windows, metered-network policy, object-lock
  support where available, and lifecycle policy alignment.
- Repository portability between cloud-folder and direct-adapter modes.

## Later be-all-end-all features

- Client-side encryption with device recovery keys.
- Enterprise policy templates and fleet monitoring.
- Per-application optimisers.
- Timeline restore with calendar/search view.
- `What changed?` chunk-level version analytics.
- Ransomware-like churn detection.
- Repository health score and disaster recovery wizard.
- Optional headless/server agent mode if Windows Server becomes in scope.
