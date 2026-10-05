# Read-only census; never stops or deletes an unowned resource.
param(
    [string]$FixtureId='72370762bcfb46f79b3e5f5f9e83d5bf',
    [string]$EvidenceDirectory=(Join-Path $PSScriptRoot ('native/'+$FixtureId)),
    [string]$OutputPath=(Join-Path $PSScriptRoot 'resource-census.json'),
    [switch]$AllowFailedRun)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$fixtureRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $fixtureRepo 'eng/fixtures/vault-windows-fixture.psm1') -Force
$workers=@(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -match '^(dotnet|testhost|FluxVault\.TestHost)\.exe$' -and $_.CommandLine -and
    $_.CommandLine.Contains($fixtureRepo,[StringComparison]::OrdinalIgnoreCase)
})
if($workers.Count){throw 'A worktree build/test executable remains.'}
$baseline=Get-Content -LiteralPath (Join-Path $fixtureRepo 'docs/verification/2026-10-05-next002-native-access/native/ccabb1b8008d492986e30a7fca83f2f8/before.json') -Raw | ConvertFrom-Json
$current=@{Services=@(foreach($old in $baseline.Services){
    $service=Get-CimInstance Win32_Service -Filter "Name='$($old.Name)'"
    if($service.State -ne 'Running'){throw 'An original service is no longer running.'}
    $process=Get-Process -Id $service.ProcessId
    try{@{Name=$service.Name;StartName=$service.StartName;PathName=$service.PathName;Identity=(Get-VaultFixtureProcessIdentity $process)}}
    finally{$process.Dispose()}
});Files=@(foreach($old in $baseline.Files){@{Path=$old.Path;Sha256=(Get-FileHash -LiteralPath $old.Path).Hash}})}
if(-not(Test-VaultFixtureInstallationUnchanged $baseline $current)){throw 'Normal installation changed.'}
$primary=@(foreach($pair in @(
    @{Name='fluxvault-index.json';Hash='9752DDF184A20EF4C574BB462083265C2FD3126153F73F8C2783F6A5FD1A5911'},
    @{Name='fluxvault-index.ps1';Hash='19BDE2B0593DA98696FC768F0DD0A68C08AE939D718B08B57891511E35F1CFE0'})){
    $path=Join-Path 'E:\Drive\Work (1)\Code\FluxVault\scripts' $pair.Name
    @{Path=$path;Unchanged=((Get-FileHash -LiteralPath $path).Hash -eq $pair.Hash)}
})
if(@($primary|Where-Object Unchanged -ne $true).Count){throw 'Unrelated primary-checkout edits changed.'}
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
$owner=Get-Content -LiteralPath (Join-Path $evidence 'completed-owner.json') -Raw | ConvertFrom-Json
$cleanup=Get-Content -LiteralPath (Join-Path $evidence 'cleanup.json') -Raw | ConvertFrom-Json
$run=Get-Content -LiteralPath (Join-Path $evidence 'result.json') -Raw | ConvertFrom-Json
if($owner.FixtureId -ne $fixtureId -or ($run.Failure -and -not $AllowFailedRun) -or -not $cleanup.OwnedJobsJoined -or -not $cleanup.RootRemoved -or
    -not $cleanup.InstallationUnchanged -or @($owner.Resources | Where-Object State -ne 'Removed').Count){throw 'Owned native run/teardown is incomplete.'}
$identities=@($owner.RunnerIdentity)+@($owner.Resources | Where-Object Kind -in 'Process','Postmaster' | ForEach-Object {$_.Identity})
foreach($file in Get-ChildItem -LiteralPath $evidence -Filter '*-child.json'){$identities+=@(Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)}
foreach($file in Get-ChildItem -LiteralPath $evidence -Filter '*-processes.jsonl'){$identities+=@(Get-Content -LiteralPath $file.FullName | ForEach-Object {$_ | ConvertFrom-Json})}
$identities+=@(Get-Content -LiteralPath (Join-Path $evidence 'output-cleanup-process.json') -Raw | ConvertFrom-Json)
$identities=@($identities | Sort-Object ProcessId,StartedUtc -Unique)
foreach($identity in $identities){
    $process=Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
    if($null -ne $process){try{if($process.StartTime.ToUniversalTime().Ticks -eq ([DateTimeOffset]$identity.StartedUtc).UtcTicks){throw 'An exact owned process remains.'}}finally{$process.Dispose()}}
}
$root=Resolve-VaultFixtureRoot -Root ('C:\ProgramData\FluxVault.Tests\NEXT002\'+$fixtureId) -Parent 'C:\ProgramData\FluxVault.Tests\NEXT002' -FixtureId $fixtureId
if(Test-Path -LiteralPath $root){throw 'Owned fixture root remains.'}
if(Get-LocalUser -Name 'FVGateA_261003' -ErrorAction SilentlyContinue){throw 'Owned A account remains.'}
if(Get-LocalUser -Name 'FVGateB_261003' -ErrorAction SilentlyContinue){throw 'Owned B account remains.'}
if(Get-LocalGroup -Name 'FVGate_261003' -ErrorAction SilentlyContinue){throw 'Owned group remains.'}
if(Get-ScheduledTask -TaskName 'FluxVault-NEXT002-261003-SYSTEM' -ErrorAction SilentlyContinue){throw 'Owned SYSTEM task remains.'}
@{ObservedUtc=[DateTime]::UtcNow.ToString('o');Worktree=$fixtureRepo;WorktreeBuildTestExecutablesAbsent=$true;
    NormalInstallationUnchanged=$true;OriginalServices=$current.Services;OriginalFiles=$current.Files;UnrelatedPrimaryEdits=$primary;
    OwnedFixture=$fixtureId;NativeRunAccepted=(-not [bool]$run.Failure);CapturedIdentitiesAbsent=$identities.Count;ResourcesRemoved=$owner.Resources.Count;
    OwnedRootAbsent=$true;OwnedAccountsGroupTaskAbsent=$true} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath
'No worktree build/test executables remain; normal services/authentication/configuration and unrelated edits are unchanged.'
