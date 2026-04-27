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
- Normal-file capture, non-interfering source sharing, and VSS fallback
  selection.
- Resource profile debounce delays.

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
- CodeQL workflow trigger guard: no `pull_request` trigger.
- Missing watched-folder failure reporting.

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
- Explorer integration registration.
- Restore smoke test from installed build.
- Developer CLI smoke test: back up a file, list versions, inspect the version,
  restore it, and compare hashes.
- Developer service package artefacts: install script, uninstall script,
  quickstart, and sample configuration.
