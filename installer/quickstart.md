# FluxVault developer quickstart

1. Run `eng\package.ps1`.
2. Open an elevated PowerShell session.
3. Run `artifacts\publish\install-service.ps1`.
4. Start `artifacts\publish\app\FluxVault.App.exe`.
5. Choose a repository folder, optionally choose a cloud-sync mirror folder, add a watched folder, then select **Save**.
6. Select **Run backup now**, edit a file, refresh, and restore a selected version to an alternate path.
7. Run `artifacts\publish\uninstall-service.ps1` to remove the unsigned developer service.

This MVP keeps all logs and diagnostics local. VSS capture requires administrator/service privileges and reports crash-consistent captures unless a later writer-aware provider proves app consistency.
