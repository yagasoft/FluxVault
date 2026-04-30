param(
    [string]$ReleaseVersion = "v1.0.1",
    [string]$Runtime = "win-x64",
    [string]$SetupPath,
    [string]$ChecksumPath,
    [string]$SmokeRoot = (Join-Path $PSScriptRoot "..\artifacts\release-smoke"),
    [switch]$UninstallAfter,
    [switch]$AllowExistingProgramData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$serviceName = "FluxVaultService"
$programDataPath = "C:\ProgramData\FluxVault"

function Normalize-ReleaseVersion([string]$ReleaseVersion) {
    if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
        throw "ReleaseVersion is required."
    }

    $trimmed = $ReleaseVersion.Trim()
    if ($trimmed -notmatch '^v?\d+\.\d+\.\d+$') {
        throw "ReleaseVersion must be in vMAJOR.MINOR.PATCH format, for example v1.0.1."
    }

    if ($trimmed.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "v$($trimmed.Substring(1))"
    }

    return "v$trimmed"
}

$releaseVersion = Normalize-ReleaseVersion $ReleaseVersion
$artifactPrefix = "Yagasoft-FluxVault-$releaseVersion-$Runtime"
$setupArtifactName = "$artifactPrefix-Setup.exe"
$checksumArtifactName = "$artifactPrefix-checksums-sha256.txt"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-FullPath {
    param([string]$Path)

    $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found at $Path."
    }
}

function Assert-Checksum {
    param(
        [string]$SetupPath,
        [string]$ChecksumPath
    )

    $setupName = Split-Path -Leaf $SetupPath
    $checksumLine = Get-Content -LiteralPath $ChecksumPath |
        Where-Object { $_ -match "^\s*([0-9A-Fa-f]{64})\s+$([regex]::Escape($setupName))\s*$" } |
        Select-Object -First 1

    if (-not $checksumLine) {
        throw "Checksum file $ChecksumPath does not contain an entry for $setupName."
    }

    $expectedHash = (($checksumLine -split "\s+") | Where-Object { $_ })[0].ToUpperInvariant()
    $actualHash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToUpperInvariant()

    if ($actualHash -ne $expectedHash) {
        throw "Checksum mismatch for $setupName. Expected $expectedHash but found $actualHash."
    }
}

function Wait-ServiceStatus {
    param(
        [System.ServiceProcess.ServiceControllerStatus]$Status,
        [TimeSpan]$Timeout = [TimeSpan]::FromSeconds(60)
    )

    $deadline = (Get-Date).Add($Timeout)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq $Status) {
            return
        }

        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $state = if ($current) { $current.Status } else { "missing" }
    throw "$serviceName did not reach $Status within $($Timeout.TotalSeconds) seconds. Current state: $state."
}

function Wait-ServiceDeleted {
    param([TimeSpan]$Timeout = [TimeSpan]::FromSeconds(60))

    $deadline = (Get-Date).Add($Timeout)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if (-not $service) {
            return
        }

        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    throw "$serviceName still exists after uninstall."
}

