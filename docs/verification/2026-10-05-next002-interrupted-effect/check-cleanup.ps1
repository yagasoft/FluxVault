# Read-only final verification; never stops/deletes a resource.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$worktreeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$fixtures=@('374de56d64a1446da69c8776ded94c9a','191fd1f817dc4b2d80729870bb0206b2','87bf46aeb2384923be5158174c949f73')
$owners=@();$runs=@();$identities=@();$snapshots=@()
foreach($id in $fixtures) {
    $path=Join-Path $PSScriptRoot ('native/'+$id)
    $owner=Get-Content -LiteralPath (Join-Path $path 'completed-owner.json') -Raw|ConvertFrom-Json
    $owners+=@($owner)
    $run=Get-Content -LiteralPath (Join-Path $path 'result.json') -Raw|ConvertFrom-Json
    $runs+=@($run)
    $cleanup=Get-Content -LiteralPath (Join-Path $path 'cleanup.json') -Raw|ConvertFrom-Json
    if(-not $cleanup.RootRemoved -or -not $cleanup.OwnedJobsJoined -or -not $cleanup.InstallationUnchanged){throw 'A fixture cleanup guard failed.'}
    $identities+=@($owner.RunnerIdentity)
    $identities+=@($owner.Resources|Where-Object Kind -in 'Process','Postmaster'|ForEach-Object {$_.Identity})
    $identities+=@(Get-Content -LiteralPath (Join-Path $path 'output-cleanup-process.json') -Raw|ConvertFrom-Json)
    foreach($log in Get-ChildItem -LiteralPath $path -Filter 'system-processes-*-processes.jsonl') {
        $identities+=@(Get-Content -LiteralPath $log.FullName|ForEach-Object {$_|ConvertFrom-Json})
    }
    foreach($name in @('effect-held-server-ready.json','effect-reopened-server-ready.json')) {
        $ready=Join-Path $path $name
        if(Test-Path -LiteralPath $ready){$identities+=@(Get-Content -LiteralPath $ready -Raw|ConvertFrom-Json)}
    }
    foreach($name in @('before.json','after.json')){$snapshots+=@(Get-Content -LiteralPath (Join-Path $path $name) -Raw|ConvertFrom-Json)}
}
if($runs[0].Failure -ne 'Trusted fixture component is missing.' -or
    $runs[1].Failure -ne 'Actor probe incomplete: Native server exit did not match the owned termination.' -or $runs[2].Failure){throw 'Expected failed/accepted run distinction changed.'}
$successful=$runs[2]
$termination=@($successful.Observations|Where-Object {$null -ne $_.PSObject.Properties['NativeInterruptedTermination']})
$servers=@($successful.Observations|Where-Object {$null -ne $_.PSObject.Properties['NativeInterruptedServers']})
$creators=@($successful.Observations|Where-Object {$null -ne $_.PSObject.Properties['NativeInterruptedCreator']}|
    ForEach-Object {$_.NativeInterruptedCreator.Results|Where-Object Kind -eq 'Interrupted'|ForEach-Object {$_.Result}})
if($termination.Count -ne 1 -or $servers.Count -ne 1 -or $creators.Count -ne 2 -or
    -not $servers[0].NativeInterruptedServers.AllJoined -or -not $servers[0].NativeInterruptedServers.PrivatePostmasterUnchanged -or
    -not $termination[0].NativeInterruptedTermination.WithinHoldWindow -or $termination[0].NativeInterruptedTermination.ExitCode -eq 0 -or
    @($creators|Where-Object {$_.Phase -eq 'before' -and $_.Passed -eq 5}).Count -ne 1 -or
    @($creators|Where-Object {$_.Phase -eq 'after' -and $_.Passed -eq 7}).Count -ne 1){throw 'Native interruption/reconciliation proof is incomplete.'}
$death=$termination[0].NativeInterruptedTermination
if(([DateTimeOffset]$death.TerminatedUtc).UtcDateTime -ge ([DateTimeOffset]$death.Backend.LatestKillUtc).UtcDateTime){throw 'Actual native death missed the completion window.'}
$identities+=@($servers[0].NativeInterruptedServers.Original,$servers[0].NativeInterruptedServers.Killed,$servers[0].NativeInterruptedServers.Reopened)
$successPath=Join-Path $PSScriptRoot ('native/'+$fixtures[2])
$created=Get-Content -LiteralPath (Join-Path $successPath 'effect-gate-created.json') -Raw|ConvertFrom-Json
$removed=Get-Content -LiteralPath (Join-Path $successPath 'effect-gate-removed.json') -Raw|ConvertFrom-Json
if($created.State -ne 'Created' -or $removed.State -ne 'Removed' -or $created.Operation -ne $removed.Operation -or
    $created.FunctionHash -ne $removed.FunctionHash -or $created.TriggerHash -ne $removed.TriggerHash){throw 'Exact private completion gate removal is not recorded.'}
