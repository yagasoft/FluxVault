# Packaging and release

## Installer

FluxVault targets a per-machine installer because the service, VSS capture, and
Explorer integration require machine-level setup. MVP packages may be unsigned
developer artefacts. V1 public releases require signed binaries and installer.

Developer packages include:

- WPF dashboard/tray shell
- Windows service shell
- CLI backup harness

## GitHub releases

GitHub Releases are the first distribution channel. The app may check releases
when the user asks it to check for updates. It must not silently replace the
Windows service.

## CI gates

- Restore and build.
- Unit and integration tests.
- Package validation.
- Dependency vulnerability scan.
- CodeQL/security scan.
- SBOM generation.
- Release artefact upload.
