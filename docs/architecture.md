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

## MVP capture flow

1. Directory notification wakes the service quickly when a watched path changes.
2. USN journal catch-up confirms the changed file set and repairs missed events.
3. Smart cadence coalesces hot files to avoid repeated full-file reads.
4. VSS supplies a stable read view for open or consistency-sensitive files.
5. Core chunking splits the captured bytes into FastCDC-style chunks.
6. Chunk fingerprints are compared against the local repository.
7. New chunks and a manifest are committed atomically.
8. Repository artefacts are mirrored into the configured cloud sync folder.

## MVP harness flow

The CLI harness bypasses VSS/USN and commits a normal readable file stream
directly into the repository. This proves chunking, manifest creation, mirror
writes, listing, inspection, and restore without claiming open-file consistency.

## Storage boundary

The repository stores immutable chunks and append-only version manifests. A
manifest is the authoritative description of one captured file version. Chunks
are addressed by BLAKE3 digest and may be zstd-compressed according to policy.

## Future release tracks

R2 introduces a WinFsp managed workspace for high-frequency large-file workloads.
R3 introduces Cloud Files API / ProjFS sync-root integration. These are separate
tracks because they change the namespace and write-path assumptions.
