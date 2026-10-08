param([string]$EvidenceDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$evidenceRoot = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$worktreeRoot = (Resolve-Path -LiteralPath (Join-Path $evidenceRoot '../../..')).Path
$fixturePaths = @('native/0ee82f86c1404967b35dcb2910454df4','native/2b3ee620d8064a03a9537f36c1fb79af')
$owners = @($fixturePaths | ForEach-Object { Get-Content -LiteralPath (Join-Path $evidenceRoot "$_/completed-owner.json") -Raw | ConvertFrom-Json })
$identities = @($owners | ForEach-Object {
    $_.RunnerIdentity
    $_.Resources | Where-Object Kind -in 'Process','Postmaster' | ForEach-Object { $_.Identity }
}) + @($fixturePaths | ForEach-Object { Get-Content -LiteralPath (Join-Path $evidenceRoot "$_/output-cleanup-process.json") -Raw | ConvertFrom-Json })

$runs=@($fixturePaths | ForEach-Object { Get-Content -LiteralPath (Join-Path $evidenceRoot "$_/result.json") -Raw | ConvertFrom-Json })
if(@($runs | Where-Object Failure).Count){throw 'Native rehearsal did not pass.'}
foreach($run in $runs) {
    $replacement=@($run.Observations | Where-Object {$null -ne $_.PSObject.Properties['NativeRestartIdentity']})
    $joined=@($run.Observations | Where-Object {$null -ne $_.PSObject.Properties['NativeRestartServer']})
    if($replacement.Count -ne 1 -or $joined.Count -ne 1 -or -not $replacement[0].NativeRestartIdentity.OriginalJoinedBeforeReplacement -or
        -not $joined[0].BothServersJoined -or -not $joined[0].PostmasterUnchanged){throw 'Native process replacement/join evidence is missing.'}
    $original=$replacement[0].NativeRestartIdentity.Original
    $newServer=$replacement[0].NativeRestartIdentity.Replacement
    if($original.ProcessId -eq $newServer.ProcessId -and $original.StartedUtc -eq $newServer.StartedUtc){throw 'Native server identity was not replaced.'}
    $identities+=@($original,$newServer)
}
$unknownProof=@($runs.Observations | Where-Object {$null -ne $_.PSObject.Properties['NativeRestart']} |
    ForEach-Object {$_.NativeRestart.Results | Where-Object Kind -eq 'Restart'} |
    Where-Object {$_.Result.Phase -eq 'after' -and $_.Result.UnknownOutcomePreservedWithoutReplay -and $_.Result.Passed -eq 17})
if($unknownProof.Count -ne 1){throw 'The admitted unknown outcome restart/replay proof is missing.'}

$identities = @($identities | Sort-Object ProcessId,StartedUtc -Unique)
$liveOwned = @($identities | ForEach-Object {
    $identity = $_
    $process = Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        try {
            if ($process.StartTime.ToUniversalTime().Ticks -eq ([datetime]$identity.StartedUtc).ToUniversalTime().Ticks) { $identity }
        } finally { $process.Dispose() }
    }
})
$snapshots = @($fixturePaths | ForEach-Object {
    foreach ($name in 'before.json','after.json') { Get-Content -LiteralPath (Join-Path $evidenceRoot "$_/$name") -Raw | ConvertFrom-Json }
})
$baseline = $snapshots[0]
$services = @($baseline.Services | ForEach-Object {
    $expected = $_
    $service = Get-CimInstance Win32_Service -Filter "Name='$($expected.Name)'"
    $process = Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue
    try {
        $unchanged = $null -ne $process -and $service.ProcessId -eq $expected.Identity.ProcessId -and
            $process.StartTime.ToUniversalTime().Ticks -eq ([datetime]$expected.Identity.StartedUtc).ToUniversalTime().Ticks -and
            $service.PathName -eq $expected.PathName -and $service.StartName -eq $expected.StartName
    } finally { if ($process) { $process.Dispose() } }
    [ordered]@{ Name=$expected.Name; ProcessId=$service.ProcessId; StartedUtc=$expected.Identity.StartedUtc; Unchanged=$unchanged }
})
$files = @($baseline.Files | ForEach-Object {
    [ordered]@{ Path=$_.Path; Sha256=(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash; Unchanged=(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -eq $_.Sha256 }
})
$snapshotsConsistent = $true
foreach ($snapshot in $snapshots) {
    foreach ($expected in $baseline.Files) {
        $found = @($snapshot.Files | Where-Object Path -eq $expected.Path)
        if ($found.Count -ne 1 -or $found[0].Sha256 -ne $expected.Sha256) { $snapshotsConsistent = $false }
    }
    foreach ($expected in $baseline.Services) {
        $found = @($snapshot.Services | Where-Object Name -eq $expected.Name)
        if ($found.Count -ne 1 -or $found[0].Identity.ProcessId -ne $expected.Identity.ProcessId -or
            $found[0].Identity.StartedUtc -ne $expected.Identity.StartedUtc -or $found[0].PathName -ne $expected.PathName -or
            $found[0].StartName -ne $expected.StartName) { $snapshotsConsistent = $false }
    }
}
$primary = 'E:\Drive\Work (1)\Code\FluxVault'
$primaryUnchanged = (Get-FileHash -LiteralPath (Join-Path $primary 'scripts/fluxvault-index.json')).Hash -eq '9752DDF184A20EF4C574BB462083265C2FD3126153F73F8C2783F6A5FD1A5911' -and
    (Get-FileHash -LiteralPath (Join-Path $primary 'scripts/fluxvault-index.ps1')).Hash -eq '19BDE2B0593DA98696FC768F0DD0A68C08AE939D718B08B57891511E35F1CFE0'
$workers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(dotnet|MSBuild|testhost|vstest.console|VBCSCompiler|FluxVault.*)\.exe$' } | ForEach-Object {
    $matchesTask = ($_.ExecutablePath -and $_.ExecutablePath.Contains($worktreeRoot, [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.CommandLine -and $_.CommandLine.Contains($worktreeRoot, [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.ExecutablePath -and $_.ExecutablePath.Contains('C:\ProgramData\FluxVault.Tests\NEXT002\', [StringComparison]::OrdinalIgnoreCase))
    if ($_.ProcessId -notin @($baseline.Services.Identity.ProcessId)) {
        [ordered]@{ Name=$_.Name; ProcessId=$_.ProcessId; ParentProcessId=$_.ParentProcessId; StartedUtc=$_.CreationDate.ToUniversalTime().ToString('o'); TaskPathMatch=[bool]$matchesTask; Attribution='Ownership not established, preserved' }
    }
})
$intents = @()
$report = [ordered]@{
    CheckedUtc=[datetime]::UtcNow.ToString('o')
    AllFixtureRootsAbsent=@($owners | Where-Object { Test-Path -LiteralPath $_.Root }).Count -eq 0
    OwnershipResourcesRemoved=@($owners.Resources | Where-Object State -ne 'Removed').Count -eq 0
    OwnedProcessIdentitiesAbsent=$liveOwned.Count -eq 0
    CapturedProcessCount=$identities.Count
    BothNativeServersAbsent=$liveOwned.Count -eq 0
    DistinctNativeServerIdentities=$true
    PrivatePostmasterUnchangedDuringReplacement=$true
    AdmittedUnknownOutcomePreservedWithoutReplay=$true
    AccountsAbsent=@(Get-LocalUser | Where-Object Name -in 'FVGateA_261003','FVGateB_261003').Count -eq 0
    GroupAbsent=@(Get-LocalGroup | Where-Object Name -eq 'FVGate_261003').Count -eq 0
    TaskAbsent=@(Get-ScheduledTask | Where-Object TaskName -eq 'FluxVault-NEXT002-261003-SYSTEM').Count -eq 0
    TaskBuildTestAndCliProcessesAbsent=@($workers | Where-Object TaskPathMatch).Count -eq 0
    SnapshotsConsistent=$snapshotsConsistent
    Services=$services
    Files=$files
    UnrelatedPrimaryChangesPreserved=$primaryUnchanged
    IntegrityDatabaseIntentCount=$intents.Count
    IntegrityDatabaseIntentsRemoved=@($intents | Where-Object State -ne 'Removed').Count -eq 0
    RemainingOutOfScopeWorkers=$workers
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'process-census.json') -Encoding utf8
$failed = @($report.Keys | Where-Object { $report[$_] -is [bool] -and -not $report[$_] })
if (@($services | Where-Object { -not $_.Unchanged }).Count -or @($files | Where-Object { -not $_.Unchanged }).Count) { $failed += 'Installation unchanged' }
if ($failed.Count) { throw "Cleanup census failed: $($failed -join ', ')" }
[pscustomobject]$report | Select-Object CheckedUtc,CapturedProcessCount,IntegrityDatabaseIntentCount,OwnedProcessIdentitiesAbsent,TaskBuildTestAndCliProcessesAbsent | ConvertTo-Json
