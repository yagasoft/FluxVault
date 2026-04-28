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
- Normal-file capture, non-interfering source sharing, and VSS fallback
  selection.
- Resource profile debounce delays.
- XAML quality checks for compact Activity pane controls, constrained dashboard
  header status text, wrapping USN tooltips, and wrapping Options tooltips.
- USN diagnostic formatting for volume-open, unsupported-volume, journal-read,
  and file-id path-resolution failures.
- Windows interop binding checks for native USN calls such as `DeviceIoControl`.
- Tray pane placement for taskbar edges, high-DPI conversion, and small working
  areas.
- File browser selection model: tri-state folder toggling, immediate files
  only, recursive selection, retained manual child selections, parent
  child-selection indicators, and pending-change summaries.
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
- File browser flow: unsaved selection edits do not change service
  configuration until Save.
- File browser compiled selections: recursive folders include nested files,
  immediate-only folders exclude nested files, and selected-file rules capture
  only the selected files.
- Planned sync mapping gate: remote peers do not hydrate, patch, or create a
  newly selected target until mapping is confirmed.

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
- Service recovery.
- Tray/dashboard service connection.
- Tray activity pane placement inside the active monitor working area.
- Explorer integration registration.
- Restore smoke test from installed build.
- Developer CLI smoke test: back up a file, list versions, inspect the version,
  restore it, and compare hashes.
- Developer service package artefacts: install script, uninstall script,
  quickstart, and sample configuration.
