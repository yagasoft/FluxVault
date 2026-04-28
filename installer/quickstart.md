# FluxVault developer quickstart

1. Run `eng\package.ps1`.
2. Open an elevated PowerShell session.
3. Run `artifacts\publish\install-service.ps1`.
4. Start `artifacts\publish\app\FluxVault.App.exe`.
5. Choose a repository folder, optionally choose a cloud-sync mirror folder, add a watched folder, then select **Save**.
6. Select **Run backup now**, edit a file, refresh, and restore a selected version to an alternate path.
7. If the dashboard reports that the service is stopped, use the Start service control. If Windows denies the request, rerun the dashboard or script from an elevated session.
8. Run `artifacts\publish\uninstall-service.ps1` to remove the unsigned developer service.

This MVP keeps all logs and diagnostics local. The developer service also writes service lifecycle, warning, and fatal recovery events to the Windows Application log under the `FluxVaultService` source. VSS capture requires administrator/service privileges and reports crash-consistent captures unless a later writer-aware provider proves app consistency.