function Invoke-Bundle {
    param(
        [string[]]$Arguments,
        [string]$LogPath
    )

    $argumentList = @($Arguments + @("-log", "`"$LogPath`""))
    $process = Start-Process -FilePath $SetupPath -ArgumentList $argumentList -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Installer command failed with exit code $($process.ExitCode). Log: $LogPath"
    }

    if ($process.ExitCode -eq 3010) {
        Write-Warning "Installer reported success with reboot required. Continue validating installed state before any restart."
    }
}

function Assert-ServiceDelayedAutoStart {
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $delayed = (Get-ItemProperty -LiteralPath $serviceKey -Name DelayedAutoStart -ErrorAction Stop).DelayedAutoStart
    if ($delayed -ne 1) {
        throw "$serviceName DelayedAutoStart registry value should be 1 but was $delayed."
    }
}

function Assert-InstalledFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Installed file was not found: $Path"
    }
}

function Invoke-CheckedCli {
    param(
        [string]$CliPath,
        [string[]]$Arguments
    )

    $output = & $CliPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "FluxVault.Cli.exe failed with exit code $exitCode for arguments '$($Arguments -join " ")'. Output: $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Invoke-CliSmoke {
    param([string]$CliPath)

    $cliSmokeRoot = Join-Path $SmokeRoot "cli"
    $repositoryPath = Join-Path $cliSmokeRoot "repository"
    $sourcePath = Join-Path $cliSmokeRoot "source.txt"
    $restorePath = Join-Path $cliSmokeRoot "restored.txt"

    if (Test-Path -LiteralPath $cliSmokeRoot) {
        Remove-Item -LiteralPath $cliSmokeRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $cliSmokeRoot | Out-Null
    Set-Content -LiteralPath $sourcePath -Value "FluxVault release smoke payload" -Encoding UTF8

    Write-Host "Running CLI smoke: backup --source"
    $backupOutput = Invoke-CheckedCli -CliPath $CliPath -Arguments @(
        "backup", "--source", $sourcePath, "--repository", $repositoryPath
    )
    $versionLine = $backupOutput | Where-Object { $_ -like "Version:*" } | Select-Object -First 1
    if (-not $versionLine) {
        throw "CLI backup output did not contain a Version line. Output: $($backupOutput -join [Environment]::NewLine)"
    }

    $versionId = $versionLine.Substring("Version:".Length).Trim()
    Write-Host "Running CLI smoke: list --repository"
    $listOutput = Invoke-CheckedCli -CliPath $CliPath -Arguments @("list", "--repository", $repositoryPath)
    if (-not (($listOutput -join [Environment]::NewLine).Contains($versionId))) {
        throw "CLI list output did not include version $versionId."
    }

    Write-Host "Running CLI smoke: restore --repository"
    Invoke-CheckedCli -CliPath $CliPath -Arguments @(
        "restore", "--repository", $repositoryPath, "--version", $versionId, "--output", $restorePath
    ) | Out-Null

    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    $restoreHash = (Get-FileHash -LiteralPath $restorePath -Algorithm SHA256).Hash
    if ($sourceHash -ne $restoreHash) {
        throw "CLI restore hash did not match the source file hash."
    }
}

if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    $SetupPath = Join-Path $PSScriptRoot "..\artifacts\release\$setupArtifactName"
}

if ([string]::IsNullOrWhiteSpace($ChecksumPath)) {
    $ChecksumPath = Join-Path $PSScriptRoot "..\artifacts\release\$checksumArtifactName"
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required to install and uninstall the FluxVault release package."
}

$SetupPath = Resolve-FullPath $SetupPath
$ChecksumPath = Resolve-FullPath $ChecksumPath
$SmokeRoot = Resolve-FullPath $SmokeRoot

Assert-FileExists -Path $SetupPath -Description $setupArtifactName
Assert-FileExists -Path $ChecksumPath -Description $checksumArtifactName
Assert-Checksum -SetupPath $SetupPath -ChecksumPath $ChecksumPath

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "FluxVaultService already exists. Aborting smoke install to avoid damaging an existing FluxVault installation."
}

if ((Test-Path -LiteralPath $programDataPath -PathType Container) -and -not $AllowExistingProgramData) {
    throw "ProgramData already exists at $programDataPath. Aborting clean release smoke validation. Move or back up this folder first, or rerun with -AllowExistingProgramData to perform a non-clean smoke that preserves existing ProgramData."
}

New-Item -ItemType Directory -Force -Path $SmokeRoot | Out-Null
$installLog = Join-Path $SmokeRoot "install.log"
$uninstallLog = Join-Path $SmokeRoot "uninstall.log"

$installedBySmoke = $false
try {
    Write-Host "Installing FluxVault release package..."
    Invoke-Bundle -Arguments @("-quiet", "-norestart") -LogPath $installLog
    $installedBySmoke = $true

    Wait-ServiceStatus -Status ([System.ServiceProcess.ServiceControllerStatus]::Running)
    Assert-ServiceDelayedAutoStart

    $programFilesRoot = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $installRoot = Join-Path $programFilesRoot "FluxVault"
    $appPath = Join-Path $installRoot "app\FluxVault.App.exe"
    $servicePath = Join-Path $installRoot "service\FluxVault.Service.exe"
    $cliPath = Join-Path $installRoot "cli\FluxVault.Cli.exe"

    Assert-InstalledFile -Path $appPath
    Assert-InstalledFile -Path $servicePath
    Assert-InstalledFile -Path $cliPath

    if (-not [System.Diagnostics.EventLog]::SourceExists($serviceName)) {
        throw "Application Event Log source $serviceName was not registered."
    }

    if (-not (Test-Path -LiteralPath $programDataPath -PathType Container)) {
        throw "$programDataPath was not created."
    }

    Invoke-CliSmoke -CliPath $cliPath
    Write-Host "FluxVault release install smoke checks passed."

    if ($UninstallAfter) {
        Write-Host "Uninstalling FluxVault release package..."
        Invoke-Bundle -Arguments @("-uninstall", "-quiet", "-norestart") -LogPath $uninstallLog
        Wait-ServiceDeleted

        if (-not (Test-Path -LiteralPath $programDataPath -PathType Container)) {
            throw "ProgramData was not preserved after uninstall: $programDataPath."
        }

        Write-Host "ProgramData preserved at $programDataPath."
        Write-Host "FluxVault release uninstall smoke checks passed."
        $installedBySmoke = $false
    }
    else {
        Write-Host "FluxVault remains installed. Re-run with -UninstallAfter to include uninstall validation."
    }
}
finally {
    if ($installedBySmoke -and $UninstallAfter) {
        Write-Warning "Smoke validation ended before uninstall completed. Attempting cleanup uninstall."
        try {
            Invoke-Bundle -Arguments @("-uninstall", "-quiet", "-norestart") -LogPath $uninstallLog
            Wait-ServiceDeleted
        }
        catch {
            Write-Warning "Cleanup uninstall failed: $($_.Exception.Message)"
        }
    }
}
