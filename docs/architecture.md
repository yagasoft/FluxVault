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
configured local repository, with optional atomic mirroring into enabled
`MirrorSet` nodes. On Windows, the service creates the IPC pipe with
an explicit ACL: LocalSystem and Administrators retain full control, while
Authenticated Users and packaged app tokens receive read/write pipe access.

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
an `IExplorerCommand` shell extension. FluxVault keeps the Options surface as
the control point: unpackaged runs and unsigned consumer installs register the
full menu and report compact registration as unavailable, while packaged runs
can report compact registration as active when the sparse package identity and
`FluxVault.ExplorerCommand.dll` artefact are present. The shell extension only
forwards selected paths to the same app startup arguments; it does not perform
backup, remove, or restore work.

When installed by the developer script or production MSI, `FluxVaultService`
uses delayed automatic start, SCM restart recovery, and the `FluxVaultService`
Windows Event Log source in the Application log. Service startup and shutdown
are information events, recoverable runtime fallbacks are warnings, and fatal
background task failures are logged as critical before being rethrown so SCM
recovery can restart the service.

Production packaging is split deliberately. The unsigned consumer WiX MSI owns
machine-level state: app, service, CLI, service install/recovery, Event Log
source, and permanent ProgramData preservation. The unsigned consumer WiX Burn
bundle wraps only that MSI and does not install sparse MSIX package identity.
Options remains the user-facing Explorer integration control after install: the
classic full Explorer menu works through HKCU registration, while the Windows 11
compact menu remains available only through a future signed/package-identity
distribution path.

Repository maintenance runs inside the service next to IPC and the protection
loop. `RepositoryMaintenancePolicy` is part of configuration and defaults to
enabled, a 24-hour interval, automatic repair from a mirror, and restore
rehearsal of the newest three versions. The maintenance loop persists the last
health, scrub, and rehearsal results under ProgramData service state so the
dashboard keeps health evidence after a service restart. Manual IPC commands can
return the current health snapshot, run a scrub, or run a restore rehearsal
without changing existing backup/restore contracts. The Options dialog exposes
scheduled maintenance enablement, interval, mirror repair, and restore
rehearsal count so these active service behaviours are configurable without
editing `config.json` by hand.

Repository scrub is repository-owned only. `FileSystemChunkRepository` walks
remaining manifests, validates referenced chunks, and ignores artefacts already
made unreachable by retention. A healthy mirror copy may repair a primary
manifest/chunk, and a healthy primary copy may repair mirror drift. If neither
side is healthy, FluxVault reports a critical unresolved issue and does not read
or trust the live source file as a repair source. Restore rehearsal restores
recent versions to a FluxVault temporary state folder, verifies logical length,
records pass/fail details, and deletes the temporary output without writing
restore-lineage hints.

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
   writer/requester failure fails the capture. The native COM calls sit behind
   an internal VSS backup-session adapter so CI can verify requester sequencing,
   metadata interpretation, writer-status failure handling, and cleanup without
   elevated live writers.
7. Core chunking splits the captured bytes into FastCDC-style chunks.
8. Chunk fingerprints are compared against the local repository.
9. New chunks and a manifest are committed atomically.
10. Repository artefacts are mirrored according to the active mirror placement
    profile after the primary commit succeeds. Manifests go to every enabled
    mirror node, while chunks and metadata follow full-copy, capacity-balanced,
    or redundant placement. Mirror failures are captured as warnings after the
    primary commit succeeds.
11. Compression is selected by policy. The default adaptive profile uses zstd,
    skips known compressed file types from the configurable skip-extension
    list, uses lz4 for hot files, and keeps Brotli and LZMA as explicit
    ratio/cold-archive choices.
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
width. Options now covers retention, scheduled maintenance, default workload
preset, capture cadence, active compression choices, compression skip
extensions, and Explorer integration. Future user-meaningful operational
settings should follow the same pattern: typed defaults, compatibility for existing config files,
Options load/save tests, and no exposed dormant fields. The selected dashboard
direction is Concept A, the operational cockpit: stable primary commands in the
header, left navigation for workspaces, first-tab startup, and health tiles in
the footer. The dashboard health strip keeps status compact but exposes
detailed durable-change information through the USN tooltip and diagnostics
export. The main header constrains long service text with ellipsis trimming so
it cannot overlap command buttons. The Capture tile is the live
watcher/debounce queue; the USN tile is the latest durable catch-up result.

