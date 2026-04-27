param(
    [string]$ServiceName = "FluxVaultService"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe stop $ServiceName | Out-Null
    sc.exe delete $ServiceName | Out-Null
    Write-Host "FluxVault service removed."
} else {
    Write-Host "FluxVault service is not installed."
}
