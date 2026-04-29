# Requirements

## Product goal

FluxVault protects large Windows workstation files with frequent, versioned
backups while users continue working. The first cloud path is a local cloud sync
folder; direct cloud adapters arrive later.

## MVP requirements

- Windows 11 only.
- WPF dashboard plus tray experience.
- Per-machine Windows service for continuous protection.
- Watched folders with recursive policy, include/exclude patterns, size limits,
  and resource profiles.
- Near-real-time detection using file-system notifications, with USN journal
  catch-up as the durable source of truth.
- VSS-backed reads for open files, using a writer-aware requester path for the
  service fallback rather than shelling out to `vssadmin.exe`.
- Chunked deduplicated storage with BLAKE3 fingerprints.
- Configurable hot-file cadence with bounded forced snapshots.
- Non-interfering capture reads that do not take exclusive source-file locks.
- Adaptive compression with zstd default, lz4 hot-file mode, Brotli ratio mode,
  LZMA cold-archive mode, and off mode for already-compressed formats.
- Smart retention with dense recent versions and thinner older versions
  implemented with conservative MVP defaults: keep all versions for 24 hours,
  one per hour for 30 days, one per day for 180 days, and at least the latest
  20 versions per source file.
- Restore browser supporting alternate restore paths, destination cancellation,
  explicit overwrite confirmation, and safe failure status for IPC,
  locked-destination, and access-denied errors.
- Local logs and diagnostics export only.
- Tray activity pane and dashboard Activity view for pending, in-progress,
  blocked, failed, and completed capture events.
- Dashboard service status must remain constrained and must not overlap command
  buttons; full detail should be available through tooltips and diagnostics.
- Dashboard service availability must be explicit when the Windows service is
  stopped, not installed, inaccessible, or IPC is unavailable. The app should
  allow Start/Stop service attempts and report elevated-permission failures
  clearly.
- Packaged and unpackaged dashboard runs must be able to connect to the local
  service IPC pipe as the logged-in desktop user, without granting service
  control or ProgramData write access through that IPC permission.
- The runtime dashboard follows the selected operational cockpit direction:
  stable primary commands in the header, left navigation, first-tab startup,
  and footer health/status tiles.
- The tray activity pane must open fully inside the active monitor work area,
  including high-DPI and taskbar-edge scenarios.
- Helpful tooltips for every Options control, covering retention, capture
  cadence, compression, preview, apply, save, and close behaviours.
- USN health must show exact fallback reasons in the UI and diagnostics instead
  of only saying unavailable.
- Developer command-line harness for backing up, listing, inspecting, and
  restoring normal readable files without claiming VSS or USN consistency.
- File browser tab with three panes: drives/folders, files in the selected
  folder, and pending unsaved selection changes.
- Folder selection cycles through recursive selected, immediate files only, and
  not selected, shown through compact checkbox-style visual indicators.
- Manual child selections are retained when a parent recursive selection is
  removed, and parents show child-selection indicators.
- File browser panes share available space equally and provide horizontal and
  vertical scrolling for long paths and dense folders.
- File browser tree hover must not show a broad pane tooltip; selection help
  remains on the compact selection control. Mouse-wheel scrolling must work when
  hovered over the tree.
- File browser file and pending-change columns auto-fit on first render and use
  horizontal scrolling for long names rather than squeezing columns too narrow.
- Unsaved File browser changes must survive dashboard refresh, app activation,
  and Options dialog return until the user saves or explicitly discards them.
- The older watched-folder Browse workflow is removed once the File browser
  becomes the primary selection workflow.
- Selection changes must not affect the running service until the user saves.
- Scoped include/exclude regex rules attach to selected File browser folders or
  files. Recursive folder regex applies recursively, immediate folder regex
  applies only to direct files, and child regex rules are additive with inherited
  parent rules. Folders with local regex rules show a tree indicator.
- File browser context menus provide `Show in File Explorer` for folders and
  `Open in default app` plus `Show in File Explorer` for files.
- Health/status tiles are displayed in the footer so command buttons and header
  text remain stable.
- About branding shows Yagasoft copyright, logo, app description, GitHub project
  link, website link, and brief roadmap milestones without changing the FluxVault
  executable, taskbar, or tray icon.

## Planned V1 and sync requirements

- Developer service packaging should configure delayed automatic service start,
  restart recovery, and Windows Event Log source registration. Internal fatal
  service failures should be logged before the service exits for SCM recovery.
- VSS capture must coordinate writers through the requester API. FluxVault must
  report `AppConsistent` only when writer coordination succeeds and writer
  metadata covers the source path. Successful snapshots without matching writer
  coverage must be reported as `CrashConsistent`; writer/requester failure must
  fail the capture rather than commit a misleading version.
- Explorer file/folder entry points are registered per user from Options, not
  from the developer service scripts. Full-menu verbs are ordered `Add to
  FluxVault`, `Show FluxVault versions`, then `Remove from FluxVault`.
- Explorer Add saves an immediate/non-recursive folder selection or file
  selection immediately. Explorer Remove removes a direct rule or creates a
  scoped exclusion on the nearest inherited parent selection.
- `Show FluxVault versions` launches `FluxVault.App.exe --show-versions "%1"`;
  the legacy `--restore-path "%1"` remains accepted. Both treat the path as a
  restore/version UI hint only.
- A second app launch with `--show-versions`/`--restore-path`, `--add-path`, or
  `--remove-path` must forward the request to the already-running dashboard when
  possible, bring that dashboard forward, and exit without auto-restoring or
  overwriting data.
- Windows 11 compact context-menu registration requires app identity and an
  `IExplorerCommand` shell extension. Options remains the single user-facing
  control, registering the full menu and reporting compact-menu availability
  from package identity plus the shell-extension artefact.
- Restoring an older version preserves newer versions and records the restored
  version as the fork origin for the new restore event.
- Version history supports Git-like per-file lineage through FluxVault manifests
  and chunks, not through a normal `.git` runtime repository.
- Multi-PC sync must keep a newly selected folder or file pending on each peer
  until same-path or per-PC override mapping is confirmed.
- Peers must not create, hydrate, or patch an unconfirmed mapping target.
- Multi-PC sync must prevent recursive re-publication loops by recording source
  version, operation, origin device, and applied remote-version metadata. Chunk
  existence checks alone are not enough.

## Explicit non-goals for v1

- No client-side encryption.
- No direct cloud API upload.
- No support commitment for Windows 10, Windows Server, network shares, or NAS.
- No FluxVault-owned kernel driver.
- No hidden telemetry.
- The command-line harness does not provide open-file consistency; it backs up
  normal file streams only.

## MVP retention behaviour

Retention is enabled by default and runs after successful service backups. The
WPF Options dialog can change retention values, preview the number of prunable
versions and estimated reclaimable bytes, and run retention immediately. This
slice does not enforce a hard repository-size quota; repository size is
reported for visibility only.

## MVP cadence behaviour

Continuous edits must not defer snapshots indefinitely. Directory notification
bursts are coalesced by path and profile, but FluxVault forces a snapshot after
the configured maximum hot-file delay. Defaults are Fast 30 seconds, Balanced 2
minutes, and Quiet 10 minutes. Periodic reconciliation still runs every 10
minutes by default as a safety net.

## Consistency language

FluxVault detects changed files and then identifies changed content efficiently.
It must not claim generic changed-byte detection for arbitrary Windows files.
VSS captures are app-consistent only where relevant VSS writers participate and
the capture result proves that status. Normal readable-file captures remain
`BestEffort`; writer-aware VSS captures are `AppConsistent` only with matching
writer coverage and are otherwise `CrashConsistent`.
