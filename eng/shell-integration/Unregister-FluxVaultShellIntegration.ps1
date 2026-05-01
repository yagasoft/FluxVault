[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$DisplayName = "FluxVault",
    [string]$ManifestPath = (Join-Path $PSScriptRoot "FluxVault.CloudFiles.ProjFs.manifest.json")
)

$plan = [ordered]@{
    Action = "PlanFluxVaultShellIntegrationUnregistration"
    DisplayName = $DisplayName
    ManifestPath = $ManifestPath
    RegistrationDeferred = $true
    PlaceholderCleanupDeferred = $true
    MachineMutation = $false
}

if ($PSCmdlet.ShouldProcess($DisplayName, "Emit FluxVault shell integration unregistration plan")) {
    $plan | ConvertTo-Json -Depth 4
}
