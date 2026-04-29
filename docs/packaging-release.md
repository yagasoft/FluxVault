# Packaging and release

## Installer

FluxVault targets a per-machine installer because the service and writer-aware
VSS capture require machine-level setup. Explorer file/folder entry points are
currently per-user HKCU registrations managed from the app Options dialog. MVP
packages may be unsigned developer artefacts. V1 public releases require signed
binaries and installer.

Developer packages include:

- WPF dashboard/tray shell
- Windows service shell
- CLI backup harness
- FluxVault app icon and Yagasoft About-window logo asset
- install and uninstall PowerShell scripts
- quickstart and sample configuration

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

This is V1 script hardening plus the first compact Explorer menu packaging
foundation. Production installer technology, signing, upgrade packages, and
full rollback semantics remain release-track work.
Developer install/uninstall scripts do not register Explorer context-menu
commands; the dashboard owns those per-user entries through Options. The app
currently writes HKCU full-menu verbs for `Add to FluxVault`, `Show FluxVault
versions`, and `Remove from FluxVault`. Developer packaging now also includes a
sparse package manifest and native `FluxVault.ExplorerCommand.dll` scaffold for
Windows 11 compact menus. The package script writes `compact-menu-status.txt`
when Visual C++ build targets, MakeAppx, SignTool, or a signing certificate are
missing, so the full-menu path remains usable even before production packaging
is final.

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
