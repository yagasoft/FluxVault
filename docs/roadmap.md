# Roadmap

## MVP

Build the full user-facing shape early: WPF dashboard, tray shell, Windows
service, watched folders, VSS + USN + smart cadence, chunked repository,
cloud-folder mirror, conservative automatic retention with Options dialog
controls, restore, diagnostics, and benchmarks.

## V1 production

Harden MVP for public use with installer support, service recovery, Explorer
restore integration, app-consistency reporting, upgrade/rollback tests, quota
controls, signed release readiness, and complete public documentation.

## R2: WinFsp performance workspace

Add a FluxVault-managed root or drive for critical huge files. The goal is to
control enough of the write path to keep more precise change journals without
building a FluxVault-owned kernel driver.

## R3: Cloud Files API / ProjFS

Add Windows-native sync-root, placeholder, hydration, and Explorer integration.
This release must be designed separately because it changes how users see and
interact with protected content.

## R4: Direct cloud adapters

Add provider-neutral direct upload adapters. Prioritise Azure Blob and
S3-compatible storage, resumable uploads, lifecycle policies, and server-side
retention.

## Later

Client-side encryption, enterprise policy, central monitoring, managed
templates, app-specific optimisers, and optional ReFS acceleration.
