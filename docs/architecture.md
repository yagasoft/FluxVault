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
profile management, and diagnostics export. Configuration is stored in
`C:\ProgramData\FluxVault\config.json`. New files use a profile-set shape with
one `FluxVaultConfiguration` per vault profile; legacy single-profile JSON is
loaded as the enabled `Default` profile. A single service process owns one
operations/protection/maintenance runtime per enabled profile, keeping each
profile's repository, mirrors, watcher state, USN checkpoint state, and
maintenance evidence isolated. IPC requests may specify `ProfileId`; old clients
without a profile id target the active/default profile. Repository artefacts are
written to the selected profile's configured local repository, with optional
atomic mirroring into its enabled `MirrorSet` nodes. On Windows, the service
creates the IPC pipe with an explicit ACL: LocalSystem and Administrators retain
full control, while Authenticated Users and packaged app tokens receive
read/write pipe access. The pipe server accepts multiple clients concurrently;
long mutating operations are coordinated inside the operations layer so status
and activity requests can still complete while Backup Now is running. Status IPC
supports a fast detail level used by dashboard auto-refresh. Fast status returns
cached runtime summaries, activity, recent versions, and live backup/watcher
diagnostics without rebuilding the full tracked repository inventory every few
seconds. Manual refreshes and views that need repository file-browser detail
request full status.

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
Classic HKCU verb status is considered current only when every FluxVault-owned
file and directory verb points to the running `FluxVault.App.exe` path with the
expected argument. Options registration overwrites stale FluxVault-owned verbs,
unregistration removes the known FluxVault-owned keys, and compact-menu status
also validates that the resolved app executable still exists.

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
7. Before reading source bytes, FluxVault compares the target's length and
   source last-write time with the latest repository summary for that path.
   Matching entries are reported as skipped unchanged. Periodic deep
   verification can force a later reread so source metadata is not trusted
   indefinitely.
8. Core chunking splits captured bytes into FastCDC-style chunks.
9. Chunk fingerprints are compared against the local repository.
10. New chunks are written to the content-addressed repository, then version,
    lineage, folder, chunk-reference, current-entry, and outbox metadata are
    committed to PostgreSQL. The exact manifest payload is also stored as
    `manifest_json` so restore and inspect can reconstruct the captured version
    without reading a JSON manifest file.
11. Repository artefacts are mirrored according to the active mirror placement
    profile after the primary commit succeeds. Chunks and chunk metadata follow
    full-copy, capacity-balanced, or redundant placement. Runtime version
    metadata is exported through the DB-backed metadata journal rather than
    mirrored as per-version JSON manifests.
12. Compression is selected by policy. The default adaptive profile uses zstd,
    skips known compressed file types from the configurable skip-extension
    list, uses lz4 for hot files, and keeps Brotli and LZMA as explicit
    ratio/cold-archive choices.
13. If retention is enabled and the backup produced captured versions or
    deletion tombstones, the service applies the configured retention policy
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
- Maximum concurrent captures: 2.
- Watcher event backlog limit: 4096 unique pending paths.
- USN fallback full-scan cooldown: 30 minutes.
- Source deep verification interval: 7 days.

Directory notifications are latency hints. They trigger USN catch-up, collapse
repeated per-path events, and are capped by the watcher event backlog limit. If
the backlog limit is exceeded, FluxVault drops the per-path queue and schedules
one reconciliation scan instead of hot-looping on every watcher event. USN is
the durable catch-up source where available. Reconciliation remains the safety
net when USN cannot prove continuity or watcher volume is excessive.
When USN reports reset, wrap, or another full-scan-required state repeatedly,
FluxVault performs one fallback scan, records the reason, suppresses duplicate
fallback scans until the configured cooldown expires, and keeps watcher-targeted
backup processing available for matured events. Backup runtime status exposes
the current phase, effective worker count, enumerated/captured/skipped/failed
counts, suppressed fallback count, last full-scan reason, and next allowed
fallback time.
Durable-change and watcher runtime status carry latest-check labels, timestamps,
event rate/backlog, last catch-up source (`USN`, `Watcher fallback`, or
`Reconciliation scan`), and structured diagnostic details, so the UI and
exported diagnostics can distinguish unable to open volume, unsupported volume,
journal ID change, journal wrap, checkpoint seeding, file-id path resolution
failures, and noisy-folder churn such as OneDrive/Office activity.