The Diagnostics workspace is the detailed health dashboard. It shows repository
integrity, mirror state, restore rehearsal, USN state, and blocked-file rows, and
keeps manual Run scrub, Preview mirror repair, Run mirror repair, Preview mirror
placement, Apply mirror placement, Run restore rehearsal, and Export
diagnostics actions in one place.

The Mirrors workspace owns mirror configuration. The Protection workspace shows
a concise mirror summary and links into Mirrors; it no longer exposes the
legacy editable mirror-path textbox. Mirror nodes have id, label, path, enabled
state, optional capacity budget, and priority. The workspace saves the active
placement profile (`FullCopy`, `CapacityBalanced`, or `Redundant`) and minimum
mirror copy count. It also exposes a read-only placement preview and compact
per-node placement status, plus an Apply placement action that executes the
same copy/delete plan conservatively. Selected-node drain preview/run copies
required artefacts to remaining mirrors before deleting drained chunk/metadata
copies and disabling the node. The workspace also shows compact per-node mirror
repair status and allows selected-node preview/repair. Selected-node repair
only repairs that mirror from the healthy primary repository; primary repair
remains a repair-all operation.

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
individual file selections compile to parent-folder include patterns. Each
selection can carry a workload preset. Missing preset fields from older config
files default to the general-purpose preset, while Options controls the preset
used for new File browser and Explorer Add selections.

Scoped `ProtectionScopedRegexRule` lists complement each selected folder or
file. Recursive folder regex rules apply to descendants; immediate-folder rules
apply only to files directly inside that folder; child rules are additive with
inherited parent rules. Include rules are treated as an additive narrowing set,
and any matching exclude rule suppresses the file before capture. Folders with
local scoped regex rules show an `R` indicator in the tree. Legacy global
`ProtectionExclusionRule` records are still understood by the service for
backward compatibility, but the Options global regex editor has been removed.

## Workload policy presets

`WorkloadPolicyConfiguration` stores the default preset for newly protected
items. Built-in preset metadata lives in the core policy catalogue and is
resolved at capture time with the selected rule nearest to the file path. This
keeps parent recursive selections and child selections additive: a child folder
or file can use a different preset without changing its parent.

The conservative catalogue covers general purpose, Office documents, CAD/BIM,
Adobe/video, developer workspaces, and generic large files. Presets resolve to
a resource profile, compression preference, minimum compression threshold,
folder exclusions, extension exclusions, no-compression extensions, and
extension-specific overrides. Generated or cache folders such as developer
dependency trees and creative media caches are skipped before capture when they
belong to the selected root. Global compression skip extensions remain the last
no-compression guardrail.

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

Restore remains append-only in the V1 lineage model. Restoring an older version
writes bytes plus a repository-local pending lineage hint; it does not create a
manifest until the next capture of that destination. That capture records the
restored source version and fork origin, while newer versions remain queryable
and restorable. Byte-identical copies into protected paths become visible
`InheritedCopy` versions that reuse chunks and point to the original history.
This gives FluxVault Git-like history semantics without using Git as the live
repository engine.

Retention is manifest-led. FluxVault groups versions by normalised source path,
keeps dense recent history, thins older history to hourly and daily buckets,
and always keeps at least the latest configured number of versions per source
file. After deleting pruned manifests locally, it garbage-collects only chunks
and metadata no remaining manifest references. Mirror cleanup is best-effort
and reported through status and diagnostics.

`MirrorSetConfiguration` is the active mirror configuration model. Older
`mirrorPath` configuration files are loaded for compatibility and normalised
into one enabled full-copy mirror node. During commit, chunks, metadata, and
manifests are written to the primary repository first. Manifests are then
mirrored to every enabled node so each mirror has version metadata. Chunks and
metadata are written through the pure `MirrorPlacementPlanner`: `FullCopy`
targets every enabled node, `CapacityBalanced` uses deterministic weighted
rendezvous selection against capacity budget and priority, and `Redundant`
targets the configured minimum copy count capped by eligible nodes. A failed
mirror write records a node-specific warning in the commit result and service
status, but it does not roll back or fail the primary backup.

