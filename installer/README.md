# Installer skeleton

FluxVault targets a per-machine Windows installer for V1. The installer must:

- install `FluxVault.Service` as `FluxVault Service`
- install `FluxVault.App` for dashboard/tray startup
- install the full published app, service, and CLI payloads, not only their
  executable entry points
- leave per-user Explorer restore integration to the app Options dialog unless
  a future installer decision explicitly changes ownership
- create `%ProgramData%\FluxVault` for machine state
- create `%LocalAppData%\FluxVault` for per-user UI state
- support clean upgrade, rollback, and uninstall
- configure delayed automatic service start, restart recovery, and Windows
  Event Log source registration
- support the current unsigned consumer release profile with clear Unknown
  publisher / SmartScreen warnings; Authenticode signing remains a future
  production-hardening track

MVP developer packages can be produced with `dotnet publish` while the installer
technology is finalised. The developer scripts now cover the service recovery
and Event Log basics, but they are not a signed production installer and do not
register Explorer context-menu commands.

Run `eng\smoke-release-install.ps1 -UninstallAfter` from an elevated clean
validation session to smoke-test the generated unsigned Burn setup, installed
service, installed CLI, Event Log source, ProgramData preservation, and bundle
uninstall path. The script refuses existing ProgramData by default; use
`-AllowExistingProgramData` only for deliberate non-clean validation.
