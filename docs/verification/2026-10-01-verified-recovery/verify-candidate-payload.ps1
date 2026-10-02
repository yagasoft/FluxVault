[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$PublishPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$InstallPath,
    [string]$WixPath = 'C:\Users\os008\.nuget\packages\wixtoolset.sdk\7.0.0\tools\net8.0\wix.dll'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$scratch = Join-Path $repo ('artifacts/payload-inspection-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $scratch 'work'))
& dotnet $WixPath burn extract $SetupPath -o (Join-Path $scratch 'bundle') -oba (Join-Path $scratch 'bootstrapper') -intermediateFolder (Join-Path $scratch 'work') -acceptEula wix7
if ($LASTEXITCODE -ne 0) { throw 'Burn extraction failed.' }
$msi = @(Get-ChildItem -LiteralPath (Join-Path $scratch 'bundle') -Recurse -File -Filter *.msi)
if ($msi.Count -ne 1) { throw 'Expected exactly one embedded MSI.' }
& dotnet $WixPath msi decompile $msi[0].FullName -x (Join-Path $scratch 'files') -o (Join-Path $scratch 'Package.wxs') -intermediateFolder (Join-Path $scratch 'work') -acceptEula wix7
if ($LASTEXITCODE -ne 0) { throw 'MSI extraction failed.' }
[xml]$package = Get-Content -LiteralPath (Join-Path $scratch 'Package.wxs') -Raw
$ns = [Xml.XmlNamespaceManager]::new($package.NameTable)
$ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
$payload = [ordered]@{}
foreach ($file in $package.SelectNodes('//w:File', $ns)) {
    $parts = [Collections.Generic.List[string]]::new()
    $parent = $file.ParentNode
    while ($parent -and $parent.GetAttribute('Id') -ne 'INSTALLFOLDER') {
        if ($parent.LocalName -eq 'Directory') { $parts.Insert(0, $parent.GetAttribute('Name')) }
        $parent = $parent.ParentNode
    }
    if (-not $parent) { continue }
    $relative = (($parts.ToArray() + $file.GetAttribute('Name')) -join '/')
    $published = Join-Path $PublishPath $relative
    $embedded = Join-Path $scratch ('files/File/' + $file.GetAttribute('Id'))
    if (-not (Test-Path -LiteralPath $published -PathType Leaf)) { throw "Missing published payload: $relative" }
    $hash = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne (Get-FileHash -LiteralPath $embedded -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Embedded mismatch: $relative" }
    if ($InstallPath) {
        $installed = Join-Path $InstallPath $relative
        if ($hash -ne (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Installed mismatch: $relative" }
    }
    $payload[$relative] = $hash
}
$publishedFiles = @('app','service','cli' | ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $PublishPath $_) -Recurse -File })
if ($payload.Count -ne $publishedFiles.Count) { throw 'Payload file coverage differs.' }
[pscustomobject]@{
    recorded_utc=[DateTimeOffset]::UtcNow.ToString('o'); setup=(Resolve-Path -LiteralPath $SetupPath).Path
    setup_sha256=(Get-FileHash -LiteralPath $SetupPath).Hash.ToLowerInvariant()
    embedded_msi_sha256=(Get-FileHash -LiteralPath $msi[0].FullName).Hash.ToLowerInvariant()
    product_version=$package.SelectSingleNode('//w:Package', $ns).GetAttribute('Version')
    extracted_root=$scratch; matching_payload_files=$payload.Count
    all_published_files_match_embedded_msi=$true; install_root=$InstallPath
    all_installed_files_match=([bool]$InstallPath); payload_file_sha256=$payload
    decompiler_limit='WIX1060 table reconstruction warnings are offline decompiler diagnostics, not build warnings. Installed service policy checked separately.'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Verified $($payload.Count) embedded/published payload files. Installed comparison: $([bool]$InstallPath)"
