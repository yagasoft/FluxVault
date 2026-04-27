# FluxVault

FluxVault is a Windows 11 backup utility for frequent, versioned protection of
large local files that may be open while users work. It combines a WPF dashboard,
a tray app, and a per-machine Windows service.

## MVP direction

- Watched local folders with recursive include/exclude policies.
- USN journal catch-up plus directory notifications for low-latency detection.
- VSS snapshot reads for open-file capture and app-consistency where writers cooperate.
- FastCDC-style chunking with BLAKE3 chunk fingerprints.
- Configurable zstd compression and smart retention.
- Local immutable repository plus atomic writes into a user-selected cloud sync folder.
- Basic restore to original or alternate paths.
- Local diagnostics only; no hidden telemetry.

## Repository status

This is an early scaffold. The first implementation slice focuses on the
deduplicated repository, restore path, and release documentation before the
Windows-specific capture provider is completed.

## Build

```powershell
dotnet restore
dotnet build
dotnet test
```
