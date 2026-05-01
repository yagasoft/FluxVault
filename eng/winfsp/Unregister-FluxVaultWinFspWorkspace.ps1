[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$MountName = "FluxVaultWorkspace"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$plan = [ordered]@{
    MountName = $MountName
    DriverInstallRequired = $true
    MachineMutation = $false
    Note = "Prepared only. This foundation script does not unregister OS-level WinFsp objects."
}

if ($PSCmdlet.ShouldProcess($MountName, "Prepare FluxVault WinFsp workspace unregistration plan")) {
    $plan | ConvertTo-Json -Depth 4
}
