param(
    [string]$Configuration = "Release",
    [string]$ReleaseVersion = "v1.0.1",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\release",
    [string]$PublishRoot = "artifacts\publish"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publishRootFull = Join-Path $root $PublishRoot
$releaseRoot = Join-Path $root $OutputRoot
$stagingRoot = Join-Path $releaseRoot "_staging"
$ReleasePackageRoot = $stagingRoot

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

function ConvertTo-ProductVersion([string]$Version) {
    $normalized = Normalize-ReleaseVersion $Version
    return "$($normalized.Substring(1)).0"
}

$version = Normalize-ReleaseVersion $ReleaseVersion
$productVersion = ConvertTo-ProductVersion $version
$artifactPrefix = "Yagasoft-FluxVault-$version-$Runtime"
$setupArtifact = "$artifactPrefix-Setup.exe"
$checksumArtifact = "$artifactPrefix-checksums-sha256.txt"
$notesArtifact = "$artifactPrefix-release-notes.md"
$statusArtifact = "$artifactPrefix-release-status.txt"

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot "package.ps1") `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -OutputRoot $PublishRoot

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$installerProject = Join-Path $root "installer\wix\FluxVault.Installer\FluxVault.Installer.wixproj"
$bundleProject = Join-Path $root "installer\wix\FluxVault.Bundle\FluxVault.Bundle.wixproj"

Invoke-Checked {
    dotnet build $installerProject `
        --configuration $Configuration `
        /p:PublishRoot=$publishRootFull `
        /p:ProductVersion=$productVersion `
        /p:ReleasePackageRoot=$stagingRoot `
        /p:OutputPath=$stagingRoot\
} "FluxVault.Installer.wixproj build"

Invoke-Checked {
    dotnet build $bundleProject `
        --configuration $Configuration `
        /p:PublishRoot=$publishRootFull `
        /p:ProductVersion=$productVersion `
        /p:ReleasePackageRoot=$stagingRoot `
        /p:OutputPath=$stagingRoot\
} "FluxVault.Bundle.wixproj build"

$setupSource = Join-Path $stagingRoot "FluxVault.Setup.exe"
$setupDestination = Join-Path $releaseRoot $setupArtifact
Copy-Item -LiteralPath $setupSource -Destination $setupDestination -Force

$releaseNotesPath = Join-Path $releaseRoot $notesArtifact
@"
# FluxVault $version unsigned Windows installer

This release contains the unsigned Yagasoft FluxVault Windows installer.

## Install

Run ``$setupArtifact``. The installer sets up the FluxVault dashboard, Windows service, CLI, ProgramData folder, delayed service start, service recovery, and Application Event Log source.

## Explorer integration

The Options-managed full Explorer menu remains available under **Show more options** after registering Explorer integration from the FluxVault dashboard.

The Windows 11 compact Explorer context menu is not included in this unsigned consumer installer because compact-menu package identity requires signed MSIX packaging for a non-developer distribution path.

## Windows warning

This installer is unsigned. Windows will show Unknown publisher and may show SmartScreen warnings before installation.
"@ | Set-Content -LiteralPath $releaseNotesPath -Encoding UTF8

$statusPath = Join-Path $releaseRoot $statusArtifact
@(
    "Yagasoft FluxVault unsigned consumer release status"
    "Generated: $([DateTimeOffset]::Now.ToString('u'))"
    "Version: $version"
    "Product version: $productVersion"
    "Runtime: $Runtime"
    "Configuration: $Configuration"
    "Publish root: $publishRootFull"
    "ReleasePackageRoot: $ReleasePackageRoot"
    "Installer project: FluxVault.Installer.wixproj"
    "Bundle project: FluxVault.Bundle.wixproj"
    "Consumer setup: $setupArtifact"
    "Signing: not used for this unsigned consumer release profile"
    "Windows warning: Unknown publisher and SmartScreen warnings are expected"
    "Explorer full menu: available through Options-managed HKCU registration"
    "Windows 11 compact Explorer context menu is not included in this unsigned consumer installer"
) | Set-Content -LiteralPath $statusPath -Encoding UTF8

$checksumsPath = Join-Path $releaseRoot $checksumArtifact
Get-FileHash -Algorithm SHA256 -LiteralPath $setupDestination, $releaseNotesPath, $statusPath |
    ForEach-Object { "$($_.Hash)  $(Split-Path -Leaf $_.Path)" } |
    Set-Content -LiteralPath $checksumsPath -Encoding UTF8

Write-Host "Published FluxVault unsigned consumer release artefacts to $releaseRoot"
Write-Host "Consumer installer: $setupArtifact"
