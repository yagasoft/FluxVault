[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SyncRootPath = "$env:ProgramData\FluxVault\shell-integration\sync-root",
    [string]$DisplayName = "FluxVault",
    [string]$PlaceholderStatePath = "$env:ProgramData\FluxVault\shell-integration\state",
    [string]$ManifestPath = (Join-Path $PSScriptRoot "FluxVault.CloudFiles.ProjFs.manifest.json")
)

$plan = [ordered]@{
    Action = "PlanFluxVaultShellIntegrationRegistration"
    SyncRootPath = $SyncRootPath
    DisplayName = $DisplayName
    PlaceholderStatePath = $PlaceholderStatePath
    ManifestPath = $ManifestPath
    RegistrationDeferred = $true
    PlaceholderCreationDeferred = $true
    MachineMutation = $false
    Note = "Static planning helper only. Run with -WhatIf for review; no shell provider registration or placeholder creation is performed by this script."
}

if ($PSCmdlet.ShouldProcess($DisplayName, "Emit FluxVault shell integration registration plan")) {
    $plan | ConvertTo-Json -Depth 4
}
