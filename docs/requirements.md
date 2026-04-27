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
- VSS-backed reads for open files.
- Chunked deduplicated storage with BLAKE3 fingerprints.
- Configurable hot-file cadence with bounded forced snapshots.
- Non-interfering capture reads that do not take exclusive source-file locks.
- Adaptive compression with zstd default, lz4 hot-file mode, Brotli ratio mode,
  LZMA cold-archive mode, and off mode for already-compressed formats.
- Smart retention with dense recent versions and thinner older versions
  implemented with conservative MVP defaults: keep all versions for 24 hours,
  one per hour for 30 days, one per day for 180 days, and at least the latest
  20 versions per source file.
- Basic restore browser supporting original and alternate restore paths.
- Local logs and diagnostics export only.
- Tray activity pane and dashboard Activity view for pending, in-progress,
  blocked, failed, and completed capture events.
- Helpful tooltips for every Options control, covering retention, capture
  cadence, compression, preview, apply, save, and close behaviours.
- USN health must show exact fallback reasons in the UI and diagnostics instead
  of only saying unavailable.
- Developer command-line harness for backing up, listing, inspecting, and
  restoring normal readable files without claiming VSS or USN consistency.

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
the capture result proves that status.
