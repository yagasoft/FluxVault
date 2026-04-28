# Architecture

## Overview

FluxVault has four main runtime parts:

- `FluxVault.App`: WPF dashboard and tray shell.
- `FluxVault.Service`: per-machine Windows service that owns background work.
- `FluxVault.Core`: platform-neutral policy, chunking, repository, retention,
  restore, and diagnostics logic.
- `FluxVault.Windows`: Windows-specific capture providers such as USN, file
  notifications, and VSS.
- `FluxVault.Cli`: developer-facing backup/list/inspect/restore harness used to
  validate the repository before service and UI automation are complete.

The current MVP service exposes a local named-pipe JSON IPC surface for status,
configuration save, manual backup, version listing, version inspection, restore,
and diagnostics export. Configuration is stored in
`C:\ProgramData\FluxVault\config.json`; repository artefacts are written to the
configured local repository, with optional atomic mirroring into a cloud-sync
folder.

The dashboard checks the Windows service state separately from IPC. If
`FluxVaultService` is stopped, missing, inaccessible, or running without an
available pipe, the operational cockpit shows a warning and keeps the app open.
The dashboard can attempt to start or stop the service through Windows service
control APIs; access-denied results are surfaced as elevation-required messages.

The app owns per-user Explorer entry points. Options registers or unregisters
HKCU full-menu file/folder commands in this order: `Add to FluxVault`, `Show
FluxVault versions`, and `Remove from FluxVault`. Add and Remove requests are
forwarded through the same startup pipe and save configuration immediately.
`Show FluxVault versions` launches the current `FluxVault.App.exe
--show-versions "%1"` path; the legacy `--restore-path "%1"` argument remains
accepted. Startup request routing is single-instance: a second app process
forwards the request to the running dashboard through a local named pipe, then
exits. Version hints select a matching recent version where possible; they never
start a restore by themselves. Windows 11 compact menus require app identity and
an `IExplorerCommand` shell extension, so the current unpackaged app reports
compact registration as unavailable while still registering the full menu.

When installed by the developer script, `FluxVaultService` uses delayed
automatic start, SCM restart recovery, and the `FluxVaultService` Windows Event
Log source in the Application log. Service startup and shutdown are information
events, recoverable runtime fallbacks are warnings, and fatal background task
failures are logged as critical before being rethrown so SCM recovery can restart
the service.

## MVP capture flow

1. Directory notification wakes the service quickly when a watched path changes
   and records live pending capture work.
2. USN journal catch-up reads durable per-volume changes since the last saved
   checkpoint and targets only changed files when continuity is proven. Mature
   watcher changes run a USN catch-up before falling back to the watcher path.
3. Periodic reconciliation scans remain as a safety net when USN is unavailable,
   inaccessible, reset, or unsupported for the watched volume.
4. Smart cadence coalesces hot files to avoid repeated full-file reads. If a
   file keeps changing beyond its configured maximum hot-file delay, the service
   queues a snapshot anyway and reports a forced hot-file snapshot.
5. Normal stream reads capture readable files as `BestEffort`; the Windows
   service falls back to a writer-aware VSS requester for locked files when
   privileges allow it.
6. The VSS requester coordinates writers, creates the snapshot through the
   Windows VSS COM API, reads from the shadow device path, and checks writer
   metadata. Writer-covered captures are reported as `AppConsistent`; successful
   snapshots without matching writer coverage are reported as `CrashConsistent`;
   writer/requester failure fails the capture.
7. Core chunking splits the captured bytes into FastCDC-style chunks.
8. Chunk fingerprints are compared against the local repository.
9. New chunks and a manifest are committed atomically.
10. Repository artefacts are mirrored into the configured cloud sync folder.
11. Compression is selected by policy. The default adaptive profile uses zstd,
    skips known compressed file types, uses lz4 for hot files, and keeps Brotli
    and LZMA as explicit ratio/cold-archive choices.
12. If retention is enabled, the service applies the configured retention policy
    after the successful backup and reports kept, pruned, and reclaimed counts
    in service status.

## Capture cadence

The cadence policy is stored in `config.json` and is editable from Advanced
Options. Defaults are:

- Watcher poll interval: 5 seconds.
- Periodic reconciliation: 10 minutes.
- Debounce: Fast 2 seconds, Balanced 8 seconds, Quiet 30 seconds.
- Maximum hot-file delay: Fast 30 seconds, Balanced 2 minutes, Quiet 10 minutes.
- Minimum same-file capture interval: 15 seconds.
- Maximum concurrent captures: 1.

Directory notifications are latency hints and drive the live capture queue.
USN is the durable catch-up source where available. Reconciliation remains the
safety net when USN cannot prove continuity. Durable-change status carries a
latest-check label, timestamp, and structured diagnostic details, so the UI and
exported diagnostics can distinguish unable to open volume, unsupported volume,
journal ID change, journal wrap, checkpoint seeding, and file-id path
resolution failures.

## Options and status UX

Every editable Options control has a native WPF tooltip describing the
operational effect of the setting; long tooltip text wraps inside a bounded
width. The selected dashboard direction is Concept A, the operational cockpit:
stable primary commands in the header, left navigation for workspaces,
first-tab startup, and health tiles in the footer. The dashboard health strip
keeps status compact but exposes detailed durable-change
information through the USN tooltip and diagnostics export. The main header
constrains long service text with ellipsis trimming so it cannot overlap command
buttons. The Capture tile is the live watcher/debounce queue; the USN tile is
the latest durable catch-up result.

