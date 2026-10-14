# Runs the existing isolated database fixture under SYSTEM, like the native session
# harness. No normal database changes, source-account transition or reboot.
#Requires -Version 7.2
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$RunId)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'fixtures/vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$operatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole('Administrators')){throw 'The authorised staging administrator is required.'}
function Protect-SystemDirectory([string]$Path){
    Assert-VaultFixtureTrustedPath $Path -AdditionalTrustedOwnerSid $operatorSid
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false);$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
$parent='C:\ProgramData\FluxVault.Tests\MVPStartup'
if(-not(Test-Path -LiteralPath $parent)){New-VaultFixtureProtectedDirectory $parent}
Assert-VaultFixtureTrustedPath $parent -AdditionalTrustedOwnerSid $operatorSid
Protect-SystemDirectory $parent
$root=Resolve-VaultFixtureRoot (Join-Path $parent $RunId) $parent $RunId
New-VaultFixtureProtectedDirectory $root
Protect-SystemDirectory $root
$payload=Join-Path $root 'payload'
$hostRelative='tests/FluxVault.TestHost/bin/Release/net10.0-windows'
$reference='docs/verification/2026-10-03-next002-windows-fixture/live/4b7d6d4a3f6a46c3ad7fb15f3a2f3e66/postgresql-provenance.json'
foreach($directory in @('eng/fixtures',$hostRelative,([IO.Path]::GetDirectoryName($reference)))){
    $null=New-Item -ItemType Directory -Path (Join-Path $payload $directory) -Force
}
$files=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'fixtures') -File)
$files+=Get-Item -LiteralPath (Join-Path $PSScriptRoot 'test-windows-database-boundary.ps1'),(Join-Path $repository $reference)
$files+=Get-ChildItem -LiteralPath (Join-Path $repository $hostRelative) -Recurse -File
foreach($file in $files){
    Assert-VaultFixtureTrustedPath $file.FullName -AdditionalTrustedOwnerSid $operatorSid
    $relative=[IO.Path]::GetRelativePath($repository,$file.FullName)
    $target=Join-Path $payload $relative
    $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$inventory=@(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {@{RelativePath=[IO.Path]::GetRelativePath($payload,$_.FullName);Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$inventory|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $root 'payload.json') -Encoding utf8NoBOM
$job=[FluxVault.Fixtures.OwnedWindowsJob]::Create(('Global\FluxVault.NEXT002.'+$RunId),'S-1-5-18')
$taskName='FluxVault-MVP-Startup-'+$RunId
$created=$false;$taskHash=$null
try{
    $request=@{Executable='C:\Program Files\PowerShell\7\pwsh.exe';Arguments=@('-NoProfile','-NonInteractive','-File',(Join-Path $payload 'eng/test-windows-database-boundary.ps1'),
        '-RunAutomaticTests','-RunStartupRecoveryTests','-AutomaticCreatorSid',$operatorSid,'-FixtureId',$RunId,'-EvidenceDirectory',(Join-Path $root 'evidence'));
        Job=$job.Name;TimeoutSeconds=600;StartsPostmaster=$false;InputText=$null;OwnerPath=(Join-Path $root 'tool-owner.json');ResultPath=(Join-Path $root 'tool-result.json')}
    $requestPath=Join-Path $root 'request.json'
    $request|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $requestPath -Encoding utf8NoBOM
    if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Task collision.'}
    $arguments='-NoProfile -NonInteractive -File "'+(Join-Path $payload 'eng/fixtures/invoke-owned-tool.ps1')+'" -Request "'+$requestPath+'"'
    $action=New-ScheduledTaskAction -Execute $request.Executable -Argument $arguments
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 660) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User SYSTEM -RunLevel Highest -Description ('Owned FluxVault MVP startup fixture '+$RunId)|Out-Null
    $created=$true
    $taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $taskName))))
    Start-ScheduledTask -TaskName $taskName
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do{
        if($timer.Elapsed.TotalSeconds -gt 640){throw 'Owned startup fixture deadline exceeded.'}
        $task=Get-ScheduledTask -TaskName $taskName
        if($task.State -notin @('Running','Queued') -and (Test-Path -LiteralPath $request.ResultPath)){break}
        Start-Sleep -Milliseconds 200
    }while($true)
    $result=Get-Content -LiteralPath $request.ResultPath -Raw|ConvertFrom-Json
    $result|ConvertTo-Json -Depth 5
    if($result.ExitCode -ne 0){throw 'Native startup fixture failed; inspect its retained evidence.'}
}finally{
    try{
        if($created){
            $actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $taskName))))
            if($actual -cne $taskHash){throw 'Task ownership changed.'}
            if((Get-ScheduledTask $taskName).State -in @('Running','Queued')){Stop-ScheduledTask $taskName}
            $join=[Diagnostics.Stopwatch]::StartNew()
            while((Get-ScheduledTask $taskName).State -in @('Running','Queued')){if($join.Elapsed.TotalSeconds -gt 10){throw 'Owned task remains.'};Start-Sleep -Milliseconds 100}
            Unregister-ScheduledTask $taskName -Confirm:$false
        }
    }finally{try{$job.StopAndJoin()}finally{$job.Dispose()}}
    @{OwnedJobJoined=$true;TaskRemoved=$created;RootRetained=$root;CompletedUtc=[DateTimeOffset]::UtcNow}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'wrapper-cleanup.json') -Encoding utf8NoBOM
}
