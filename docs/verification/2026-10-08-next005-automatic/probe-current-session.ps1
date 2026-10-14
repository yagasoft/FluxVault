#Requires -Version 7.2
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$RunId,
    [ValidatePattern('^[0-9a-f]{32}$')][string]$CleanupRunId)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $repository 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$root=Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT005' $RunId
if(Test-Path -LiteralPath $root){throw 'Session probe root collision.'}
New-VaultFixtureProtectedDirectory $root
$runtime=Join-Path $root 'runtime'
New-VaultFixtureProtectedDirectory $runtime
$operatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
foreach($name in @('invoke-owned-tool.ps1','vault-windows-fixture.psm1','owned-windows-job.cs')){
    $path=Join-Path $repository ('eng/fixtures/'+$name)
    Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $operatorSid
    Copy-Item -LiteralPath $path -Destination (Join-Path $runtime $name)
}
$hostSource=Join-Path $repository 'tests/FluxVault.TestHost/bin/Release/net10.0-windows'
Get-ChildItem -LiteralPath $hostSource -File | ForEach-Object {
    Assert-VaultFixtureTrustedPath $_.FullName -AdditionalTrustedOwnerSid $operatorSid
    Copy-Item -LiteralPath $_.FullName -Destination $runtime
}
$job=[FluxVault.Fixtures.OwnedWindowsJob]::Create(('Global\FluxVault.NEXT002.'+$RunId),'S-1-5-18')
$taskName='FluxVault-NEXT005-Session-'+$RunId
$created=$false;$taskHash=$null
try{
    $request=@{Executable=(Join-Path $runtime 'FluxVault.TestHost.exe');Arguments=@('--mode','windows-automatic-session','--creator-sid',$operatorSid,'--scratch',$root);
        Job=$job.Name;TimeoutSeconds=20;StartsPostmaster=$false;InputText=$null;
        OwnerPath=(Join-Path $root 'tool-owner.json');ResultPath=(Join-Path $root 'result.json')}
    if($CleanupRunId){$request.Arguments=@('--mode','windows-automatic-session-cleanup','--scratch',(Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT005' $CleanupRunId))}
    $requestPath=Join-Path $root 'request.json'
    $request|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $requestPath
    if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Session task collision.'}
    $arguments='-NoProfile -NonInteractive -File "'+(Join-Path $runtime 'invoke-owned-tool.ps1')+'" -Request "'+$requestPath+'"'
    $action=New-ScheduledTaskAction -Execute 'C:\Program Files\PowerShell\7\pwsh.exe' -Argument $arguments
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 45) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User SYSTEM -RunLevel Highest -Description ('Owned FluxVault native authority probe '+$RunId)|Out-Null
    $created=$true
    $taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $taskName))))
    Start-ScheduledTask -TaskName $taskName
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do{
        $task=Get-ScheduledTask -TaskName $taskName
        if($timer.Elapsed.TotalSeconds -gt 45){throw 'Session probe deadline exceeded.'}
        if($task.State -notin @('Running','Queued') -and (Test-Path -LiteralPath $request.ResultPath)){break}
        Start-Sleep -Milliseconds 100
    }while($true)
    $result=Get-Content -LiteralPath $request.ResultPath -Raw|ConvertFrom-Json
    $result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot ('session-'+$RunId+'.json'))
    $result|ConvertTo-Json -Depth 5
    if($CleanupRunId -and $result.ExitCode -eq 0){
        $cleanupTarget=[IO.Path]::GetFullPath((Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT005' $CleanupRunId))
        if($cleanupTarget -cne ('C:\ProgramData\FluxVault.Tests\NEXT005\'+$CleanupRunId)){throw 'Cleanup target changed.'}
        Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
        @{OwnedJobJoined=$true;TaskRemoved=$true;RootRemoved=(-not(Test-Path -LiteralPath $cleanupTarget));AclCleanupCorrected=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot ('session-'+$CleanupRunId+'-cleanup.json'))
    }
}finally{
    try{
        if($created){
            $actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $taskName))))
            if($actual -cne $taskHash){throw 'Session task ownership changed.'}
            if((Get-ScheduledTask $taskName).State -in @('Running','Queued')){Stop-ScheduledTask $taskName}
            $joined=[Diagnostics.Stopwatch]::StartNew()
            while((Get-ScheduledTask $taskName).State -in @('Running','Queued')){
                if($joined.Elapsed.TotalSeconds -gt 10){throw 'Session task did not exit.'}
                Start-Sleep -Milliseconds 100
            }
            Unregister-ScheduledTask $taskName -Confirm:$false
        }
    }finally{try{$job.StopAndJoin()}finally{$job.Dispose()}}
    $resolved=[IO.Path]::GetFullPath($root)
    if($resolved -cne ('C:\ProgramData\FluxVault.Tests\NEXT005\'+$RunId)){throw 'Session cleanup target changed.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
    @{OwnedJobJoined=$true;TaskRemoved=$created;RootRemoved=(-not(Test-Path -LiteralPath $root))}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot ('session-'+$RunId+'-cleanup.json'))
}
