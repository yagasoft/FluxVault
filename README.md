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

## Local backup harness

The MVP command-line harness backs up normal readable files into a local
FluxVault repository. It does not use VSS or USN yet.

```powershell
dotnet run --project .\src\FluxVault.Cli -- backup --source .\README.md --repository .\.tmp\vault --mirror .\.tmp\cloud
dotnet run --project .\src\FluxVault.Cli -- list --repository .\.tmp\vault
dotnet run --project .\src\FluxVault.Cli -- inspect --repository .\.tmp\vault --version <version-id>
dotnet run --project .\src\FluxVault.Cli -- restore --repository .\.tmp\vault --version <version-id> --output .\.tmp\README.restored.md
```

Exit codes are `0` for success, `1` for invalid arguments, `2` for not found,
and `3` for backup/restore failures.
