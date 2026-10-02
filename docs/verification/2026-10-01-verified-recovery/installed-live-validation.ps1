[CmdletBinding()]
param([ValidateSet('Prepare','Complete')][string]$Phase = 'Prepare')

$ErrorActionPreference = 'Stop'
$statePath = Join-Path $PSScriptRoot 'installed-live-state.json'
$resultPath = Join-Path $PSScriptRoot 'installed-live-results.json'

function Invoke-StagingIpc([hashtable]$Request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'FluxVault.Service', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $writer = $null
    $reader = $null
    try {
        $pipe.Connect(2000)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $true, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine(($Request | ConvertTo-Json -Depth 64 -Compress))
        $response = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult()
        if ($null -eq $response) { throw 'Installed IPC closed without a response.' }
        return $response | ConvertFrom-Json
    } finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        $pipe.Dispose()
    }
}

function Assert-IpcSuccess($Response) {
    if (-not $Response.success) { throw "Installed IPC failed: $($Response.errorMessage)" }
}

function Assert-FileHash([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) { throw "Restored file is missing: $Destination" }
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant()
    $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $destinationHash) { throw "Restored hash differs: $Destination" }
    return [pscustomobject]@{Source=$Source; Destination=$Destination; Sha256=$sourceHash; Bytes=(Get-Item -LiteralPath $Source).Length; Match=$true}
}

