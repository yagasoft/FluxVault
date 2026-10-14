# Read-only sampling: sources are opened for read and never renamed, edited or reconfigured.
# Private source mappings and copied content must remain outside the repository.
#Requires -Version 7.2
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference = 'Stop'
$Source = [IO.Path]::GetFullPath($Source)
$RunRoot = [IO.Path]::GetFullPath($RunRoot)
$parent = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'FluxVault Staging/MVP'
if ([IO.Path]::GetDirectoryName($RunRoot) -ine $parent -or [IO.Path]::GetFileName($RunRoot) -notmatch '^[0-9a-f]{32}$') { throw 'An exact owned MVP run directory is required.' }
if (Test-Path -LiteralPath $RunRoot) { throw 'A fresh run directory is required.' }
for ($part=$RunRoot; $part; $part=[IO.Path]::GetDirectoryName($part)) {
    if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse staging path refused.' }
}
$extensions = @('.docx','.xlsx','.pptx','.pdf','.vsdx','.drawio','.mpp','.xlsm','.csv','.jsonl','.ndjson','.md','.txt','.msg','.png','.jpg','.svg','.mp4','.zip','.7z','.rar','.json','.vtt','.html')
$files = @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force | Where-Object {
    $_.Extension.ToLowerInvariant() -in $extensions -and $_.Length -gt 0 -and
    -not ([long]$_.Attributes -band 0x1000) -and -not ([long]$_.Attributes -band 0x400000) -and
    $_.Name -notlike '~$*' -and $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]'
})
$corpus = Join-Path $RunRoot 'corpus'
$null = New-Item -ItemType Directory -Path $corpus
$mapping = @(); $samples = @(); $number = 0
foreach ($group in ($files | Group-Object Extension | Sort-Object Name)) {
    $ordered = @($group.Group | Sort-Object Length,FullName)
    $selected = @($ordered[-1],$ordered[[int][Math]::Floor(($ordered.Count-1)/2)] | Sort-Object FullName -Unique)
    foreach ($file in $selected) {
        $number++
        $name = 'sample-{0:D3}{1}' -f $number,$file.Extension.ToLowerInvariant()
        $destination = Join-Path $corpus $name
        $before = Get-Item -LiteralPath $file.FullName -Force
        $inputStream = [IO.FileStream]::new($file.FullName,'Open','Read','Read')
        try {
            $outputStream = [IO.FileStream]::new($destination,'CreateNew','Write','None')
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
            $inputStream.Position = 0
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream))
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $digest) { throw 'Copied content digest differs.' }
            $after = Get-Item -LiteralPath $file.FullName -Force
            if ($after.Length -ne $before.Length -or $after.LastWriteTimeUtc -ne $before.LastWriteTimeUtc) { throw 'Source changed while being sampled.' }
            $folder = if ($file.Extension -in @('.mp4','.zip','.7z','.rar')) { 'media' } elseif ($file.Extension -in @('.csv','.jsonl','.ndjson','.json','.txt','.md','.vtt','.html')) { 'data' } else { 'documents' }
            $samples += @{ Name=$name; Folder=$folder; Length=$before.Length; Sha256=$digest }
            $mapping += @{ Sample=$name; Source=$file.FullName; Length=$before.Length; Sha256=$digest; LastWriteUtc=$before.LastWriteTimeUtc.ToString('o'); Attributes=[long]$before.Attributes }
        } finally { $inputStream.Dispose() }
    }
}
$samples | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunRoot 'corpus.json') -Encoding utf8NoBOM
$mapping | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunRoot 'source-map.private.json') -Encoding utf8NoBOM
@{ RunRoot=$RunRoot; Samples=$samples.Count; Bytes=($samples | Measure-Object Length -Sum).Sum; MaximumBytes=($samples | Measure-Object Length -Maximum).Maximum; Extensions=@($samples | ForEach-Object {[IO.Path]::GetExtension($_.Name)} | Sort-Object -Unique); SourcesOpenedReadOnly=$true } | ConvertTo-Json -Depth 5
