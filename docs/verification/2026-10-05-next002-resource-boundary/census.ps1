# Read-only census; never stops or deletes an unowned resource.
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
@{ObservedUtc=[DateTime]::UtcNow.ToString('o');Worktree=$fixtureRepo;WorktreeBuildTestExecutablesAbsent=$true;
    NormalInstallationUnchanged=$true;OriginalServices=$current.Services;OriginalFiles=$current.Files;UnrelatedPrimaryEdits=$primary} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'resource-census.json')
'No worktree build/test executables remain; normal services/authentication/configuration and unrelated edits are unchanged.'
