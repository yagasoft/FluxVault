# Testing strategy

## Unit tests

- Policy resolution and inheritance.
- Workload policy preset catalogue and resolver behaviour, including nearest
  selected rule precedence, conservative generated/cache exclusions, global
  skip-extension precedence, and minimum compression thresholds.
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
- IPC pipe security for LocalSystem, Administrators, desktop users, and
  packaged app tokens.
- Durable-change detail serialisation for USN fallback diagnostics.
- Normal-file capture, non-interfering source sharing, writer-aware VSS
  fallback selection, and capture consistency evidence.
- Fake-coordinator VSS requester tests for app-consistent writer coverage,
  crash-consistent no-writer-coverage snapshots, and writer/requester failure
  cleanup.
- CI-friendly VSS requester adapter tests for call order, writer metadata
  coverage rules, writer-status failure, snapshot cleanup, and failed-capture
  no-commit behaviour. These tests prove FluxVault's requester logic without
  requiring live elevated VSS writers in CI.
- Resource profile debounce delays.
- XAML quality checks for compact Activity pane controls, the selected
  operational cockpit shell, prioritised command order, constrained dashboard
  header status text, service availability warning and Start/Stop controls,
  first-tab startup, Options Explorer menu actions, File browser context menus,
  scoped regex controls, workload preset controls, repository maintenance
  Options controls, compression skip-extension editing, wrapping USN tooltips,
  and wrapping Options tooltips.
- Restore workflow view-model tests for destination cancellation, overwrite
  confirmation, overwrite denial, IPC failure, locked/access-denied failure
  messaging, and preserving the selected version.
- Startup request routing tests for `--restore-path`, `--show-versions`,
  `--add-path`, and `--remove-path` parsing and forwarding a second launch to
  the primary dashboard instance.
- About window and branding checks for Yagasoft logo packaging, GitHub/website
  links, roadmap summary, external-link failure handling, and preserving the
  FluxVault application icon.
- File browser XAML checks for native tree/grid scrolling, no broad tree
  tooltip, named auto-fit grids, first-render column sizing hooks, scoped regex
  indicators, and shell-launch context menus.
- USN diagnostic formatting for volume-open, unsupported-volume, journal-read,
  and file-id path-resolution failures.
- Windows interop binding checks for native USN calls such as `DeviceIoControl`.
- Tray pane placement for taskbar edges, high-DPI conversion, and small working
  areas.
- File browser selection model: tri-state folder toggling, immediate files
  only, recursive selection, retained manual child selections, parent
  child-selection indicators, checkbox-style visual states, scoped regex
  indicators, per-selection workload presets, Explorer Add/Remove selection
  mutations, shell-launch context commands, pending-change summaries,
  discard/reload, and Save-only application.
- Dirty File browser and configuration edits are preserved across automatic
  refresh, manual refresh, app activation, and Options return.
- Exclusion rules: config round-trip, legacy global regex validation,
  case-insensitive file and folder matching, disabled-rule handling, and scoped
  selection regex inheritance for recursive, immediate, and child selections.
- Restore and inherited-copy lineage: old manifests default to normal captures,
  same-path captures record parents, restore records a pending hint consumed by
  the next capture, byte-identical copies become visible inherited versions,
  and later edits retain fork-origin lineage.
- Repository maintenance model tests for default policy values, IPC
  serialisation of health/scrub/rehearsal results, healthy scrub, primary repair
  from mirror, mirror repair from primary, unresolved corruption, ignored
  retention-pruned artefacts, restore rehearsal length verification, failure
  reporting, and temporary-output cleanup.

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
- Service operations for manual repository scrub, manual restore rehearsal,
  health status updates, and scheduled maintenance due/not-due behaviour.
- Service-triggered retention and manual `RunRetentionNow`.
- Mirror cleanup after retention pruning.
- Options dialog view-model save, preview, run-retention, repository
  maintenance policy, default workload preset, and compression skip-extension
  behaviour with a fake service client.
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
  selections skip excluded files, selected-file rules are filtered, targeted
  backup paths from watcher/USN are ignored when excluded, and scoped selection
  regex include/exclude rules filter full and targeted backup candidates.
- Workload policy capture behaviour: generated/cache paths excluded by the
  selected preset are not captured, and committed versions use the resolved
  compression codec and minimum compression threshold.
- Planned sync mapping gate: remote peers do not hydrate, patch, or create a
  newly selected target until mapping is confirmed.
- Planned sync change discovery: peer head changes cause only missing immutable
  operation records to be fetched from that peer, local per-peer cursors prevent
  replay after restart, corrupt or missing operation records surface as peer
  lag or warnings rather than crashing sync, and operation-origin metadata stops
  a node from applying its own published operation as a new remote change.
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
- Production WiX packaging source checks for per-machine service install,
  delayed auto-start, SCM recovery, Event Log source registration, permanent
  ProgramData preservation, stable major-upgrade metadata, unsigned
  consumer-bundle MSI-only chaining, absence of `Add-AppxPackage`, no sparse
  MSIX dependency, recursive full-publish payload harvesting, and narrow WiX
  warning suppressions.
- Release package validation builds developer artefacts, MSI, unsigned Burn
  bundle, branded setup/checksum/notes/status files, and unsigned-release
  warnings without installing elevated components or requiring signing secrets.
- Elevated release install/uninstall smoke validation uses
  `eng\smoke-release-install.ps1 -UninstallAfter` on a clean validation
  machine/session to verify checksum, install, service startup, delayed
  automatic start, installed payloads, ProgramData creation and preservation,
  Event Log source registration, installed CLI backup/list/restore, and bundle
  uninstall. The harness refuses an existing service or ProgramData by default
  so clean-release evidence does not mutate an existing FluxVault machine
  state.
- Tray/dashboard service connection.
- Tray activity pane placement inside the active monitor working area.
- Explorer integration registration from Options, with packaging guards that
  keep context-menu registration out of install/uninstall scripts, assert the
  ordered Add/Show FluxVault versions/Remove full-menu verbs, verify the sparse
  package manifest, native `IExplorerCommand` scaffold, package-script output,
  Visual C++ prerequisite preflight, and compact-menu status detection
  separately.
- Restore smoke test from installed build.
- Developer CLI smoke test: back up a file, list versions, inspect the version,
  restore it, and compare hashes.
- Developer service package artefacts: install script, uninstall script,
  quickstart, and sample configuration.
- Release package artefacts: `Yagasoft-FluxVault-v1.0.1-win-x64-Setup.exe`,
  `Yagasoft-FluxVault-v1.0.1-win-x64-checksums-sha256.txt`,
  `Yagasoft-FluxVault-v1.0.1-win-x64-release-notes.md`, and
  `Yagasoft-FluxVault-v1.0.1-win-x64-release-status.txt`.
- Branding artefacts: FluxVault icon remains the application icon, and the
  Yagasoft logo is copied as an app content asset.
- Optional elevated VSS smoke check: with the developer service installed, lock
  a protected file, run a backup, and inspect Activity/Capture consistency
  detail. This is troubleshooting evidence only, not a V1 completion or CI gate.
