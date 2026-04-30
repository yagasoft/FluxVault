param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\release",
    [string]$PublishRoot = "artifacts\publish",
    [string]$PackageCertificatePath = "",
    [string]$PackageCertificatePassword = "",
    [switch]$RequireSigning
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publishRootFull = Join-Path $root $PublishRoot
$releaseRoot = Join-Path $root $OutputRoot
$ReleasePackageRoot = $releaseRoot

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if ($RequireSigning -and [string]::IsNullOrWhiteSpace($PackageCertificatePath)) {
    throw "PackageCertificatePath is required when -RequireSigning is supplied."
}

& (Join-Path $PSScriptRoot "package.ps1") `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -OutputRoot $PublishRoot `
    -PackageCertificatePath $PackageCertificatePath `
    -PackageCertificatePassword $PackageCertificatePassword

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRootFull "FluxVault.SparsePackage.msix") -Destination (Join-Path $releaseRoot "FluxVault.SparsePackage.msix") -Force

$installerProject = Join-Path $root "installer\wix\FluxVault.Installer\FluxVault.Installer.wixproj"
$bundleProject = Join-Path $root "installer\wix\FluxVault.Bundle\FluxVault.Bundle.wixproj"

Invoke-Checked {
    dotnet build $installerProject `
        --configuration $Configuration `
        /p:PublishRoot=$publishRootFull `
        /p:ReleasePackageRoot=$releaseRoot `
        /p:OutputPath=$releaseRoot\
} "FluxVault.Installer.wixproj build"

Invoke-Checked {
    dotnet build $bundleProject `
        --configuration $Configuration `
        /p:PublishRoot=$publishRootFull `
        /p:ReleasePackageRoot=$releaseRoot `
        /p:OutputPath=$releaseRoot\
} "FluxVault.Bundle.wixproj build"

$signTool = Get-ChildItem -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter "SignTool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\SignTool.exe$" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if ($PackageCertificatePath -and $signTool) {
    $signArguments = @("sign", "/fd", "SHA256", "/f", $PackageCertificatePath)
    if ($PackageCertificatePassword) {
        $signArguments += @("/p", $PackageCertificatePassword)
    }

    foreach ($artifact in @(
        (Join-Path $releaseRoot "FluxVault.Installer.msi"),
        (Join-Path $releaseRoot "FluxVault.Setup.exe"),
        (Join-Path $releaseRoot "FluxVault.SparsePackage.msix")
    )) {
        if (Test-Path -LiteralPath $artifact) {
            & $signTool @signArguments $artifact | Out-Host
        }
    }
} elseif ($RequireSigning) {
    throw "SignTool.exe was not found; install the Windows SDK to sign release packages."
}

@(
    "FluxVault release packaging status"
    "Generated: $([DateTimeOffset]::Now.ToString('u'))"
    "Publish root: $publishRootFull"
    "ReleasePackageRoot: $ReleasePackageRoot"
    "Installer project: FluxVault.Installer.wixproj"
    "Bundle project: FluxVault.Bundle.wixproj"
    "Sparse package: FluxVault.SparsePackage.msix"
    "Signing required: $($RequireSigning.IsPresent)"
) | Set-Content -LiteralPath (Join-Path $releaseRoot "release-package-status.txt") -Encoding UTF8

Write-Host "Published FluxVault release artefacts to $releaseRoot"
