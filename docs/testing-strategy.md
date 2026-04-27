# Testing strategy

## Unit tests

- Policy resolution and inheritance.
- Chunk boundary validity and deterministic chunking.
- BLAKE3 fingerprinting.
- zstd round-trip compression.
- Manifest write/read.
- Deduplicated commit and restore.
- Retention decisions.

## Integration tests

- Generated large files.
- Open and locked file reads.
- Rapid edits and service restart catch-up.
- VSS unavailable or failed.
- Atomic cloud-folder mirror writes.
- Restore to original and alternate paths.

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