Mirror repair reuses the repository validation boundary. Preview checks primary
and enabled mirror manifests, chunks, and chunk metadata without writing files.
Chunk and metadata checks are placement-aware, so non-target mirrors are not
reported as missing required copies. Repair-all repairs primary artefacts from
any healthy enabled mirror, then repairs enabled mirrors from the healthy
primary copy. Selected-node repair is mirror-only and leaves primary repair to
repair-all. Mirror repair reports are persisted in repository health state so
Diagnostics and Mirrors can display the latest per-node repair evidence after
refresh or service restart.

Mirror placement preview is read-only. It compares referenced chunk/metadata
artefacts against the active placement plan, reports missing required copies,
extra non-target copies, offline nodes, planned copy/delete actions, estimated
bytes, and per-node health, then persists the latest preview in repository
health state. Apply placement copies missing required artefacts from the healthy
primary repository using the same atomic write pattern, recomputes placement,
then deletes extra non-target mirror artefacts only for chunks whose required
target copies are satisfied and have no unresolved node issue.

Mirror drain is selected-node maintenance, not the same operation as deleting a
row from mirror configuration. Drain preview plans the copy and delete work
needed to take one enabled mirror out of chunk/metadata placement. Drain
execution copies required artefacts from the healthy primary repository to the
remaining target mirrors first, recomputes state, and deletes the selected
mirror's chunk/metadata artefacts only for chunks with no unresolved required
target issue. On a healthy completion the selected mirror node is disabled in
configuration. Manifests continue to follow normal manifest mirroring; drain
does not act as a destructive repository purge.

## Planned multi-PC sync safety

R3 sync starts with a stable local device identity and trusted-device records
stored in typed configuration. Legacy configurations normalise to one local
trusted record so later peer metadata can reference a stable device id before
operation logs, mapping gates, or hydration exist. Diagnostics shows the local
device id and trusted-device count for support visibility.

R3 sync metadata is repository-owned. Each peer owns a `sync/peers/<device>/`
area with immutable operation records and a compact `head.json`; local cursors
under `sync/cursors/` record how far this device has processed each peer. Peer
operation records reference FluxVault versions, source paths, content
signatures, operation ids, and optional metadata; they do not duplicate chunk
payloads.

R3 sync mapping metadata is also repository-owned under `sync/mappings/`. A
newly selected folder or file from one PC is stored as a pending mapping until
this peer confirms a same-path mapping or chooses a per-PC override. The sync
mapping gate allows hydration only for confirmed mappings with a local target
path, so later sync hydration cannot create, patch, or overwrite an
unconfirmed target. After confirmation, peers fetch only missing chunks and
patch safe targets at chunk level.

Sync loop prevention is metadata-led. Remote-applied versions carry sync-origin
metadata in the manifest: source device id, source operation id, source version
id, optional mapping id, and applied timestamp. Repository sync metadata also
stores applied remote-version records under `sync/applied/`, keyed by source
device and operation. A publish gate suppresses these remote-applied manifests
from being advertised as fresh local captures. Later chunk hydration must use
that metadata together with chunk existence checks so the peer does not publish
the same remote version as a new local change unless the local file diverged
afterward.

## Future release tracks

R2 is the distributed mirror fabric. The first slice establishes `MirrorSet`
full-copy nodes and warning semantics. The repair foundation adds explicit
preview/run repair and per-node mirror health. The placement foundation adds
real full-copy, capacity-balanced, and redundant placement for new commits plus
read-only movement preview, safe rebalance execution for existing artefacts,
and selected-node drain/remove execution. R3 is multi-PC sync over
repository-owned peer metadata. R4 introduces a WinFsp managed workspace for
high-frequency large-file workloads. R5 introduces Cloud Files API / ProjFS
sync-root integration. R4 and R5 are separate tracks because they change the
namespace and write-path assumptions.