if ($Phase -eq 'Prepare') {
    if (Test-Path -LiteralPath $statePath) { throw 'A staging validation state already exists; do not overwrite it.' }
    $status = Invoke-StagingIpc @{command=0}
    Assert-IpcSuccess $status
    $configuration = $status.status.configuration
    if ($configuration.repositoryPath -ne 'C:\ProgramData\FluxVault\repository' -or
        $configuration.metadataStore.databaseName -ne 'fluxvault_metadata' -or
        @($configuration.watchedFolders).Count -ne 0) { throw 'Unexpected installed staging target or pre-existing watched data.' }
    $scratch = Join-Path 'C:\ProgramData\FluxVault\staging-validation' ([guid]::NewGuid().ToString('N'))
    $working = Join-Path $scratch 'working'
    [void][IO.Directory]::CreateDirectory((Join-Path $working 'nested'))
    [void][IO.Directory]::CreateDirectory((Join-Path $scratch 'restored'))
    [IO.File]::WriteAllText((Join-Path $working 'sample.txt'), "FluxVault installed service validation.`r`nOnly generated staging content.`r`n", [Text.UTF8Encoding]::new($false))
    $bytes = [byte[]]::new(1024 * 1024)
    [Random]::new(20261001).NextBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $working 'nested/project.bin'), $bytes)
    $configuration | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath (Join-Path $scratch 'original-configuration.json') -Encoding utf8
    $configuration.watchedFolders = @([pscustomobject]@{id='staging-verification'; path=$working; recursive=$true; includePatterns=@('*'); excludePatterns=@(); compression=0; resourceProfile=2; isEnabled=$true})
    $state = [pscustomobject]@{Scratch=$scratch; Working=$working; NativeRestore=(Join-Path $scratch 'restored/native-file.txt'); ProfileId=$status.status.activeProfileId}
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    $save = Invoke-StagingIpc @{command=1; configuration=$configuration; profileId=$state.ProfileId}
    Assert-IpcSuccess $save
    $backup = Invoke-StagingIpc @{command=2}
    Assert-IpcSuccess $backup
    if (-not $backup.backup.success -or $backup.backup.failedFileCount -ne 0) { throw 'Installed backup reported a failure.' }
    $inventory = Invoke-StagingIpc @{command=3}
    Assert-IpcSuccess $inventory
    $file = @($inventory.versions | Where-Object { $_.sourcePath -eq (Join-Path $working 'sample.txt') -and -not $_.isDeleted }) | Select-Object -First 1
    $folder = @($inventory.versions | Where-Object { $_.sourcePath -eq $working -and -not $_.isDeleted }) | Select-Object -First 1
    if (-not $file -or -not $folder) { throw 'Installed capture did not produce visible file/folder versions.' }
    $fileRestore = Invoke-StagingIpc @{command=5; versionId=$file.versionId; outputPath=(Join-Path $scratch 'restored/ipc-file.txt')}
    Assert-IpcSuccess $fileRestore
    if ($fileRestore.restoreResult.restoredFileCount -ne 1) { throw 'Installed IPC did not report one verified file.' }
    $folderRestore = Invoke-StagingIpc @{command=5; versionId=$folder.versionId; outputPath=(Join-Path $scratch 'restored/ipc-folder')}
    Assert-IpcSuccess $folderRestore
    if ($folderRestore.restoreResult.restoredFileCount -ne 2) { throw 'Installed IPC did not report two verified folder files.' }
    $hashes = @(
        Assert-FileHash (Join-Path $working 'sample.txt') $fileRestore.outputPath
        Assert-FileHash (Join-Path $working 'sample.txt') (Join-Path $folderRestore.outputPath 'sample.txt')
        Assert-FileHash (Join-Path $working 'nested/project.bin') (Join-Path $folderRestore.outputPath 'nested/project.bin')
    )
    $sentinel = Join-Path $folderRestore.outputPath 'operator-sentinel.txt'
    [IO.File]::WriteAllText($sentinel, 'Existing staging destination must survive.')
    $repeat = Invoke-StagingIpc @{command=5; versionId=$folder.versionId; outputPath=$folderRestore.outputPath}
    if ($repeat.success -or [IO.File]::ReadAllText($sentinel) -ne 'Existing staging destination must survive.') { throw 'Installed folder repeat failed to preserve the destination.' }
    [void](Assert-FileHash (Join-Path $working 'sample.txt') (Join-Path $folderRestore.outputPath 'sample.txt'))
    $state | Add-Member -NotePropertyName FileVersionId -NotePropertyValue $file.versionId
    $state | Add-Member -NotePropertyName FolderVersionId -NotePropertyValue $folder.versionId
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    [pscustomobject]@{RecordedUtc=[DateTimeOffset]::UtcNow.ToString('o'); Interface='Installed FluxVault.Service named pipe and Windows service with PostgreSQL'; SourceRoot=$working; Backup=$backup.backup; FileVersionId=$file.versionId; FolderVersionId=$folder.versionId; VerifiedFileResult=$fileRestore.restoreResult; VerifiedFolderResult=$folderRestore.restoreResult; IndependentHashes=$hashes; ExistingFolderRefusedAndPreserved=$true; RepeatError=$repeat.errorMessage; NativeValidation='pending'; OriginalConfigurationRestored=$false} | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output "Installed service capture, visible versions, file/folder recovery and destination refusal passed. Native output: $($state.NativeRestore)"
} else {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $nativeHash = Assert-FileHash (Join-Path $state.Working 'sample.txt') $state.NativeRestore
    $original = Get-Content -LiteralPath (Join-Path $state.Scratch 'original-configuration.json') -Raw | ConvertFrom-Json
    $save = Invoke-StagingIpc @{command=1; configuration=$original; profileId=$state.ProfileId; purgeRemovedSelections=$false}
    Assert-IpcSuccess $save
    $status = Invoke-StagingIpc @{command=0}
    Assert-IpcSuccess $status
    if (@($status.status.configuration.watchedFolders).Count -ne 0 -or -not $status.status.isServiceRunning -or $status.status.metadataStore.lastError) { throw 'Installed service final state is not healthy and idle.' }
    $result.NativeValidation = 'Installed UI restore hash verified'
    $result.OriginalConfigurationRestored = $true
    $result | Add-Member -NotePropertyName NativeHash -NotePropertyValue $nativeHash -Force
    $result | Add-Member -NotePropertyName FinalMetadataStatus -NotePropertyValue $status.status.metadataStore -Force
    $result | Add-Member -NotePropertyName FinalWatchedFolderCount -NotePropertyValue 0 -Force
    $result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output 'Installed native restore verified; original configuration restored without history purge. Service remains healthy with no watched test folders.'
}