## Options and status UX

Every editable Options control has a native WPF tooltip describing the
operational effect of the setting; long tooltip text wraps inside a bounded
width. Options now covers retention, scheduled maintenance, default workload
preset, capture cadence, USN fallback cooldown, source deep verification,
active compression choices, compression skip extensions, and Explorer
integration. Future user-meaningful operational
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
state, optional capacity budget, and priority. Add/edit uses a dialog so
validation and explicit migration status are owned by the app rather than
inline table edits. The workspace saves the active placement profile
(`FullCopy`, `CapacityBalanced`, or `Redundant`) and minimum mirror copy count,
using English display labels in the dashboard. It also exposes a read-only
placement preview and compact per-node placement status, plus an Apply
placement action that executes the same copy/delete plan conservatively.
Selected-node drain preview/run copies required artefacts to remaining mirrors
before deleting drained chunk/metadata copies and disabling the node. The
workspace also shows compact per-node mirror repair status and allows
selected-node preview/repair. Selected-node repair only repairs that mirror
from the healthy primary repository; primary repair remains a repair-all
operation. Mirror grid column widths and order are saved as local UI state, not
service configuration.

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
The File browser refresh command reloads roots, expanded folder children,
selected-folder files, and selection indicators while preserving current
selection where possible. Save, discard, Explorer Add/Remove, backup
completion, restore completion, profile switch, and Options close trigger the
same refresh path.

Folder selection is tri-state: recursive selected, immediate files only, and
not selected. Manual child selections are retained below a parent so they can be
restored when a recursive parent selection is removed. Parent folders show a
compact child-selection indicator when descendants have manual selections. The
service reloads watched selection rules only after the user saves the browser
changes. While configuration or File browser edits are dirty, dashboard refresh
updates runtime status, versions, and activity without replacing local unsaved
selection rules or clearing the pending-changes pane.

When a save removes protected content selections, the app compares the saved
baseline with the pending rules and asks for destructive confirmation before
requesting a purge. Regex-only scope removals are ignored because they are
filters, not protected content. Confirmed removal scopes are sent over IPC with
the save request together with still-protected preserve scopes, so retained
child selections are not purged when a parent recursive selection is removed.
The service computes the same removal scopes for old clients that do not send
purge fields, but those clients only stop future monitoring; they do not purge
history unless `PurgeRemovedSelections` is explicitly set.

The File browser tree owns its own scrollbars so mouse-wheel scrolling works
when the cursor is over the folder tree. File checkboxes are interactive inside
the otherwise read-only grid, so individual files can be added or removed from
protection without inline table editing. File and pending-change grids auto-fit
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
Folder and file context menus expose latest restore actions. The default latest
restore writes elsewhere: file selections use a save-file picker, folder
selections use a folder picker and preserve relative paths under the chosen
destination. Latest restore to original requires overwrite confirmation when
conflicts exist. Show versions opens the version inventory filtered to the
selected file or folder.

Protected-selection removal is coordinated with the running service profile.
After saving the new configuration, `FluxVaultOperations` updates the current
protection snapshot, signals the profile's `ProtectionRuntimeCoordinator`,
clears matching runtime status rows, cancels matching active capture tokens,
and optionally calls repository purge. `FileSystemProtectionLoop` wakes from
the same coordinator instead of waiting for the next watcher poll, clears stale
pending watcher paths, rebuilds watchers, and avoids watcher fallback work for
removed scopes. Backup enumeration and targeted backup both re-check each
target against the latest protection snapshot before reading source bytes.

The File browser merges live filesystem entries with the repository's latest
tracked entries. Deleted or otherwise missing tracked files and folders appear
as translucent phantom rows. Phantom rows can be restored and can open version
history, but shell open/show-in-Explorer actions are disabled because no live
filesystem object exists. The browser also has an address bar; entering a live
or tracked path and pressing Enter or Go navigates to that folder/file, while
invalid or untracked paths leave the current selection unchanged and report a
status message.

