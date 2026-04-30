# Packaging and release

## Installer

FluxVault targets a per-machine installer because the service and writer-aware
VSS capture require machine-level setup. Explorer file/folder entry points are
per-user HKCU registrations managed from the app Options dialog. The production
package path is a WiX per-machine MSI plus a WiX Burn bootstrapper that also
carries the sparse MSIX package used for Windows 11 compact Explorer menu
identity.

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

Developer scripts remain developer tooling. Production releases use
`eng/release-package.ps1`, which builds:

- `FluxVault.Installer.msi`
- `FluxVault.Setup.exe`
- `FluxVault.SparsePackage.msix`
- `release-package-status.txt`

The MSI installs the app, service, CLI, and native Explorer command DLL under
Program Files, creates the ProgramData folder as permanent/never-overwrite
state, installs `FluxVaultService`, starts it on install, sets delayed
automatic start, configures SCM restart recovery, and registers the Application
Event Log source. The stable `MajorUpgrade` metadata gives future releases a
single upgrade path while preserving `C:\ProgramData\FluxVault`.

The Burn bundle wraps the MSI and runs the signed sparse MSIX installation for
the installing user. The sparse package leg is marked permanent because Burn
does not have a reliable built-in detector for this PowerShell-driven sparse
MSIX install. Removing package identity remains an explicit
`Remove-AppxPackage` operation, separate from MSI rollback and from repository
state preservation.

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

Release signing:

- `eng/release-package.ps1 -RequireSigning` fails unless a package certificate
  path is supplied.
- When `-PackageCertificatePath` is supplied, the script signs the MSI, bundle,
  and sparse MSIX using SignTool.
- The `release-package` workflow is manual/opt-in. It may build unsigned
  validation artefacts without signing secrets, but public release runs should
  provide the certificate and password secrets and use the required-signing path.

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
- Package validation, dependency review, SBOM generation, signing, and release
  artefact upload are V1/release-track gates.
- The `release-package` workflow is separate from pull request `build-test` so
  normal feature PRs do not depend on release signing secrets or installer
  elevation.
