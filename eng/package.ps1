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

dotnet publish (Join-Path $root "src\FluxVault.Cli\FluxVault.Cli.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output (Join-Path $output "cli")

Copy-Item -LiteralPath (Join-Path $root "eng\install-service.ps1") -Destination (Join-Path $output "install-service.ps1") -Force
Copy-Item -LiteralPath (Join-Path $root "eng\uninstall-service.ps1") -Destination (Join-Path $output "uninstall-service.ps1") -Force
Copy-Item -LiteralPath (Join-Path $root "installer\quickstart.md") -Destination (Join-Path $output "quickstart.md") -Force
Copy-Item -LiteralPath (Join-Path $root "installer\sample-config.json") -Destination (Join-Path $output "sample-config.json") -Force

Write-Host "Published FluxVault developer artefacts to $output"
