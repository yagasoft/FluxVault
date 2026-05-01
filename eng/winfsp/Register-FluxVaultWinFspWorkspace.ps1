[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspacePath,

    [string]$MountName = "FluxVaultWorkspace",

    [string]$ManifestPath = (Join-Path $PSScriptRoot "FluxVault.WinFsp.Workspace.manifest.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "WinFsp workspace manifest was not found: $ManifestPath"
}

$plan = [ordered]@{
    WorkspacePath = $WorkspacePath
    MountName = $MountName
    ManifestPath = $ManifestPath
    DriverInstallRequired = $true
    MachineMutation = $false
    Note = "Prepared only. Run with -WhatIf to review; do not register without explicit release approval."
}

if ($PSCmdlet.ShouldProcess($WorkspacePath, "Prepare FluxVault WinFsp workspace registration plan")) {
    $plan | ConvertTo-Json -Depth 4
}
