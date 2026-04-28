param(
    [string]$ServiceName = "FluxVaultService",
    [string]$ProgramDataRoot = "$env:ProgramData\FluxVault",
    [string]$EventLogSource = "FluxVaultService",
    [int]$ServiceTransitionTimeoutSeconds = 30,
    [switch]$RemoveProgramData,
    [switch]$RemoveEventLogSource
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

function Wait-ServiceStatus {
    param(
        [string]$Name,
        [System.ServiceProcess.ServiceControllerStatus]$Status,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $Name -ErrorAction Stop
        if ($service.Status -eq $Status) {
            return
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for service '$Name' to reach status '$Status'."
}

function Wait-ServiceDeleted {
    param(
        [string]$Name,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            return
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for service '$Name' to be deleted."
}

Assert-Administrator

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        sc.exe stop $ServiceName | Out-Null
        Wait-ServiceStatus -Name $ServiceName -Status Stopped -TimeoutSeconds $ServiceTransitionTimeoutSeconds
    }

    sc.exe delete $ServiceName | Out-Null
    Wait-ServiceDeleted -Name $ServiceName -TimeoutSeconds $ServiceTransitionTimeoutSeconds
    Write-Host "FluxVault service removed."
} else {
    Write-Host "FluxVault service is not installed."
}

if ($RemoveProgramData) {
    if (Test-Path -LiteralPath $ProgramDataRoot) {
        Remove-Item -LiteralPath $ProgramDataRoot -Recurse -Force
        Write-Host "Removed FluxVault ProgramData at $ProgramDataRoot."
    }
} else {
    Write-Host "FluxVault ProgramData preserved at $ProgramDataRoot."
}

if ($RemoveEventLogSource) {
    if ([System.Diagnostics.EventLog]::SourceExists($EventLogSource)) {
        [System.Diagnostics.EventLog]::DeleteEventSource($EventLogSource)
        Write-Host "Removed Windows Event Log source $EventLogSource."
    }
}
