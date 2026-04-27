param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\publish"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root $OutputRoot

New-Item -ItemType Directory -Force -Path $output | Out-Null

dotnet publish (Join-Path $root "src\FluxVault.Service\FluxVault.Service.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output (Join-Path $output "service")

dotnet publish (Join-Path $root "src\FluxVault.App\FluxVault.App.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output (Join-Path $output "app")

Write-Host "Published FluxVault developer artefacts to $output"
