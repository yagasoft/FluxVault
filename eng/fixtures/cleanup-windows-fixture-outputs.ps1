#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$FixtureId)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
$parent='C:\ProgramData\FluxVault.Tests\NEXT002'
$resolved=Resolve-VaultFixtureRoot $Root $parent $FixtureId
Assert-VaultFixtureTrustedPath $resolved
$journal=Read-VaultFixtureJournal $resolved $parent $FixtureId
if($journal.State -ne 'Complete' -or @($journal.Resources | Where-Object {$_.State -notin @('Removed','Absent')}).Count){throw 'Output cleanup requires completed probe teardown.'}
foreach($account in @($journal.Resources | Where-Object {$_.Kind -eq 'Account'})) {
    if(Get-LocalUser -SID $account.Identity.Sid -ErrorAction SilentlyContinue){throw 'A probe account remains.'}
}
$ledgerPath=Join-Path $resolved 'output-cleanup-owner.json'
$taskName='FluxVault-NEXT002-261003-SYSTEM'
Import-VaultFixtureJobType
$job=$null
function Save-CleanupLedger {
    $temporary=Join-Path $resolved ('.output-cleanup-'+[guid]::NewGuid().ToString('N')+'.tmp')
    $bytes=[Text.Encoding]::UTF8.GetBytes(($ledger | ConvertTo-Json -Depth 4))
    try {
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {$stream.Write($bytes);$stream.Flush($true)} finally {$stream.Dispose()}
        [IO.File]::Move($temporary,$ledgerPath,(Test-Path -LiteralPath $ledgerPath))
    } finally {if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
}
function Assert-CleanupTask($task) {
    if($task.Description -ne $ledger.Description -or $task.Actions.Execute -ne $ledger.Executable -or
       $task.Actions.Arguments -ne $ledger.Arguments -or $task.Principal.UserId -notin @('SYSTEM','S-1-5-18')){throw 'Cleanup task collision.'}
    if($ledger.DefinitionSha256 -and (Get-TaskDigest) -ne $ledger.DefinitionSha256){throw 'Cleanup task definition changed.'}
}
function Get-TaskDigest {
    $bytes=[Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $taskName))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}
