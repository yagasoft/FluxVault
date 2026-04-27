param(
    [string]$ServiceName = "FluxVaultService",
    [string]$PublishRoot = "$PSScriptRoot\..\artifacts\publish",
    [string]$ProgramDataRoot = "$env:ProgramData\FluxVault"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session."
}

$serviceExe = Resolve-Path (Join-Path $PublishRoot "service\FluxVault.Service.exe")
New-Item -ItemType Directory -Force -Path $ProgramDataRoot | Out-Null

if (-not (Test-Path (Join-Path $ProgramDataRoot "config.json"))) {
    Copy-Item -LiteralPath (Join-Path $PublishRoot "sample-config.json") -Destination (Join-Path $ProgramDataRoot "config.json")
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe stop $ServiceName | Out-Null
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $ServiceName binPath= "`"$serviceExe`"" start= auto DisplayName= "FluxVault Service" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/""/60000 | Out-Null
sc.exe start $ServiceName | Out-Null

Write-Host "FluxVault service installed and started."
