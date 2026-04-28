# Installer skeleton

FluxVault targets a per-machine Windows installer for V1. The installer must:

- install `FluxVault.Service` as `FluxVault Service`
- install `FluxVault.App` for dashboard/tray startup
- register Explorer restore integration in V1
- create `%ProgramData%\FluxVault` for machine state
- create `%LocalAppData%\FluxVault` for per-user UI state
- support clean upgrade, rollback, and uninstall
- configure delayed automatic service start, restart recovery, and Windows
  Event Log source registration
- require Authenticode signing before public V1 release

MVP developer packages can be produced with `dotnet publish` while the installer
technology is finalised. The developer scripts now cover the service recovery
and Event Log basics, but they are not a signed production installer.
