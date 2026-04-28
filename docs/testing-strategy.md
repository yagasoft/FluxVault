# Testing strategy

## Unit tests

- Policy resolution and inheritance.
- Chunk boundary validity and deterministic chunking.
- BLAKE3 fingerprinting.
- zstd, lz4, Brotli, and LZMA round-trip compression.
- Adaptive codec-policy selection.
- Capture cadence defaults and forced hot-file snapshot decisions.
- Manifest write/read.
- Deduplicated commit and restore.
- Retention decisions.
- Configuration load/save/defaults and validation.
- IPC request/response serialisation.
- Durable-change detail serialisation for USN fallback diagnostics.
- Normal-file capture, non-interfering source sharing, writer-aware VSS
  fallback selection, and capture consistency evidence.
- Fake-coordinator VSS requester tests for app-consistent writer coverage,
  crash-consistent no-writer-coverage snapshots, and writer/requester failure
  cleanup.
- Resource profile debounce delays.
- XAML quality checks for compact Activity pane controls, the selected
  operational cockpit shell, prioritised command order, constrained dashboard
  header status text, service availability warning and Start/Stop controls,
  first-tab startup, Options Explorer menu actions, wrapping USN tooltips, and
  wrapping Options tooltips.
- Restore workflow view-model tests for destination cancellation, overwrite
  confirmation, overwrite denial, IPC failure, locked/access-denied failure
  messaging, and preserving the selected version.
- Startup request routing tests for `--restore-path` parsing and forwarding a
  second launch to the primary dashboard instance.
- About window and branding checks for Yagasoft logo packaging, GitHub/website
  links, roadmap summary, external-link failure handling, and preserving the
  FluxVault application icon.
- File browser XAML checks for native tree/grid scrolling, no broad tree
  tooltip, named auto-fit grids, and first-render column sizing hooks.
- USN diagnostic formatting for volume-open, unsupported-volume, journal-read,
  and file-id path-resolution failures.
- Windows interop binding checks for native USN calls such as `DeviceIoControl`.
- Tray pane placement for taskbar edges, high-DPI conversion, and small working
  areas.
- File browser selection model: tri-state folder toggling, immediate files
  only, recursive selection, retained manual child selections, parent
  child-selection indicators, checkbox-style visual states, pending-change
  summaries, discard/reload, and Save-only application.
- Dirty File browser and configuration edits are preserved across automatic
  refresh, manual refresh, app activation, and Options return.
- Exclusion rules: config round-trip, regex validation, case-insensitive file
  and folder matching, and disabled-rule handling.
- Planned restore lineage: restored versions preserve newer versions and record
  the restored-from and fork-origin version ids.

## Integration tests

- Generated large files.
- Open and locked file reads.
- Rapid edits and service restart catch-up.
- VSS unavailable or failed.
- Atomic cloud-folder mirror writes.
- Restore to original and alternate paths.
- CLI backup/list/inspect/restore using real temporary files.
- Service operation backup/list/restore using real temporary files.
- Restore IPC failure for locked or access-denied destination paths.
- Service operation persistence of `AppConsistent` capture results and optional
  consistency detail in status/activity.
- Service-triggered retention and manual `RunRetentionNow`.
- Mirror cleanup after retention pruning.
- Options dialog view-model save, preview, and run-retention behaviour with a
  fake service client.
- Activity pane view-model loading for recent events and blocked files.
- Diagnostics export containing durable-change fallback details.
- CodeQL workflow trigger guard: no `pull_request` trigger.
- Missing watched-folder failure reporting.
- Watcher-driven due changes trigger USN catch-up first, then fall back to the
  watcher path only when USN reports no changed files.
- USN unavailable/full-scan-required watcher cycles run one full scan without
  duplicating the same targeted watcher capture.
- Service worker fatal task failures are logged as critical and rethrown for
  Windows Service recovery; recoverable USN exceptions are logged as warnings
  before reconciliation fallback.
- File browser flow: unsaved selection edits do not change service
  configuration until Save.
- File browser compiled selections: recursive folders include nested files,
  immediate-only folders exclude nested files, and selected-file rules capture
  only the selected files.
- Protection exclusions: recursive scans skip excluded folders, immediate
  selections skip excluded files, selected-file rules are filtered, and targeted
  backup paths from watcher/USN are ignored when excluded.
- Planned sync mapping gate: remote peers do not hydrate, patch, or create a
  newly selected target until mapping is confirmed.
- Planned sync loop prevention: a remotely hydrated version is not republished
  as a new local version when watcher/USN observes FluxVault's own sync-applied
  write.

## Benchmarks

Benchmark 1 GB, 10 GB, and 100 GB patterns:

- unchanged re-scan
- small middle edit
- append-only edit
- random sparse edits
- continuously hot file

## Packaging tests

- Clean install.
- Upgrade.
- Uninstall.
- Service recovery, delayed automatic start, Event Log source registration, and
  explicit ProgramData/Event Log cleanup switches.
- Tray/dashboard service connection.
- Tray activity pane placement inside the active monitor working area.
- Explorer integration registration from Options, with packaging guards that
  keep context-menu registration out of install/uninstall scripts.
- Restore smoke test from installed build.
- Developer CLI smoke test: back up a file, list versions, inspect the version,
  restore it, and compare hashes.
- Developer service package artefacts: install script, uninstall script,
  quickstart, and sample configuration.
- Branding artefacts: FluxVault icon remains the application icon, and the
  Yagasoft logo is copied as an app content asset.
- Manual elevated VSS smoke check: with the developer service installed, lock a
  protected file, run a backup, and confirm the Activity/Capture detail reports
  either writer-covered `AppConsistent` or no-writer-coverage
  `CrashConsistent`. This is documented evidence, not an automated PR gate.
