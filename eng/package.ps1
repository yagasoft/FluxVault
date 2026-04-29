param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\publish",
    [string]$PackageCertificatePath = "",
    [string]$PackageCertificatePassword = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root $OutputRoot
$statusNote = Join-Path $output "compact-menu-status.txt"

function Test-NativeMsBuild([string]$MsBuildPath) {
    if ([string]::IsNullOrWhiteSpace($MsBuildPath) -or -not (Test-Path -LiteralPath $MsBuildPath)) {
        return $false
    }

    $directory = Split-Path -Parent $MsBuildPath
    while ($directory) {
        if ((Split-Path -Leaf $directory) -ieq "MSBuild") {
            $cppDefaultProps = Join-Path $directory "Microsoft\VC\v170\Microsoft.Cpp.Default.props"
            return Test-Path -LiteralPath $cppDefaultProps
        }

        $parent = Split-Path -Parent $directory
        if ($parent -eq $directory) {
            break
        }

        $directory = $parent
    }

    return $false
}

function Resolve-NativeMsBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $paths = & $vswhere `
            -latest `
            -products * `
            -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -find "MSBuild\**\Bin\MSBuild.exe"
        foreach ($path in $paths) {
            if (Test-NativeMsBuild $path) {
                return $path
            }
        }
    }

    if ($env:MSBUILD_EXE_PATH -and (Test-NativeMsBuild $env:MSBUILD_EXE_PATH)) {
        return $env:MSBUILD_EXE_PATH
    }

    $msbuildCommand = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($msbuildCommand -and (Test-NativeMsBuild $msbuildCommand.Source)) {
        return $msbuildCommand.Source
    }

    return $null
}

function Resolve-WindowsSdkTool([string]$ToolName) {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $kitsRoot)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter $ToolName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\$([regex]::Escape($ToolName))$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Export-PackageLogoAssets([string]$DestinationAssetsPath) {
    New-Item -ItemType Directory -Force -Path $DestinationAssetsPath | Out-Null
    Add-Type -AssemblyName System.Drawing
    $iconPath = Join-Path $root "src\FluxVault.App\Assets\FluxVault.ico"
    $icon = [System.Drawing.Icon]::new($iconPath)
    try {
        foreach ($asset in @(
            @{ Name = "StoreLogo.png"; Size = 50 },
            @{ Name = "Square44x44Logo.png"; Size = 44 },
            @{ Name = "Square150x150Logo.png"; Size = 150 }
        )) {
            $bitmap = [System.Drawing.Bitmap]::new($asset.Size, $asset.Size)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.Clear([System.Drawing.Color]::Transparent)
                    $graphics.DrawIcon($icon, [System.Drawing.Rectangle]::new(0, 0, $asset.Size, $asset.Size))
                } finally {
                    $graphics.Dispose()
                }

                $bitmap.Save((Join-Path $DestinationAssetsPath $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $bitmap.Dispose()
            }
        }
    } finally {
        $icon.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
$status = [System.Collections.Generic.List[string]]::new()
$status.Add("FluxVault compact Explorer menu packaging status")
$status.Add("Generated: $([DateTimeOffset]::Now.ToString('u'))")

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

$shellExtensionProject = Join-Path $root "src\FluxVault.ExplorerCommand\FluxVault.ExplorerCommand.vcxproj"
$shellExtensionOutput = Join-Path $output "shell-extension"
New-Item -ItemType Directory -Force -Path $shellExtensionOutput | Out-Null
$msbuild = Resolve-NativeMsBuild
if ($msbuild) {
    & $msbuild $shellExtensionProject `
        /m `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:OutDir="$shellExtensionOutput\"
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $shellExtensionOutput "FluxVault.ExplorerCommand.dll"))) {
        $status.Add("Native shell extension: built FluxVault.ExplorerCommand.dll.")
    } else {
        $status.Add("Native shell extension: Visual C++ Build Tools were found, but the build failed. See MSBuild output.")
    }
} else {
    $status.Add("Native shell extension: Visual C++ Build Tools are missing; install Visual Studio 2022 Build Tools with Desktop development with C++, MSVC v143 x64/x86 tools, and Windows SDK to build FluxVault.ExplorerCommand.dll.")
}

$sparsePackageSource = Join-Path $root "installer\sparse-package"
$sparsePackageOutput = Join-Path $output "sparse-package"
if (Test-Path -LiteralPath $sparsePackageOutput) {
    Remove-Item -LiteralPath $sparsePackageOutput -Recurse -Force
}
Copy-Item -LiteralPath $sparsePackageSource -Destination $sparsePackageOutput -Recurse -Force
Export-PackageLogoAssets (Join-Path $sparsePackageOutput "Assets")
$status.Add("Sparse package manifest: copied installer\sparse-package\AppxManifest.xml.")

$makeAppx = Resolve-WindowsSdkTool "MakeAppx.exe"
$signTool = Resolve-WindowsSdkTool "SignTool.exe"
$msixPath = Join-Path $output "FluxVault.SparsePackage.msix"
if ($makeAppx) {
    & $makeAppx pack /overwrite /nv /d $sparsePackageOutput /p $msixPath | Out-Host
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $msixPath)) {
        $status.Add("Sparse package: created $msixPath with MakeAppx.exe.")
    } else {
        $status.Add("Sparse package: MakeAppx.exe was found, but package creation failed. See MakeAppx output.")
    }
} else {
    $status.Add("Sparse package: MakeAppx.exe was not found; install the Windows SDK to create the .msix file.")
}

if (-not (Test-Path -LiteralPath $msixPath)) {
    $status.Add("Sparse package signing: skipped because no .msix package was created.")
} elseif ($signTool -and $PackageCertificatePath) {
    $signArguments = @("sign", "/fd", "SHA256", "/f", $PackageCertificatePath)
    if ($PackageCertificatePassword) {
        $signArguments += @("/p", $PackageCertificatePassword)
    }

    $signArguments += $msixPath
    & $signTool @signArguments | Out-Host
    $status.Add("Sparse package signing: signed with SignTool.exe.")
} elseif ($signTool) {
    $status.Add("Sparse package signing: SignTool.exe was found, but no PackageCertificatePath was supplied.")
} else {
    $status.Add("Sparse package signing: SignTool.exe was not found; install the Windows SDK to sign release packages.")
}

Copy-Item -LiteralPath (Join-Path $root "eng\install-service.ps1") -Destination (Join-Path $output "install-service.ps1") -Force
Copy-Item -LiteralPath (Join-Path $root "eng\uninstall-service.ps1") -Destination (Join-Path $output "uninstall-service.ps1") -Force
Copy-Item -LiteralPath (Join-Path $root "installer\quickstart.md") -Destination (Join-Path $output "quickstart.md") -Force
Copy-Item -LiteralPath (Join-Path $root "installer\sample-config.json") -Destination (Join-Path $output "sample-config.json") -Force
$status | Set-Content -LiteralPath $statusNote -Encoding UTF8

Write-Host "Published FluxVault developer artefacts to $output"
Write-Host "Compact Explorer menu packaging status written to $statusNote"
