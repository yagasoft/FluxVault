#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Ledger)
trap {
    $failure=$_
    try {
        $scriptRoot=Split-Path $PSScriptRoot -Parent;$parsed=[guid]::Empty
        if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18' -and
           [guid]::TryParseExact((Split-Path $scriptRoot -Leaf),'N',[ref]$parsed) -and
           $scriptRoot -eq (Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $parsed.ToString('N'))) {
            @{Success=$false;Error=$failure.Exception.Message;Location=$failure.ScriptStackTrace} | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $scriptRoot 'output-cleanup-result.json')
        }
    } catch { }
    exit 1
}
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
$parent='C:\ProgramData\FluxVault.Tests\NEXT002'
$fixtureId=Split-Path $Root -Leaf
$resolved=Resolve-VaultFixtureRoot $Root $parent $fixtureId
if($resolved -ne $Root -or $Ledger -ne (Join-Path $Root 'output-cleanup-owner.json') -or
   $PSScriptRoot -ne (Join-Path $resolved 'runtime') -or
   [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){throw 'Output cleanup location or identity changed.'}
# The protected root belongs to its elevated administrator, not to this SYSTEM actor.
# Trust that physical owner only after direct administrator membership is independently checked.
$rootOwner=(Get-Acl -LiteralPath $resolved).GetOwner([Security.Principal.SecurityIdentifier]).Value
Assert-VaultFixtureTrustedPath $resolved -AdditionalTrustedOwnerSid $rootOwner
Assert-VaultFixtureTrustedPath $Ledger -AdditionalTrustedOwnerSid $rootOwner
Assert-VaultFixtureTrustedPath $PSCommandPath -AdditionalTrustedOwnerSid $rootOwner
Assert-VaultFixtureTrustedPath (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -AdditionalTrustedOwnerSid $rootOwner
Assert-VaultFixtureTrustedPath (Join-Path $PSScriptRoot 'owned-windows-job.cs') -AdditionalTrustedOwnerSid $rootOwner
$ownership=Get-Content -LiteralPath $Ledger -Raw | ConvertFrom-Json -AsHashtable
if($ownership.OwnerSid -ne $rootOwner -or $ownership.FixtureId -ne $fixtureId -or
   $ownership.JournalSha256 -ne (Get-FileHash -LiteralPath (Join-Path $Root 'owner.json')).Hash -or
   [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18') {throw 'Output cleanup identity changed.'}
$journal=Read-VaultFixtureJournal $Root $parent $ownership.FixtureId
if($journal.State -ne 'Complete' -or @($journal.Resources | Where-Object {$_.State -notin @('Removed','Absent')}).Count) {throw 'Probe resources remain.'}
Import-VaultFixtureJobType
[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($ownership.KernelName)
$process=Get-Process -Id $PID
try {Get-VaultFixtureProcessIdentity $process | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Root 'output-cleanup-process.json')}
finally {$process.Dispose()}
$result=Join-Path $Root 'output-cleanup-result.json'
try {
    $removed=[Collections.Generic.List[string]]::new()
    foreach($name in @('output-A','output-B')) {
        $output=Join-Path $resolved $name
        if(-not(Test-Path -LiteralPath $output)){continue}
        $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($output)
        while($pending.Count) {
            $directory=$pending.Pop()
            if((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Output cleanup refuses a reparse entry.'}
            foreach($entry in Get-ChildItem -LiteralPath $directory -Force) {
                if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint -or -not $entry.FullName.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Output cleanup refuses an unexpected entry.'}
                if($entry.PSIsContainer){$pending.Push($entry.FullName)}
            }
        }
        # All probe users and jobs are gone. Remove directory entries, never alter file ACLs;
        # a hard-linked file can have another name outside this allowlisted output subtree.
        Remove-Item -LiteralPath $output -Recurse -Force
        if(Test-Path -LiteralPath $output){throw 'Owned output remains.'}
        $removed.Add($name)
    }
    @{Success=$true;Removed=@($removed)} | ConvertTo-Json | Set-Content -LiteralPath $result
} catch {
    @{Success=$false;Error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $result
    exit 1
}