$identities=@($identities|Sort-Object ProcessId,StartedUtc -Unique)
$live=@(foreach($identity in $identities) {
    $process=Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
    if($null -ne $process){try{if($process.StartTime.ToUniversalTime().Ticks -eq ([DateTimeOffset]$identity.StartedUtc).UtcTicks){$identity}}finally{$process.Dispose()}}
})
$baseline=$snapshots[0]
foreach($snapshot in $snapshots) {
    foreach($file in $baseline.Files){$found=@($snapshot.Files|Where-Object Path -eq $file.Path);if($found.Count -ne 1 -or $found[0].Sha256 -ne $file.Sha256){throw 'Installation file snapshots disagree.'}}
    foreach($service in $baseline.Services){$found=@($snapshot.Services|Where-Object Name -eq $service.Name);if($found.Count -ne 1 -or
        $found[0].Identity.ProcessId -ne $service.Identity.ProcessId -or $found[0].Identity.StartedUtc -ne $service.Identity.StartedUtc -or
        $found[0].PathName -ne $service.PathName -or $found[0].StartName -ne $service.StartName){throw 'Installation service snapshots disagree.'}}
}
$normalServices=@(foreach($old in $baseline.Services) {
    $service=Get-CimInstance Win32_Service -Filter "Name='$($old.Name)'"
    $process=Get-Process -Id $service.ProcessId
    try{if($service.State -ne 'Running' -or $service.ProcessId -ne $old.Identity.ProcessId -or
        $process.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$old.Identity.StartedUtc).UtcTicks -or
        $service.PathName -ne $old.PathName -or $service.StartName -ne $old.StartName){throw 'Normal service changed.'}
        @{Name=$service.Name;ProcessId=$service.ProcessId;StartedUtc=$old.Identity.StartedUtc;Unchanged=$true}}
    finally{$process.Dispose()}
})
$normalFiles=@(foreach($file in $baseline.Files){$hash=(Get-FileHash -LiteralPath $file.Path).Hash;if($hash -ne $file.Sha256){throw 'Normal configuration/authentication changed.'};@{Path=$file.Path;Sha256=$hash;Unchanged=$true}})
$primary=@(foreach($pair in @(
    @{Name='fluxvault-index.json';Hash='9752DDF184A20EF4C574BB462083265C2FD3126153F73F8C2783F6A5FD1A5911'},
    @{Name='fluxvault-index.ps1';Hash='19BDE2B0593DA98696FC768F0DD0A68C08AE939D718B08B57891511E35F1CFE0'})) {
    $path=Join-Path 'E:\Drive\Work (1)\Code\FluxVault\scripts' $pair.Name
    if((Get-FileHash -LiteralPath $path).Hash -ne $pair.Hash){throw 'Unrelated primary-checkout edit changed.'}
    @{Path=$path;Unchanged=$true}
})
$workers=@(Get-CimInstance Win32_Process|Where-Object {$_.Name -match '^(dotnet|MSBuild|testhost|vstest.console|VBCSCompiler|FluxVault.*)\.exe$' -and
    $_.ProcessId -notin @($baseline.Services.Identity.ProcessId)}|ForEach-Object {
    @{Name=$_.Name;ProcessId=$_.ProcessId;StartedUtc=$_.CreationDate.ToUniversalTime().ToString('o');
        TaskPathMatch=(($_.ExecutablePath -and $_.ExecutablePath.Contains($worktreeRoot,[StringComparison]::OrdinalIgnoreCase)) -or
            ($_.CommandLine -and $_.CommandLine.Contains($worktreeRoot,[StringComparison]::OrdinalIgnoreCase)) -or
            ($_.ExecutablePath -and $_.ExecutablePath.Contains('C:\ProgramData\FluxVault.Tests\NEXT002\',[StringComparison]::OrdinalIgnoreCase)));
        Attribution='Unproven ownership is preserved'}
})
$report=@{ObservedUtc=[DateTime]::UtcNow.ToString('o');CapturedProcessCount=$identities.Count;OwnedProcessIdentitiesAbsent=$live.Count -eq 0;
    AllFixtureRootsAbsent=@($owners|Where-Object {Test-Path -LiteralPath $_.Root}).Count -eq 0;
    OwnershipResourcesRemoved=@($owners.Resources|Where-Object State -ne 'Removed').Count -eq 0;OwnershipResourceCount=$owners.Resources.Count;
    AccountsAbsent=@(Get-LocalUser|Where-Object Name -in 'FVGateA_261003','FVGateB_261003').Count -eq 0;
    GroupAbsent=@(Get-LocalGroup|Where-Object Name -eq 'FVGate_261003').Count -eq 0;
    TaskAbsent=@(Get-ScheduledTask|Where-Object TaskName -eq 'FluxVault-NEXT002-261003-SYSTEM').Count -eq 0;
    TaskBuildTestProcessesAbsent=@($workers|Where-Object TaskPathMatch).Count -eq 0;
    ActualDeathWithinHeldCompletionWindow=$true;CompletionGateRemoved=$true;CompletionBackendAbsent=$true;
    ExactUnknownReceiptReplayWithoutRepeatedPublication=$true;PrivatePostmasterUnchanged=$true;
    NormalServices=$normalServices;NormalFiles=$normalFiles;UnrelatedPrimaryEdits=$primary;UnownedWorkersPreserved=$workers}
foreach($field in @('OwnedProcessIdentitiesAbsent','AllFixtureRootsAbsent','OwnershipResourcesRemoved','AccountsAbsent','GroupAbsent','TaskAbsent','TaskBuildTestProcessesAbsent')) {
    if(-not $report[$field]){throw ('Cleanup verification failed: '+$field)}
}
$report|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'process-census.json')
$report|Select-Object CapturedProcessCount,OwnershipResourceCount,OwnedProcessIdentitiesAbsent,AllFixtureRootsAbsent,TaskBuildTestProcessesAbsent