Scoped `ProtectionScopedRegexRule` lists complement selected folders/files and
regex-only folder scopes. A regex-only scope can be saved on an unselected
folder; it does not compile into a watched folder and only filters protected
descendant selections. Recursive folder and regex-scope rules apply to
descendants; immediate-folder rules apply only to files directly inside that
folder; child rules are additive with inherited parent rules. Include rules are
treated as an additive narrowing set, and any matching exclude rule suppresses
the file before capture. Folders with local scoped regex rules show an `R`
indicator in the tree. Legacy global `ProtectionExclusionRule` records are still
understood by the service for backward compatibility, but the Options global
regex editor has been removed.

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

The repository stores immutable chunks, while PostgreSQL stores append-only
version metadata. A `FileVersionManifest` is still the canonical in-memory
description of one captured version, but normal runtime reconstructs it from
`versions.manifest_json` and indexed relational rows. Chunks are addressed by
BLAKE3 digest and may be stored raw or encoded with zstd, lz4, Brotli, or LZMA
according to policy.

Restore remains append-only in the V1 lineage model. Restoring an older version
writes bytes plus a repository-local pending lineage hint; it does not create a
manifest until the next capture of that destination. That capture records the
restored source version and fork origin, while newer versions remain queryable
and restorable. Byte-identical copies into protected paths become visible
`InheritedCopy` versions that reuse chunks and point to the original history.
This gives FluxVault Git-like history semantics without using Git as the live
repository engine.

Opening a file version for preview is separate from restore. The dashboard asks
the service to reconstruct the chosen version into FluxVault-owned preview
state, opens the returned temporary file with the user's default application,
and does not write a restore-lineage hint. Folder versions are previewed inside
FluxVault through the version history browser: selecting a folder version shows
the snapshot entries stored in metadata, double-clicking a folder entry
navigates into that snapshot folder, and double-clicking a file entry opens the
existing temporary file-preview flow before any restore.

Retention is metadata-led. FluxVault groups versions by normalised source path,
keeps dense recent history, thins older history to hourly and daily buckets,
and always keeps at least the latest configured number of versions per source
entry. Folder versions and deletion tombstones retain their referenced child
versions while they are kept. After deleting pruned DB version rows, FluxVault
garbage-collects only chunks and metadata no remaining DB chunk reference uses.
Mirror cleanup is best-effort and reported through status and diagnostics.

Confirmed protected-selection purge bypasses retention for the removed scopes.
`RepositoryPurgeRequest` supports exact file, immediate-folder, and recursive
folder scopes plus preserve scopes for retained child selections. The repository
deletes every matching file/folder/deletion manifest and expands the purge to
remaining manifests that reference purged versions, then deletes primary and
mirror chunks/metadata only when no remaining manifest or metadata row still
references the digest. Purge returns purged-version count, deleted-chunk count,
reclaimed bytes, and non-fatal mirror warnings.

`MirrorSetConfiguration` is the active mirror configuration model. Older
`mirrorPath` configuration files are loaded for compatibility and normalised
into one enabled full-copy mirror node. During commit, chunks and chunk metadata
are written to the primary repository first, while version metadata is committed
to PostgreSQL and exported to `metadata-journal/<device>/<sequence>.fvop`.
Chunks and metadata are written through the pure `MirrorPlacementPlanner`: `FullCopy`
targets every enabled node, `CapacityBalanced` uses deterministic weighted
rendezvous selection against capacity budget and priority, and `Redundant`
targets the configured minimum copy count capped by eligible nodes. A failed
mirror write records a node-specific warning in the commit result and service
status, but it does not roll back or fail the primary backup.

Mirror repair reuses the repository validation boundary. Preview checks
DB-referenced chunks and chunk metadata without writing files.
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
configuration. Version metadata remains in PostgreSQL and metadata-journal
exports; drain does not act as a destructive repository purge.

## Whole-PC metadata store

PostgreSQL is now the primary metadata runtime for the whole-PC storage track.
Each PC owns its own local PostgreSQL database and chunk repository; no runtime
design assumes a shared database between PCs. PostgreSQL owns structured
metadata for paths, versions, chunk references, lineage, folder entries, current
entry projections, mirror placement, sync records, conflicts, capture queue
state, and metadata export outbox rows. Chunk bytes remain in the
content-addressed repository so large-file streaming, dedupe, mirror repair,
and future object-store transport do not push payloads through the relational
database.

