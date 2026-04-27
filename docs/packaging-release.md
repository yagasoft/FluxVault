# Packaging and release

## Installer

FluxVault targets a per-machine installer because the service, VSS capture, and
Explorer integration require machine-level setup. MVP packages may be unsigned
developer artefacts. V1 public releases require signed binaries and installer.

Developer packages include:

- WPF dashboard/tray shell
- Windows service shell
- CLI backup harness
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
`FluxVault Service`.

## GitHub releases

GitHub Releases are the first distribution channel. The app may check releases
when the user asks it to check for updates. It must not silently replace the
Windows service.

## CI gates

- Restore and build.
- Unit and integration tests.
- Package validation.
- Dependency vulnerability scan.
- CodeQL/security scan for final release or production PRs. MVP feature PRs use
  the `build-test` workflow as the required gate unless explicitly promoted to
  release readiness.
- SBOM generation.
- Release artefact upload.