function Join-CleanupTask {
    $task=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if($null -eq $task){return}
    Assert-CleanupTask $task
    Stop-ScheduledTask -TaskName $taskName
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
        if([DateTime]::UtcNow -gt $deadline){throw 'Cleanup task did not exit.'};Start-Sleep -Milliseconds 100
    }
    $scheduler=$null;$folder=$null;$registered=$null;$instances=$null
    try {
        $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect();$folder=$scheduler.GetFolder('\');$registered=$folder.GetTask($taskName)
        do {
            $instances=$registered.GetInstances(0);$count=$instances.Count
            $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($instances);$instances=$null
            if(-not $count){break}
            if([DateTime]::UtcNow -gt $deadline){throw 'Cleanup scheduler instance remains.'};Start-Sleep -Milliseconds 100
        } while($true)
    } finally {
        foreach($reference in @($instances,$registered,$folder,$scheduler)){if($null -ne $reference -and [Runtime.InteropServices.Marshal]::IsComObject($reference)){$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($reference)}}
    }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Cleanup task remains.'}
}
if(Test-Path -LiteralPath $ledgerPath) {
    Assert-VaultFixtureTrustedPath $ledgerPath
    $ledger=Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json -AsHashtable
    if($ledger.FixtureId -ne $FixtureId -or $ledger.Root -ne $resolved -or
       $ledger.JournalSha256 -ne (Get-FileHash -LiteralPath (Join-Path $resolved 'owner.json')).Hash -or
       $ledger.KernelName -notmatch '^Global\\FluxVault\.NEXT002\.[0-9a-f]{32}$' -or
       $ledger.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Cleanup ledger identity changed.'}
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open($ledger.KernelName,$ledger.OwnerSid)
    try {if($null -ne $job){$job.StopAndJoin()};Join-CleanupTask}
    finally {if($null -ne $job){$job.Dispose();$job=$null}}
    $actorProcess=Join-Path $resolved 'output-cleanup-process.json'
    if(Test-Path -LiteralPath $actorProcess) {
        Assert-VaultFixtureTrustedPath $actorProcess
        $identity=Get-Content -LiteralPath $actorProcess -Raw | ConvertFrom-Json -AsHashtable
        $identity.StartedUtc=([DateTimeOffset]$identity.StartedUtc).UtcDateTime.ToString('o')
        Stop-VaultFixtureProcess $identity
    }
    if($ledger.State -eq 'Complete'){return}
} elseif(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Cleanup task name belongs to another resource.'}
if(@(Get-CimInstance Win32_Process | Where-Object {$_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.Contains($resolved)}).Count){throw 'Fixture is not quiescent.'}
if(-not(Test-Path -LiteralPath (Join-Path $resolved 'output-A')) -and -not(Test-Path -LiteralPath (Join-Path $resolved 'output-B'))){return}
$actorScript=Join-Path $resolved 'runtime/invoke-windows-output-cleanup.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Destination (Join-Path $resolved 'runtime/vault-windows-fixture.psm1')
Assert-VaultFixtureTrustedPath (Join-Path $resolved 'runtime/vault-windows-fixture.psm1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'invoke-windows-output-cleanup.ps1') -Destination $actorScript
Assert-VaultFixtureTrustedPath $actorScript
$executable=(Get-Command pwsh).Source;Assert-VaultFixtureTrustedPath $executable
$arguments='-NoProfile -NonInteractive -File "'+$actorScript+'" -Root "'+$resolved+'" -Ledger "'+$ledgerPath+'"'
$ledger=@{Version=1;FixtureId=$FixtureId;Root=$resolved;JournalSha256=(Get-FileHash -LiteralPath (Join-Path $resolved 'owner.json')).Hash;
    KernelName='Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N');OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
    Description='FluxVault output cleanup '+$FixtureId;Executable=$executable;Arguments=$arguments;DefinitionSha256='';InstanceId='';State='Prepared'}
Save-CleanupLedger
$result=Join-Path $resolved 'output-cleanup-result.json'
if(Test-Path -LiteralPath $result){Remove-Item -LiteralPath $result}
try {
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Create($ledger.KernelName,'S-1-5-18')
    $action=New-ScheduledTaskAction -Execute $executable -Argument $arguments
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 30) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User SYSTEM -RunLevel Highest -Description $ledger.Description | Out-Null
    $ledger.DefinitionSha256=Get-TaskDigest;Save-CleanupLedger
    $scheduler=$null;$folder=$null;$task=$null;$instance=$null
    try {
        $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect();$folder=$scheduler.GetFolder('\');$task=$folder.GetTask($taskName)
        $instance=$task.Run($null);$ledger.InstanceId=$instance.InstanceGuid;Save-CleanupLedger
        $deadline=[DateTime]::UtcNow.AddSeconds(25)
        while(-not(Test-Path -LiteralPath $result)) {if([DateTime]::UtcNow -gt $deadline){throw 'Output cleanup exceeded its deadline.'};Start-Sleep -Milliseconds 100}
        Assert-VaultFixtureTrustedPath $result
        $outcome=Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
        if(-not $outcome.Success){throw ('Owned output cleanup failed: '+$outcome.Error)}
    } finally {
        foreach($reference in @($instance,$task,$folder,$scheduler)){if($null -ne $reference -and [Runtime.InteropServices.Marshal]::IsComObject($reference)){$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($reference)}}
    }
} finally {
    try {if($null -ne $job){$job.StopAndJoin()};Join-CleanupTask}
    finally {if($null -ne $job){$job.Dispose()}}
}
$ledger.State='Complete';Save-CleanupLedger