The runtime repository initializes the PostgreSQL schema, records new file and
folder versions to DB rows, reads list/status/inspect/restore/retention/scrub
paths from metadata, and stops writing per-version JSON manifests in normal
service/app commits. Existing file-manifest repositories remain loadable only
through explicit legacy/developer paths and can be imported into the relational
shape for developer and test vaults.

Database recovery uses `eng/backup-fluxvault-db.ps1` for `pg_dump -Fc` logical
backups and `eng/restore-fluxvault-db.ps1` for `pg_restore`-based reinstall
recovery. The backup bundle can include configuration and exported
`metadata-journal` files. The runtime exports outbox rows to immutable journal
files after DB commit and marks rows exported only after the atomic write
succeeds.

Developer/bootstrap setup uses `eng/setup-fluxvault-postgresql.ps1` to install
PostgreSQL through winget, create the local FluxVault metadata role/database,
scope loopback trust to that database/user, patch ProgramData profile
configuration, initialize the schema, and run an immediate smoke check.

## Multi-PC sync safety

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

R3 chunk hydration is local and repository-driven. The hydrator copies missing
chunk and metadata artefacts from a peer repository into the local repository,
reconstructs the remote version, and commits the local target as a `RemoteSync`
version with sync-origin metadata. Target writes are atomic. Locked or
unavailable targets produce blocked hydration records and are not overwritten.
If the target already has different local content, FluxVault records an open
sync conflict under `sync/conflicts/`, leaves the target unchanged, and exposes
keep-local, keep-remote, restore-as-copy, and mark-resolved actions. Conflict
resolution records the selected action without destructively rewriting the file
in this foundation slice.

R4 WinFsp performance workspace support starts as a prepared, non-mutating
foundation. `PerformanceWorkspaceConfiguration` records WinFsp mode, workspace
path, cache budget, and mount name while defaulting disabled for existing users.
Service status projects a `PerformanceWorkspaceRuntimeStatus` that tells the
dashboard whether the workspace is enabled and that WinFsp driver checks and
registration are deferred. The `eng/winfsp` scripts and manifest are static
review artefacts in this slice; they emit plans and are covered by tests, but
FluxVault does not install WinFsp, register a file system, or mount a workspace
without explicit future approval.

R5 Cloud Files API / ProjFS shell integration also starts as a prepared,
non-mutating foundation. `ShellIntegrationConfiguration` records Cloud Files API
or ProjFS mode, sync-root path, display name, hydration policy, and placeholder
state path while defaulting disabled for existing users. Service status projects
a `ShellIntegrationRuntimeStatus` that tells the dashboard whether shell
integration is enabled and that sync-root registration and placeholder creation
are deferred. The `eng/shell-integration` scripts and manifest are static
review artefacts in this slice; they emit plans and are covered by tests, but
FluxVault does not register a provider, register a sync root, create
placeholders, or mutate machine state without explicit future approval.

R6 direct cloud adapters start as a typed, credential-reference-only foundation.
`DirectCloudConfiguration` records whether direct cloud use is enabled, the
configured Azure Blob, S3-compatible, Dropbox, Google Drive, and OneDrive
adapter records, metered-network policy, and an optional bandwidth limit. The
core cloud boundary is `ICloudObjectAdapter`, which exposes object put, read,
metadata, list, and delete operations and is tested with fake clients. Provider
SDK descriptors in `DirectCloudAdapterCatalog` bind the implementation to the
official SDK package/client types at compile time, while service status
projects `DirectCloudRuntimeStatus` and explicitly reports that live validation
is deferred. FluxVault does not load credentials, contact cloud accounts, or
upload repository artefacts through direct adapters without explicit future
approval.

The LATER security and fleet foundations are contract-only runtime surfaces.
`SecurityPostureConfiguration` stores disabled-by-default client-side
encryption policy, metadata mode, and key references that point to external key
or credential locations without storing raw key material. `ContentEncryptionPlan`
describes whether encryption is disabled, missing a key reference, or ready for
a later execution path; it does not transform repository chunks. `EnterpriseFleetConfiguration`
stores disabled-by-default local policy source, assignments, and local status
records. `FleetPolicyDocument` and `FleetPolicyEvaluation` let tests and future
services reason about policy compliance locally. Service status projects both
surfaces into Diagnostics and explicitly reports encryption execution and
remote management as deferred.

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
