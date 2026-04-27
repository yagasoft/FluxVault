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
and diagnostics export. Configuration is stored in
`C:\ProgramData\FluxVault\config.json`; repository artefacts are written to the
configured local repository, with optional atomic mirroring into a cloud-sync
folder.

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
5. Normal stream reads capture readable files; the Windows service falls back to
   VSS through `vssadmin.exe` for locked files when privileges allow it.
6. Core chunking splits the captured bytes into FastCDC-style chunks.
7. Chunk fingerprints are compared against the local repository.
8. New chunks and a manifest are committed atomically.
9. Repository artefacts are mirrored into the configured cloud sync folder.
10. Compression is selected by policy. The default adaptive profile uses zstd,
    skips known compressed file types, uses lz4 for hot files, and keeps Brotli
    and LZMA as explicit ratio/cold-archive choices.
11. If retention is enabled, the service applies the configured retention policy
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
- Maximum concurrent captures: 1.

Directory notifications are latency hints and drive the live capture queue.
USN is the durable catch-up source where available. Reconciliation remains the
safety net when USN cannot prove continuity. Durable-change status carries a
latest-check label, timestamp, and structured diagnostic details, so the UI and
exported diagnostics can distinguish unable to open volume, unsupported volume,
journal ID change, journal wrap, checkpoint seeding, and file-id path
resolution failures.

## Options and status UX

Every editable Options control has a native WPF tooltip describing the
operational effect of the setting; long tooltip text wraps inside a bounded
width. The dashboard health strip keeps status compact but exposes detailed
durable-change information through the USN tooltip and diagnostics export.
The main header constrains long service text with ellipsis trimming so it
cannot overlap command buttons. The Capture tile is the live watcher/debounce
queue; the USN tile is the latest durable catch-up result.

The tray activity pane is positioned from the active monitor working area. The
WinForms cursor and monitor coordinates are converted to WPF device-independent
units before the pane is clamped inside the visible work area; on very small
work areas the pane height is reduced instead of opening behind the taskbar.

## Non-interference contract

FluxVault should not make normal user applications wait on backup reads.

- Normal capture opens source files read-only with `FileShare.ReadWrite |
  FileShare.Delete`.
- VSS capture reads from the shadow copy path, not the live source path.
- Repository and mirror writes are outside the watched source tree unless the
  user explicitly chooses such a layout.
- Restore and future hydration workflows must avoid unsafe overwrites of open
  or blocked targets unless the user chooses an alternate path or confirms the
  operation.
- Blocked files are surfaced through service status, diagnostics, the dashboard
  Activity view, and the tray activity pane.

## MVP harness flow

The CLI harness bypasses VSS/USN and commits a normal readable file stream
directly into the repository. This proves chunking, manifest creation, mirror
writes, listing, inspection, and restore without claiming open-file consistency.

## Storage boundary

The repository stores immutable chunks and append-only version manifests. A
manifest is the authoritative description of one captured file version. Chunks
are addressed by BLAKE3 digest and may be stored raw or encoded with zstd, lz4,
Brotli, or LZMA according to policy.

Retention is manifest-led. FluxVault groups versions by normalised source path,
keeps dense recent history, thins older history to hourly and daily buckets,
and always keeps at least the latest configured number of versions per source
file. After deleting pruned manifests locally, it garbage-collects only chunks
and metadata no remaining manifest references. Mirror cleanup is best-effort
and reported through status and diagnostics.

## Future release tracks

R2 introduces a WinFsp managed workspace for high-frequency large-file workloads.
R3 introduces Cloud Files API / ProjFS sync-root integration. These are separate
tracks because they change the namespace and write-path assumptions.
