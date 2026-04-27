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

FluxVault now has a developer-usable MVP loop:

- WPF dashboard connected to the service over local named-pipe IPC.
- Per-machine Worker Service host with persisted configuration under
  `C:\ProgramData\FluxVault\config.json`.
- Watched-folder backup using file-system notifications, USN journal catch-up,
  and periodic reconciliation fallback.
- Normal readable-file capture with VSS fallback for locked files when the
  service has sufficient Windows privileges.
- Version list, inspect, restore, diagnostics export, local repository, and
  optional cloud-folder mirror.

VSS captures are reported as crash-consistent in this MVP. Writer-aware
app-consistency reporting remains a hardening item.

## Build

```powershell
dotnet restore
dotnet build
dotnet test
```

## Developer app and service quickstart

```powershell
.\eng\package.ps1

# Elevated PowerShell
.\artifacts\publish\install-service.ps1

# Normal desktop session
.\artifacts\publish\app\FluxVault.App.exe
```

In the dashboard, choose a repository folder, optionally choose a cloud-sync
mirror folder, add a watched folder, save the configuration, run a backup, then
restore a selected version to an alternate path.

To remove the unsigned developer service:

```powershell
.\artifacts\publish\uninstall-service.ps1
```

## Local backup harness

The MVP command-line harness backs up normal readable files into a local
FluxVault repository. It does not use VSS or USN.

```powershell
dotnet run --project .\src\FluxVault.Cli -- backup --source .\README.md --repository .\.tmp\vault --mirror .\.tmp\cloud
dotnet run --project .\src\FluxVault.Cli -- list --repository .\.tmp\vault
dotnet run --project .\src\FluxVault.Cli -- inspect --repository .\.tmp\vault --version <version-id>
dotnet run --project .\src\FluxVault.Cli -- restore --repository .\.tmp\vault --version <version-id> --output .\.tmp\README.restored.md
```

Exit codes are `0` for success, `1` for invalid arguments, `2` for not found,
and `3` for backup/restore failures.
