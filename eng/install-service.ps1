param(
    [string]$ServiceName = "FluxVaultService",
    [string]$PublishRoot = "",
    [string]$ProgramDataRoot = "$env:ProgramData\FluxVault",
    [string]$EventLogSource = "FluxVaultService",
    [int]$ServiceTransitionTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

function Resolve-PublishRoot {
    param(
        [string]$RequestedPublishRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPublishRoot)) {
        return (Resolve-Path -LiteralPath $RequestedPublishRoot).Path
    }

    $copiedPackageServiceExe = Join-Path $PSScriptRoot "service\FluxVault.Service.exe"
    if (Test-Path -LiteralPath $copiedPackageServiceExe) {
        return $PSScriptRoot
    }

    $sourceTreeServiceExe = Join-Path $PSScriptRoot "..\artifacts\publish\service\FluxVault.Service.exe"
    if (Test-Path -LiteralPath $sourceTreeServiceExe) {
        return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\artifacts\publish")).Path
    }

    throw "Could not find FluxVault published service artefacts. Run eng\package.ps1 first or pass -PublishRoot."
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

function Ensure-EventLogSource {
    param(
        [string]$Source
    )

    if (-not [System.Diagnostics.EventLog]::SourceExists($Source)) {
        New-EventLog -LogName Application -Source $Source
    }
}

function Remove-ExistingService {
    param(
        [string]$Name,
        [int]$TimeoutSeconds
    )

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        sc.exe stop $Name | Out-Null
        Wait-ServiceStatus -Name $Name -Status Stopped -TimeoutSeconds $TimeoutSeconds
    }

    sc.exe delete $Name | Out-Null
    Wait-ServiceDeleted -Name $Name -TimeoutSeconds $TimeoutSeconds
}

function Restore-PreviousService {
    param(
        [object]$PreviousService
    )

    if ($null -eq $PreviousService) {
        return
    }

    try {
        $displayName = if ([string]::IsNullOrWhiteSpace($PreviousService.DisplayName)) { "FluxVault Service" } else { $PreviousService.DisplayName }
        sc.exe create $PreviousService.Name binPath= $PreviousService.PathName start= auto DisplayName= $displayName | Out-Null
    }
    catch {
        Write-Warning "Failed to restore previous service registration: $($_.Exception.Message)"
    }
}

Assert-Administrator

$resolvedPublishRoot = Resolve-PublishRoot -RequestedPublishRoot $PublishRoot
$serviceExe = Resolve-Path (Join-Path $resolvedPublishRoot "service\FluxVault.Service.exe")
New-Item -ItemType Directory -Force -Path $ProgramDataRoot | Out-Null

if (-not (Test-Path (Join-Path $ProgramDataRoot "config.json"))) {
    Copy-Item -LiteralPath (Join-Path $resolvedPublishRoot "sample-config.json") -Destination (Join-Path $ProgramDataRoot "config.json")
}

Ensure-EventLogSource -Source $EventLogSource

$previousService = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue

try {
    Remove-ExistingService -Name $ServiceName -TimeoutSeconds $ServiceTransitionTimeoutSeconds

    sc.exe create $ServiceName binPath= "`"$serviceExe`"" start= auto DisplayName= "FluxVault Service" | Out-Null
    sc.exe config $ServiceName start= delayed-auto | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/""/60000 | Out-Null
    sc.exe failureflag $ServiceName 1 | Out-Null
    sc.exe start $ServiceName | Out-Null
    Wait-ServiceStatus -Name $ServiceName -Status Running -TimeoutSeconds $ServiceTransitionTimeoutSeconds
}
catch {
    Restore-PreviousService -PreviousService $previousService
    throw
}

Write-Host "FluxVault service installed and started."
