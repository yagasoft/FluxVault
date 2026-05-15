# Requirements

## Product goal

FluxVault protects large Windows workstation files with frequent, versioned
backups while users continue working. The first cloud path is a local cloud sync
folder; a disabled-by-default direct cloud adapter foundation now covers Azure
Blob, S3-compatible storage, Dropbox, Google Drive, and OneDrive through
credential-reference-only configuration and fake-client-tested contracts.

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
- File browser restore supports latest-file/folder restore elsewhere by
  default, latest restore to original with overwrite confirmation, and filtered
  version browsing for the selected file or folder.
- File browser refresh reloads roots, expanded folders, selected-folder files,
  and selection indicators while preserving the current selection; save,
  discard, backup, restore, Explorer mutations, profile switches, and Options
  close refresh the browser automatically.
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
- The service accepts concurrent named-pipe clients so status/activity requests
  remain responsive while long backup or restore operations run.
- Dashboard automatic refresh uses a lightweight status path that avoids full
  repository inventory enumeration. Full inventory refresh remains available for
  explicit user actions and screens that need tracked-entry detail.
- Whole-PC scale metadata must use PostgreSQL as the primary metadata store on
  each PC. Chunk payloads remain content-addressed files or object-store
  artefacts outside the database. Options must expose meaningful metadata-store
  and backup settings, and normal dashboard/version queries must move toward
  indexed database reads rather than manifest-directory scans.
- Backup Now and service-triggered scans must avoid rereading source content
  when repository metadata proves the file length and source last-write time
  are unchanged from the latest captured version. Periodic deep verification
  remains configurable so stale metadata is not trusted forever.
- A single service process may host multiple enabled vault profiles. Each
  profile has its own id, display name, repository, mirrors, watched folders,
  runtime state, watcher loop, and maintenance state. Old single-profile
  configuration loads as the enabled `Default` profile.
- The runtime dashboard follows the selected operational cockpit direction:
  stable primary commands in the header, left navigation, first-tab startup,
  and footer health/status tiles.
- The tray activity pane must open fully inside the active monitor work area,
  including high-DPI and taskbar-edge scenarios.
- Helpful tooltips for every Options control, covering retention, repository
  maintenance, default workload preset, capture cadence, compression, Explorer
  integration, preview, apply, save, and close behaviours.
- Operationally meaningful configurable behaviour should be represented by a
  typed, defaulted configuration model and exposed in Options when it has clear
  user semantics. Dormant or internal fields should not be exposed before they
  have a real runtime effect.
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
- Saving removed File browser protected selections must show one destructive
  confirmation before permanently deleting matching FluxVault repository and
  mirror history. On save, the service must immediately stop monitoring those
  scopes, clear pending watcher/capture state for them, cancel only matching
  active captures, skip no-longer-protected targets still being enumerated, and
  leave unrelated protected paths running.
- Scoped include/exclude regex rules attach to selected File browser folders or
  files. Recursive folder regex applies recursively, immediate folder regex
  applies only to direct files, and child regex rules are additive with inherited
  parent rules. Folders with local regex rules show a tree indicator.
- Selected File browser folders/files expose a workload preset. The preset is
  an unsaved selection edit until Save, and it controls conservative cadence,
  compression, compression thresholds, and common generated/cache exclusions.
- File browser context menus provide `Show in File Explorer` for folders and
  `Open in default app` plus `Show in File Explorer` for files.
- Health/status tiles are displayed in the footer so command buttons and header
  text remain stable.
- About branding shows Yagasoft copyright, logo, app description, GitHub project
  link, website link, and brief roadmap milestones without changing the FluxVault
  executable, taskbar, or tray icon.

## Planned V1 and sync requirements

- Developer service packaging and production MSI packaging should configure
  delayed automatic service start, restart recovery, and Windows Event Log
  source registration. Internal fatal service failures should be logged before
  the service exits for SCM recovery.
- The default consumer release should use one unsigned WiX Burn bootstrapper
  wrapping a per-machine WiX MSI. The MSI must install app, service, and CLI
  artefacts under Program Files, preserve `C:\ProgramData\FluxVault` across
  upgrade/uninstall, configure service recovery and the Event Log source, and
  define stable major-upgrade metadata.
- Release package generation must emit branded setup, checksum, release-notes,
  and status files without requiring signing secrets. Release notes and status
  must warn that Windows can show Unknown publisher and SmartScreen prompts for
  the unsigned installer.
