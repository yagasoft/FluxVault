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
- Mirror repair model tests for preview without writes, repair-all primary and
  mirror repairs, selected-node mirror-only repair, offline mirror reporting,
  manifest/chunk/metadata drift, and temporary-file cleanup.
- MirrorSet configuration tests for default empty nodes, legacy `mirrorPath`
  migration, multi-node save/load round-trip, placement policy defaults, minimum
  mirror copy count, capacity budget, priority, and validation.
- Mirror placement planner tests for full-copy targeting, capacity/priority
  selection, redundant copy counts, and under-satisfied redundancy.
- Mirror placement preview tests for missing required copies, extra non-target
  copies, offline nodes, estimated movement, read-only behaviour, and
  temporary-file cleanup.
- Mirror drain tests for selected-node preview, copy-before-delete execution,
  blocked deletes when remaining targets are unresolved, configuration disable
  after healthy drain, and temporary-file cleanup.
- Mirrors workspace view-model and XAML tests for editable node lists,
  repository/protection summary replacement, workspace navigation, per-node
  repair status/actions, placement controls/status, preview/apply action, and
  selected-node drain controls.
- Security/fleet foundation tests for disabled defaults, configuration
  round-trip, rejection of inline encryption key material, key-reference-only
  encryption planning, local fleet policy evaluation, IPC serialisation, and
  Diagnostics bindings.

## Integration tests

- Generated large files.
- Open and locked file reads.
- Rapid edits and service restart catch-up.
- VSS unavailable or failed.
- Atomic cloud-folder mirror writes, multi-node full-copy MirrorSet writes, and
  placement-aware capacity-balanced/redundant mirror writes.
- Mirror warning behaviour when a node is unavailable: the primary backup still
  succeeds, status records a node-specific warning, and no temporary files are
  left in healthy mirror nodes.
- Mirror repair IPC behaviour: preview/run return repair reports, selected-node
  repair only writes to the selected mirror, repair-all can repair the primary
  from any healthy mirror, and repository health status persists the latest
  mirror repair report.
- Mirror placement preview IPC behaviour: preview returns planned copy/delete
  actions, persists the latest preview in repository health, and leaves
  `RunMirrorRebalance` as an explicit manual execution action.
- Mirror rebalance execution behaviour: copy required artefacts from the
  healthy primary before deleting extra non-target mirror artefacts, preserve
  extra copies when a required target or primary artefact is unresolved, persist
  the latest run report, and leave no temporary files.
- Mirror drain IPC behaviour: preview/run return a drain report for the
  selected node, run disables the node only after healthy copy/delete
  completion, persists the latest drain report in repository health, and leaves
  no temporary files.
- Device identity and trust behaviour: default and legacy configurations
  normalise to a stable local device id, trusted-device records round-trip
  through configuration and IPC, service status exposes the identity, and
  Diagnostics shows compact local identity/trust status.
- Peer sync journal behaviour: appending operations writes immutable records,
  updates compact peer heads, rejects duplicate operation ids, filters
  operations from local cursors, exposes peer heads/cursors through IPC, and
  shows compact peer status in Diagnostics.
- Sync mapping gate behaviour: proposed mappings are pending by default,
  pending mappings block hydration, confirmed mappings store the local target
  path and allow hydration, mapping records round-trip through IPC/status, and
  Diagnostics shows pending mapping count.
- Sync loop-prevention behaviour: commits with sync-origin metadata are stored
  as remote-applied versions, summaries preserve source device/operation/version
  metadata, applied remote-version records deduplicate by source operation,
  service status round-trips the latest applied records, Diagnostics shows the
  applied remote-version count, and the publish gate suppresses remote-applied
  versions from local publication.
- Sync hydration and conflict behaviour: missing remote chunks and metadata are
  copied from the peer repository before hydration, successful hydrations commit
  a `RemoteSync` version, locked targets are recorded as blocked without
  overwriting, conflicting targets are preserved with open conflict metadata,
  conflict actions can mark a record resolved, IPC round-trips conflict
  resolution requests, and Diagnostics shows blocked/conflict counts.
- WinFsp performance-workspace foundation: configuration defaults disabled and
  round-trips workspace path/cache/mount fields, service status and IPC expose
  prepared/deferred driver state, Diagnostics shows the workspace status, and
  `eng/winfsp` setup assets are covered by static tests that prove they are not
  self-executing driver or machine registration scripts.
- Cloud Files API / ProjFS shell-integration foundation: configuration
  defaults disabled and round-trips sync-root path/display/hydration/state
  fields, service status and IPC expose prepared/deferred registration state,
  Diagnostics shows the shell status, dashboard saves preserve non-editable
  native workspace settings, and `eng/shell-integration` setup assets are
  covered by static tests that prove they are not self-executing sync-root,
  provider, placeholder, or machine registration scripts.
- Direct cloud adapter foundation: configuration defaults disabled and
  round-trips Azure Blob, S3-compatible, Dropbox, Google Drive, and OneDrive
  adapter records; SDK package/client bindings are compile-checked; fake-client
  object adapter tests cover put/read/metadata/list/delete without live account
  calls; service status and IPC expose provider readiness with live validation
  deferred; Diagnostics shows the direct-cloud status; and package tests prove
  no credential material is declared in project files.
- Security/fleet foundation: service status exposes client-side encryption
  readiness and local fleet-policy state with encryption execution and remote
  management explicitly deferred; dashboard saves preserve non-editable
  security/fleet configuration; and tests avoid key material, credentials,
  remote management calls, or repository encryption mutation.
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
