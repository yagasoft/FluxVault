# Packaging and release

## Installer

FluxVault targets a per-machine installer because the service, VSS capture, and
Explorer integration require machine-level setup. MVP packages may be unsigned
developer artefacts. V1 public releases require signed binaries and installer.

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
`FluxVault Service`.

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