- VSS capture must coordinate writers through the requester API. FluxVault must
  report `AppConsistent` only when writer coordination succeeds and writer
  metadata covers the source path. Successful snapshots without matching writer
  coverage must be reported as `CrashConsistent`; writer/requester failure must
  fail the capture rather than commit a misleading version. Completion evidence
  must be robust and CI-friendly, using deterministic requester/metadata tests
  rather than mandatory manual elevated live-writer smoke checks.
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
  from package identity plus the shell-extension artefact. The unsigned
  consumer release profile does not install that package identity, so compact
  menu support is unavailable there while the full menu remains supported.
- Explorer registration status treats FluxVault-owned full-menu verbs as valid
  only when all owned verbs point to the current executable and expected
  argument. Register repairs stale owned verbs, unregister removes known
  FluxVault-owned verbs, and compact-menu availability validates that the app
  executable target exists.
- Restoring an older version preserves newer versions. Restore writes the bytes
  and a repository-local pending lineage hint; the next capture of the
  destination records the restored-from and fork-origin version ids.
- Version history supports Git-like per-file lineage through FluxVault manifests
  and chunks, not through a normal `.git` runtime repository.
- When a protected file is copied from existing FluxVault content and remains
  byte-identical, FluxVault should create a visible inherited version that
  references the original history and reuses chunks rather than uploading the
  content again. A later edit to that copy starts a normal path-specific version
  chain with lineage back to the inherited source.
- Repository health must be visible in Diagnostics, including repository
  integrity, mirror state, USN state, blocked files, last scrub, and last restore
  rehearsal. Manual scrub and restore rehearsal actions must be available.
- Repository scrub must validate only artefacts referenced by remaining
  manifests. It may automatically repair repository or mirror artefacts only from
  a healthy counterpart; it must never repair from live source files.
- Restore rehearsal must restore recent versions only into FluxVault-owned
  temporary output, verify logical length, record pass/fail details, delete
  temporary files, and avoid creating restore hints or new versions.
- Scheduled maintenance defaults to enabled, runs every 24 hours, repairs from a
  mirror when possible, and rehearses the newest three versions.
- Options must allow users to change scheduled maintenance enablement, interval,
  automatic mirror repair, and restore rehearsal version count.
- Options must allow users to edit the active compression skip-extension list,
  with normalisation on save.
- Options must allow users to choose the default workload preset for new File
  browser and Explorer Add selections.
- Built-in workload presets must cover general purpose, Office documents,
  CAD/BIM, Adobe/video, developer workspaces, and generic large files. The
  conservative catalogue should skip generated/cache folders where safe, store
  already-compressed formats without extra compression, and keep global
  skip-extension entries as the final no-compression guardrail.
- Mirror configuration must use a typed `MirrorSet` with mirror node id, label,
  path, enabled state, placement policy, optional capacity budget, and node
  priority. Older `mirrorPath` configuration must remain loadable and migrate
  into one enabled full-copy mirror node.
- The dashboard must provide a dedicated Mirrors workspace for editing mirror
  nodes. The Protection workspace should show a summary and navigation to
  Mirrors instead of an editable mirror-path textbox.
- Repository commits must write the primary repository first, then mirror new
  artefacts according to the configured placement policy. `FullCopy` must write
  chunks and metadata to every enabled node. `CapacityBalanced` must place each
  new chunk/metadata pair on deterministic capacity/priority-selected targets.
  `Redundant` must write the configured minimum mirror copy count, capped by
  enabled node availability and reported when under-satisfied. Manifests must
  continue to be mirrored to enabled nodes. Mirror write failures must be
  reported as node-specific warnings and must not fail an otherwise successful
  primary backup.
- Mirror repair must provide explicit preview and run actions. Preview must not
  write artefacts. Repair-all may repair primary artefacts from any healthy
  enabled mirror and repair enabled mirrors from the healthy primary.
  Selected-node repair is mirror-only and must not repair the primary.
- Mirror placement preview must report missing required copies, extra
  non-target copies, offline nodes, planned copy/delete actions, estimated
  movement, and per-node status without writing files. The latest preview must
  be visible through repository health/status.
- Mirror placement apply must copy missing required chunk/metadata artefacts
  from the healthy primary repository to target mirrors before deleting extra
  non-target mirror artefacts. It must not delete extra mirror copies for a
  chunk while any required target copy or unresolved node issue remains.
- Mirror drain/remove must be safely distinct from ordinary configuration
  removal. Selected-node drain preview must not write files. Selected-node
  drain run must copy required chunk/metadata artefacts from the healthy
  primary repository to remaining target mirrors first, then delete the
  selected mirror's chunk/metadata artefacts only when those required copies
  are satisfied and no unresolved node issue remains. A healthy drain must
  disable the selected mirror node in configuration.
