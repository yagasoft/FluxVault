# Packaging and release

## Installer

FluxVault targets a per-machine installer because the service and writer-aware
VSS capture require machine-level setup. Explorer file/folder entry points are
per-user HKCU registrations managed from the app Options dialog. The default
consumer release path is one unsigned WiX Burn bootstrapper EXE wrapping a
per-machine WiX MSI. This unsigned profile deliberately does not install the
sparse MSIX package identity used by Windows 11 compact Explorer menus.

Developer packages include:

- WPF dashboard/tray shell
- Windows service shell
- CLI backup harness
- FluxVault app icon and Yagasoft About-window logo asset
- install and uninstall PowerShell scripts
- quickstart and sample configuration

Developer packaging prerequisites:

- .NET SDK 10.0.x for the managed app, service, CLI, and tests
- Windows SDK for MakeAppx and SignTool
- Visual Studio 2022 Build Tools with **Desktop development with C++**, MSVC
  v143 x64/x86 tools, and the Windows SDK to build the Windows 11 compact-menu
  `FluxVault.ExplorerCommand.dll`

`eng/package.ps1` publishes the developer artefacts to `artifacts\publish`:

- `service`
- `app`
- `cli`
- `install-service.ps1`
- `uninstall-service.ps1`
- `quickstart.md`
- `sample-config.json`

The developer service installs as `FluxVaultService` with display name
`FluxVault Service`. The developer install script registers the
`FluxVaultService` Windows Event Log source, configures delayed automatic start,
and sets SCM recovery actions to restart after internal service failure. The
uninstall script preserves ProgramData and the Event Log source by default; it
removes them only when `-RemoveProgramData` or `-RemoveEventLogSource` are
provided.

Developer scripts remain developer tooling. Consumer releases use
`eng/release-package.ps1`, which builds branded files under
`artifacts\release`:

- `Yagasoft-FluxVault-v1.0.0-win-x64-Setup.exe`
- `Yagasoft-FluxVault-v1.0.0-win-x64-checksums-sha256.txt`
- `Yagasoft-FluxVault-v1.0.0-win-x64-release-notes.md`
- `Yagasoft-FluxVault-v1.0.0-win-x64-release-status.txt`

The MSI installs the full published app, service, and CLI payloads under
Program Files, creates the ProgramData folder as permanent/never-overwrite
state, installs `FluxVaultService`, starts it on install, sets delayed
automatic start, configures SCM restart recovery, and registers the Application
Event Log source. The stable `MajorUpgrade` metadata gives future releases a
single upgrade path while preserving `C:\ProgramData\FluxVault`.

The unsigned consumer Burn bundle chains only the MSI. It does not run
`Add-AppxPackage`, it does not include `FluxVault.SparsePackage.msix`, and it
does not require the native compact-menu DLL. Windows will show Unknown
publisher and may show Microsoft Defender SmartScreen warnings because this
profile is intentionally unsigned.

Developer install/uninstall scripts do not register Explorer context-menu
commands; the dashboard owns those per-user entries through Options. The app
currently writes HKCU full-menu verbs for `Add to FluxVault`, `Show FluxVault
versions`, and `Remove from FluxVault`. Developer packaging now also includes a
sparse package manifest and native `FluxVault.ExplorerCommand.dll` scaffold for
Windows 11 compact menus. The package script writes `compact-menu-status.txt`
when Visual C++ build targets, MakeAppx, SignTool, or a signing certificate are
missing, so the full-menu path remains usable even before production packaging
is final.
If the native C++ targets are missing, `eng/package.ps1` now detects that before
calling MSBuild, skips only the compact-menu DLL build, and continues publishing
the managed artefacts and sparse package files.

Unsigned release behaviour:

- `eng/release-package.ps1 -Configuration Release` produces the unsigned
  consumer setup EXE and supporting checksum, notes, and status files.
- The release workflow does not require certificate secrets for this draft
  unsigned profile.
- The Windows 11 compact Explorer menu remains a future signed/package-identity
  distribution path. The normal full Explorer menu under **Show more options**
  remains available from Options in the unsigned consumer install.

## Release smoke validation

`eng\smoke-release-install.ps1 -UninstallAfter` is the repeatable local
validation gate for the unsigned consumer setup. It must be run from an elevated
PowerShell session on a clean validation machine/session because it installs and
uninstalls the per-machine bundle.

The harness verifies the setup checksum, refuses to overwrite an existing
`FluxVaultService` or existing `C:\ProgramData\FluxVault` by default, installs
the Burn bundle silently with logs under `artifacts\release-smoke`, verifies
service startup, delayed automatic start, full Program Files payloads,
ProgramData creation, Event Log source registration, and an installed CLI
backup/list/restore round trip, then uninstalls and checks that ProgramData
remains. Use `-AllowExistingProgramData` only when deliberately collecting
non-clean evidence against preserved existing machine state.

This smoke check proves installer mechanics for the current unsigned package.
It does not prove SmartScreen reputation, Authenticode signing, Windows 11
compact-menu package identity, first-run product guidance, or third-party VSS
writer certification.

Current local evidence: the corrected release package built on 2026-04-30,
the MSI administrative extraction contained the full app/service/CLI payloads,
and an elevated `eng\smoke-release-install.ps1 -UninstallAfter` run passed
install, service startup, installed CLI backup/list/restore, uninstall, service
removal, and ProgramData preservation.

## GitHub releases

GitHub Releases are the first distribution channel. The app may check releases
when the user asks it to check for updates. It must not silently replace the
Windows service.

## CI gates

- `ci.yml` runs restore, build, and tests as the `build-test` job on pull
  requests and on pushes to `main`.
- `codeql.yml` does not run on pull requests. It runs on pushes to `main`,
  weekly schedule, and manual dispatch.
- MVP feature PRs wait only for `build-test`. Do not wait for CodeQL unless the
  PR is explicitly promoted to a final release or production-readiness gate.
- Package validation, dependency review, SBOM generation, and release artefact
  upload are V1/release-track gates. Signing is a future production-hardening
  track, not a prerequisite for the current unsigned consumer draft.
- The `release-package` workflow is separate from pull request `build-test` so
  normal feature PRs do not depend on release packaging or installer elevation.