The tray activity pane is positioned from the active monitor working area. The
WinForms cursor and monitor coordinates are converted to WPF device-independent
units before the pane is clamped inside the visible work area; on very small
work areas the pane height is reduced instead of opening behind the taskbar.

The About window is an app-only branding surface. It uses the Yagasoft logo as
content, exposes the GitHub project and Yagasoft website links through the
default browser, and does not replace the FluxVault executable, taskbar, or tray
icon.

## File selection UX

The File browser replaces the older Protection-tab browse/add workflow. It is a
configuration editing surface, not an immediate service mutation. The left pane
shows drives and folders, the middle pane shows files in the selected folder,
and the right pane summarises unsaved selections and unselections.

Folder selection is tri-state: recursive selected, immediate files only, and
not selected. Manual child selections are retained below a parent so they can be
restored when a recursive parent selection is removed. Parent folders show a
compact child-selection indicator when descendants have manual selections. The
service reloads watched selection rules only after the user saves the browser
changes. While configuration or File browser edits are dirty, dashboard refresh
updates runtime status, versions, and activity without replacing local unsaved
selection rules or clearing the pending-changes pane.

The File browser tree owns its own scrollbars so mouse-wheel scrolling works
when the cursor is over the folder tree. File and pending-change grids auto-fit
their columns on first render, then rely on horizontal scrolling for long names
or paths rather than resizing the three-pane layout. Folder and file context
menus use a shell-launch abstraction so tests can prove `explorer.exe <folder>`,
`explorer.exe /select,<file>`, and default-app open behaviour without launching
Explorer.

FluxVault persists browser selections as `ProtectionSelectionRule` records and
compiles them into existing `WatchedFolderConfiguration` entries before the
service captures files. Recursive folders compile to recursive watched folders,
immediate-files selections compile to non-recursive watched folders, and
individual file selections compile to parent-folder include patterns.

Scoped `ProtectionScopedRegexRule` lists complement each selected folder or
file. Recursive folder regex rules apply to descendants; immediate-folder rules
apply only to files directly inside that folder; child rules are additive with
inherited parent rules. Include rules are treated as an additive narrowing set,
and any matching exclude rule suppresses the file before capture. Folders with
local scoped regex rules show an `R` indicator in the tree. Legacy global
`ProtectionExclusionRule` records are still understood by the service for
backward compatibility, but the Options global regex editor has been removed.

## Non-interference contract

FluxVault should not make normal user applications wait on backup reads.

- Normal capture opens source files read-only with `FileShare.ReadWrite |
  FileShare.Delete`.
- VSS capture reads from the shadow copy path, not the live source path. The
  service uses FluxVault's requester path in `FluxVault.Windows`, not
  `vssadmin.exe`.
- Consistency evidence is part of runtime status. `ConsistencyDetail` is an
  optional IPC field so older clients can ignore it while the dashboard,
  activity feed, diagnostics export, and newly written responses can explain
  writer coverage or no-writer-coverage outcomes.
- Repository and mirror writes are outside the watched source tree unless the
  user explicitly chooses such a layout.
- Restore asks the dashboard for a destination before IPC and requires explicit
  confirmation before overwriting an existing destination file. Locked or
  access-denied destination failures are returned as restore failures rather
  than crashing the dashboard or committing misleading UI state.
- Blocked files are surfaced through service status, diagnostics, the dashboard
  Activity view, and the tray activity pane.

## MVP harness flow

The CLI harness bypasses VSS/USN and commits a normal readable file stream
directly into the repository. This proves chunking, manifest creation, mirror
writes, listing, inspection, and restore without claiming open-file consistency.

## Storage boundary

The repository stores immutable chunks and append-only version manifests. A
manifest is the authoritative description of one captured file version. Chunks
are addressed by BLAKE3 digest and may be stored raw or encoded with zstd, lz4,
Brotli, or LZMA according to policy.

Restore remains append-only in the planned V1 lineage model. Restoring an older
version creates a new version event that records the restored source version and
fork origin, while newer versions remain queryable and restorable. This gives
FluxVault Git-like history semantics without using Git as the live repository
engine.

Retention is manifest-led. FluxVault groups versions by normalised source path,
keeps dense recent history, thins older history to hourly and daily buckets,
and always keeps at least the latest configured number of versions per source
file. After deleting pruned manifests locally, it garbage-collects only chunks
and metadata no remaining manifest references. Mirror cleanup is best-effort
and reported through status and diagnostics.

## Planned multi-PC sync safety

R3 sync publishes device identity, folder mapping metadata, manifests, and
heartbeats into the shared repository. A newly selected folder or file from one
PC is only advertised as pending until every peer confirms a same-path mapping
or chooses a per-PC override. Peers must not hydrate, patch, or create the
target path before that mapping is confirmed. After confirmation, peers fetch
only missing chunks and patch safe targets at chunk level.

Sync loop prevention must be metadata-led. A peer first checks whether required
chunks already exist locally, but it also records source version id, operation
id, origin device id, and applied remote-version metadata. Writes performed by
FluxVault during remote hydration must be suppressed or classified as sync
applications when local change detection observes them, so the peer does not
publish the same remote version as a new local change unless the local file
diverged afterward.

## Future release tracks

R2 introduces a WinFsp managed workspace for high-frequency large-file workloads.
R3 introduces Cloud Files API / ProjFS sync-root integration. These are separate
tracks because they change the namespace and write-path assumptions.