- Multi-PC sync must start from a stable local device identity and typed
  trusted-device records. Existing configurations without sync identity must
  load with one local trusted-device record, and service status must expose the
  local device id, display name, and trust summary for Diagnostics.
- Multi-PC sync metadata must use repository-owned peer heads, immutable
  per-peer operation records, and local per-peer cursors. Operation records
  must be append-only, reference versions and metadata instead of duplicating
  chunk payloads, and expose peer-head/cursor status through Diagnostics.
- Multi-PC sync mapping records must keep a newly selected folder or file
  pending on each peer until same-path or per-PC override mapping is confirmed.
- Peers must not create, hydrate, or patch an unconfirmed mapping target. The
  sync mapping gate must allow hydration only for confirmed mappings with a
  local target path, and Diagnostics must expose pending mapping count.
- Multi-PC sync must prevent recursive re-publication loops by recording source
  version, operation, origin device, and applied remote-version metadata. Chunk
  existence checks alone are not enough. Remote-applied manifests must carry
  sync-origin metadata, repository sync metadata must record applied remote
  versions, and a publish gate must suppress remote-applied versions from
  being advertised as fresh local captures.
- Multi-PC sync hydration must copy missing chunk and metadata artefacts from a
  peer repository before applying a remote version locally.
- Sync hydration must not overwrite locked or unavailable targets. Such targets
  must be reported as blocked and kept unchanged.
- Sync conflict handling must preserve both sides. If the target has local
  content that differs from the incoming remote version, FluxVault must keep the
  local target unchanged and record conflict actions for keeping local, keeping
  remote, restoring remote as a copy, or marking the conflict resolved.
- R4 WinFsp performance-workspace configuration must be typed, defaulted, and
  disabled by default. It must record workspace path, cache budget, and mount
  name without assuming the driver is installed.
- R4 status must expose whether WinFsp workspace setup is prepared and must
  clearly report that driver checks and OS-level registration are deferred until
  explicitly approved.
- R4 setup scripts and manifests may be authored in the repository, but tests
  and normal app flows must not install drivers, register mounts, or mutate
  machine-wide state.
- R5 Cloud Files API / ProjFS shell-integration configuration must be typed,
  defaulted, and disabled by default. It must record sync-root path, display
  name, hydration policy, and placeholder state path without assuming provider
  registration exists.
- R5 status must expose whether shell integration is prepared and must clearly
  report that sync-root registration, provider registration, and placeholder
  creation are deferred until explicitly approved.
- R5 setup scripts and manifests may be authored in the repository, but tests
  and normal app flows must not register sync roots, register providers, create
  placeholders, or mutate machine-wide state.
- R6 direct cloud adapter configuration must be typed, defaulted, and disabled
  by default. It must cover Azure Blob, S3-compatible storage, Dropbox, Google
  Drive, and OneDrive using credential references rather than secret values.
- R6 adapter contracts must be testable with fake clients and must not require
  credentials or live provider calls in tests.
- R6 status must expose configured adapter count, SDK/provider readiness, and a
  clear live-validation-deferred state until credentials and account access are
  explicitly provided.
- Later security posture configuration must be typed, defaulted, and disabled
  by default. It must store encryption key references only, reject obvious
  inline key material, expose client-side encryption readiness through
  status/Diagnostics, and keep repository artefacts plain until an explicit
  encryption execution slice exists.
- Later enterprise/fleet configuration must be typed, defaulted, and disabled
  by default. It must store local policy source, assignments, and local status
  records without contacting a remote management plane. Status/Diagnostics must
  make remote fleet management deferral explicit.

## Explicit non-goals for v1

- No client-side encryption execution in v1; the LATER foundation stores only
  references and status contracts.
- No live direct cloud account upload or credential validation in the unsigned
  consumer foundation.
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
minutes by default as a safety net. USN reset or full-scan-required fallbacks
are cooled down after a fallback scan so a noisy or unhealthy volume cannot
drive repeated idle full scans. The cooldown defaults to 30 minutes and is
editable from Advanced Options.

## Consistency language

FluxVault detects changed files and then identifies changed content efficiently.
It must not claim generic changed-byte detection for arbitrary Windows files.
VSS captures are app-consistent only where relevant VSS writers participate and
the capture result proves that status. Normal readable-file captures remain
`BestEffort`; writer-aware VSS captures are `AppConsistent` only with matching
writer coverage and are otherwise `CrashConsistent`.
