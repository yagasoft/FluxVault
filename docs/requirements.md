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
- Configurable zstd compression.
- Smart retention with dense recent versions and thinner older versions.
- Basic restore browser supporting original and alternate restore paths.
- Local logs and diagnostics export only.
- Developer command-line harness for backing up, listing, inspecting, and
  restoring normal readable files before the VSS/USN service path is complete.

## Explicit non-goals for v1

- No client-side encryption.
- No direct cloud API upload.
- No support commitment for Windows 10, Windows Server, network shares, or NAS.
- No FluxVault-owned kernel driver.
- No hidden telemetry.
- The command-line harness does not provide open-file consistency; it backs up
  normal file streams only.

## Consistency language

FluxVault detects changed files and then identifies changed content efficiently.
It must not claim generic changed-byte detection for arbitrary Windows files.
VSS captures are app-consistent only where relevant VSS writers participate and
the capture result proves that status.
