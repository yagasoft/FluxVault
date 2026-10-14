# Prepares/observes one explicitly authorised real Windows restart. Never reboots
# Windows or starts/restarts FluxVault/PostgreSQL. Reboot submission is separate.
#Requires -Version 7.2
param([Parameter(Mandatory)][ValidateSet('Prepare','Rehearse','Arm','Worker','Collect')][string]$Mode,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$RunId,
    [ValidatePattern('^[0-9a-f]{32}$')][string]$CandidateId,
    [ValidatePattern('^[0-9A-F]{64}$')][string]$CandidateSha256,
    [switch]$SnapshotOnly,[switch]$RebootConsentRecorded,[switch]$FailBeforeChild,
    [ValidateSet('None','AfterTaskRemoval','AfterBaselineRestoration')][string]$CollectionInterruption='None')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$operatorSid='S-1-5-21-136112424-624261118-1239521417-1001'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$isSystem=$identity.User.Value -eq 'S-1-5-18'
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole('Administrators')){throw 'An elevated staging operator is required.'}
if(-not $isSystem -and $identity.User.Value -ne $operatorSid){throw 'Unexpected staging operator.'}
$identity.Dispose()
Import-Module (Join-Path $PSScriptRoot 'fixtures/vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$parent='C:\ProgramData\FluxVault.Tests\MVPBoot'
$root=Resolve-VaultFixtureRoot (Join-Path $parent $RunId) $parent $RunId
$payload=Join-Path $root 'payload'
$contextPath=Join-Path $root 'boot-context.json'
$userRoot=Join-Path 'C:\Users\os008\FluxVault Staging\NEXT005' $RunId
$taskName='FluxVault-MVP-Boot-'+$RunId
if($FailBeforeChild -and ($Mode -notin @('Rehearse','Worker') -or ($Mode -eq 'Worker' -and -not $SnapshotOnly))){throw 'Early-failure injection is confined to a read-only rehearsal.'}
if($CollectionInterruption -ne 'None' -and $Mode -notin @('Rehearse','Collect')){throw 'Collection interruption is confined to the collection boundaries.'}

function New-SystemDirectory([string]$Path){
    New-VaultFixtureProtectedDirectory $Path
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Write-NewJson([string]$Name,$Value){
    $stream=[IO.FileStream]::new((Join-Path $root $Name),'CreateNew','Write','None')
    try{$bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 12));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Get-BootUtc { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O') }
function Get-ServiceObservation {
    $service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
    if($service.StartMode -ne 'Auto' -or $service.StartName -ne 'LocalSystem' -or
        $service.PathName -cne '"C:\Program Files\FluxVault\service\FluxVault.Service.exe"'){throw 'Installed automatic service binding changed.'}
    $process=$null
    if($service.ProcessId){$process=Get-Process -Id $service.ProcessId}
    try{return @{Name=$service.Name;State=$service.State;StartMode=$service.StartMode;Process=$(if($process){Get-VaultFixtureProcessIdentity $process}else{$null});ObservedUtc=[DateTimeOffset]::UtcNow}}
    finally{if($process){$process.Dispose()}}
}
function Invoke-Joined([string]$Executable,[string[]]$Arguments,[int]$Seconds,[string]$Receipt){
    $start=[Diagnostics.ProcessStartInfo]::new($Executable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    $pin=Get-VaultFixtureLiveProcessIdentity $process
    if($pin){Write-NewJson ($Receipt.Replace('.json','-process.json')) $pin}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();$timeout=$false
    try{if(-not $process.WaitForExit($Seconds*1000)){$timeout=$true;$process.Kill($true)}}
    finally{
        if(-not $process.HasExited){$process.Kill($true)}
        if(-not $process.WaitForExit(10000)){throw 'Owned boot helper did not exit.'}
        $reply=@{Process=$pin;Joined=$true;TimedOut=$timeout;ExitCode=$process.ExitCode;Output=$stdout.GetAwaiter().GetResult();Error=$stderr.GetAwaiter().GetResult()}
        $process.Dispose();Write-NewJson $Receipt $reply
    }
    if($timeout -or $reply.ExitCode -ne 0){throw ('Owned boot helper failed; inspect '+$Receipt)}
}
function Assert-Payload($Context){
    foreach($entry in $Context.Payload){
        if($entry.RelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($entry.RelativePath)){throw 'Unowned payload entry.'}
        $path=Join-Path $payload $entry.RelativePath
        Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $operatorSid
        if((Get-FileHash -LiteralPath $path).Hash -ne $entry.Sha256){throw 'Sealed observer payload changed.'}
    }
    $candidate=Get-Content -LiteralPath (Join-Path $root 'candidate.json') -Raw|ConvertFrom-Json
    if((Get-FileHash -LiteralPath (Join-Path $root 'candidate.json')).Hash -ne $Context.CandidateSha256){throw 'Candidate pin changed.'}
    foreach($entry in $candidate.Payload){
        if($entry.RelativePath -cnotmatch '^(app|cli|service)\\[^:]+$' -or $entry.RelativePath.Split('\') -contains '..'){throw 'Unowned installed payload entry.'}
        if((Get-FileHash -LiteralPath (Join-Path 'C:\Program Files\FluxVault' $entry.RelativePath)).Hash -ne $entry.Sha256){throw 'Installed candidate payload changed.'}
    }
}
function Task-Digest([string]$Name){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $Name))))}
function Register-Observer([string]$Name,[bool]$Snapshot){
    if(Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue){throw 'Owned observer task collision.'}
    $args='-NoProfile -NonInteractive -File "'+(Join-Path $payload 'eng/test-mvp-boot.ps1')+'" -Mode Worker -RunId '+$RunId+$(if($Snapshot){' -SnapshotOnly'}else{''})
    if($FailBeforeChild){$args+=' -FailBeforeChild'}
    $action=New-ScheduledTaskAction -Execute 'C:\Program Files\PowerShell\7\pwsh.exe' -Argument $args
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 990) -MultipleInstances IgnoreNew
    $parameters=@{TaskName=$Name;Action=$action;Settings=$settings;User='SYSTEM';RunLevel='Highest';Description=('Owned FluxVault MVP boot observer '+$RunId)}
    if(-not $Snapshot){$parameters.Trigger=New-ScheduledTaskTrigger -AtStartup}
    Register-ScheduledTask @parameters | Out-Null
    Write-NewJson ($(if($Snapshot){'rehearsal-task.json'}else{'boot-task.json'})) @{Name=$Name;DefinitionSha256=(Task-Digest $Name)}
}

if($Mode -eq 'Prepare'){
    if($isSystem -or -not $CandidateId -or -not $CandidateSha256){throw 'Prepare requires the creator and reviewed candidate pin.'}
    if(Test-Path -LiteralPath $root){throw 'Use a fresh owned boot run; existing evidence is retained.'}
    if(-not(Test-Path -LiteralPath $parent)){New-SystemDirectory $parent}
    New-SystemDirectory $root
    $repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $hostRelative='tests/FluxVault.TestHost/bin/Release/net10.0-windows'
    $files=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'fixtures') -File)
    $files+=Get-Item -LiteralPath $PSCommandPath
    $files+=Get-ChildItem -LiteralPath (Join-Path $repository $hostRelative) -File -Recurse
    foreach($file in $files){
        Assert-VaultFixtureTrustedPath $file.FullName -AdditionalTrustedOwnerSid $operatorSid
        $target=Join-Path $payload ([IO.Path]::GetRelativePath($repository,$file.FullName))
        $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    $candidate=Join-Path 'C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f\retained-candidates' ($CandidateId+'\candidate.json')
    if((Get-FileHash -LiteralPath $candidate).Hash -ne $CandidateSha256){throw 'Reviewed candidate manifest differs.'}
    Copy-Item -LiteralPath $candidate -Destination (Join-Path $root 'candidate.json')
    $inventory=@(Get-ChildItem -LiteralPath $payload -File -Recurse|ForEach-Object {@{RelativePath=[IO.Path]::GetRelativePath($payload,$_.FullName);Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    $context=@{RunId=$RunId;CandidateId=$CandidateId;CandidateSha256=$CandidateSha256;BootBefore=(Get-BootUtc);Payload=$inventory;PreparedUtc=[DateTimeOffset]::UtcNow}
    Write-NewJson 'boot-context.json' $context;Assert-Payload $context
    Write-NewJson 'service-before.json' (Get-ServiceObservation)
    return
}
Assert-VaultFixtureTrustedPath $root -AdditionalTrustedOwnerSid $operatorSid
$context=Get-Content -LiteralPath $contextPath -Raw|ConvertFrom-Json
if($context.RunId -ne $RunId){throw 'Boot identity changed.'}
Assert-Payload $context
$testHost=Join-Path $payload 'tests/FluxVault.TestHost/bin/Release/net10.0-windows/FluxVault.TestHost.exe'
if($Mode -eq 'Arm'){
    if($isSystem -or -not $RebootConsentRecorded){throw 'Explicit consent for this concrete Windows restart is required before arming.'}
    if(([DateTimeOffset](Get-BootUtc)).UtcDateTime -ne ([DateTimeOffset]$context.BootBefore).UtcDateTime -or (Test-Path -LiteralPath $userRoot)){throw 'Boot/source preparation is stale or already used.'}
    if(-not(Test-Path -LiteralPath (Join-Path $root 'snapshot.json'))){throw 'The read-only SYSTEM rehearsal must pass before arming.'}
    try {
    Invoke-Joined $testHost @('--mode','installed-automatic-acceptance','--root',$userRoot,'--phase','prepare-boot') 135 'prepare-source-joined.json'
    Invoke-Joined $testHost @('--mode','installed-automatic-acceptance','--root',$userRoot,'--phase','capture') 135 'capture-joined.json'
    $captured=Get-Content -LiteralPath (Join-Path $userRoot 'captured.json') -Raw|ConvertFrom-Json
    Write-NewJson 'capture-request.json' @{RunId=$RunId;FirstVersion=$captured.FirstVersion;ExpectedSha256=(Get-FileHash -LiteralPath (Join-Path $userRoot 'expected-pending.bin')).Hash}
    # Set the protected request's owner explicitly for the SYSTEM reader.
    $acl=Get-Acl -LiteralPath (Join-Path $root 'capture-request.json');$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'));Set-Acl -LiteralPath (Join-Path $root 'capture-request.json') -AclObject $acl
    Register-Observer $taskName $false
    Invoke-Joined $testHost @('--mode','installed-automatic-acceptance','--root',$userRoot,'--phase','resume-for-boot') 30 'resume-joined.json'
    Write-NewJson 'armed.json' @{ExplicitConsentRecorded=$true;PreparedUtc=[DateTimeOffset]::UtcNow;RebootSubmitted=$false;Service=(Get-ServiceObservation)}
    } catch {
        Write-NewJson 'arm-failed.json' @{Error=$_.Exception.Message;ObservedUtc=[DateTimeOffset]::UtcNow;RebootSubmitted=$false}
        if(Test-Path -LiteralPath (Join-Path $root 'boot-task.json')){
            $task=Get-Content -LiteralPath (Join-Path $root 'boot-task.json') -Raw|ConvertFrom-Json
            if((Task-Digest $task.Name) -ne $task.DefinitionSha256){throw 'Failed arm left a changed task; preserve and inspect.'}
            Unregister-ScheduledTask -TaskName $task.Name -Confirm:$false
        }
        if(Test-Path -LiteralPath (Join-Path $userRoot 'baseline.json')){
            Invoke-Joined $testHost @('--mode','installed-automatic-acceptance','--root',$userRoot,'--phase','restore-baseline') 135 'failed-arm-baseline-joined.json'
        }
        throw
    }
    Write-Host 'Armed. All preparation helpers exited. Submit the separately authorised restart promptly; this script has not rebooted.'
    return
}
if($Mode -eq 'Worker'){
    if(-not $isSystem){throw 'Only the owned SYSTEM task may observe the boot.'}
    $kind=if($SnapshotOnly){'rehearsal'}else{'boot'}
    $self=Get-Process -Id $PID;try{Write-NewJson ($kind+'-worker.json') (Get-VaultFixtureProcessIdentity $self)}finally{$self.Dispose()}
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Create(('Global\FluxVault.NEXT002.'+$RunId),'S-1-5-18')
    try{
        $boot=Get-BootUtc
        if(-not $SnapshotOnly -and ([DateTimeOffset]$boot).UtcDateTime -eq ([DateTimeOffset]$context.BootBefore).UtcDateTime){throw 'A new Windows boot was not observed.'}
        Write-NewJson ($kind+'-service-start.json') @{BootUtc=$boot;Service=(Get-ServiceObservation);SnapshotOnly=[bool]$SnapshotOnly}
        if($FailBeforeChild){throw 'Deliberate read-only rehearsal failure before any child launch.'}
        $arguments=@('--mode','installed-boot-observe','--root',$root)
        if($SnapshotOnly){$arguments+=@('--snapshot','true')}
        $request=@{Executable=$testHost;Arguments=$arguments;Job=$job.Name;TimeoutSeconds=$(if($SnapshotOnly){45}else{910});StartsPostmaster=$false;InputText=$null;OwnerPath=(Join-Path $root ($kind+'-tool-owner.json'));ResultPath=(Join-Path $root ($kind+'-tool-result.json'))}
        Write-NewJson ($kind+'-tool-request.json') $request
        Invoke-Joined 'C:\Program Files\PowerShell\7\pwsh.exe' @('-NoProfile','-NonInteractive','-File',(Join-Path $payload 'eng/fixtures/invoke-owned-tool.ps1'),'-Request',(Join-Path $root ($kind+'-tool-request.json'))) ($request.TimeoutSeconds+20) ($kind+'-launcher-joined.json')
        $result=Get-Content -LiteralPath $request.ResultPath -Raw|ConvertFrom-Json
        if($result.ExitCode -ne 0){throw 'Boot observer did not pass; inspect its result.'}
        $settled=Get-ServiceObservation
        if($settled.State -ne 'Running' -or (-not $SnapshotOnly -and ([DateTimeOffset]$settled.Process.StartedUtc).UtcDateTime -lt ([DateTimeOffset]$boot).UtcDateTime)){throw 'The automatically started service was not verified after the new boot.'}
        Write-NewJson ($kind+'-service-end.json') $settled
    }finally{$job.StopAndJoin();$job.Dispose();Write-NewJson ($kind+'-job-joined.json') @{Joined=$true;CompletedUtc=[DateTimeOffset]::UtcNow}}
    return
}
if($Mode -eq 'Rehearse'){
    $name=$taskName+'-Rehearse';Register-Observer $name $true;Start-ScheduledTask -TaskName $name
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do{Start-Sleep -Milliseconds 250;$task=Get-ScheduledTask -TaskName $name;if($timer.Elapsed.TotalSeconds -gt 80){break}}while($task.State -in @('Running','Queued') -or -not(Test-Path -LiteralPath (Join-Path $root 'rehearsal-job-joined.json')))
}
if($Mode -in @('Rehearse','Collect')){
    # Collect -SnapshotOnly can finish a failed/interrupted rehearsal separately.
    $kind=if($Mode -eq 'Rehearse' -or $SnapshotOnly){'rehearsal'}else{'boot'}
    $task=Get-Content -LiteralPath (Join-Path $root ($kind+'-task.json')) -Raw|ConvertFrom-Json
    $expectedTask=$taskName+$(if($kind -eq 'rehearsal'){'-Rehearse'}else{''})
    if($task.Name -cne $expectedTask){throw 'Observer task name changed; preserve it.'}
    $existing=Get-ScheduledTask -TaskName $task.Name -ErrorAction SilentlyContinue
    if($existing -and (Task-Digest $task.Name) -ne $task.DefinitionSha256){throw 'Observer task definition changed; preserve it.'}
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open(('Global\FluxVault.NEXT002.'+$RunId),'S-1-5-18')
    $stopped=$false;$jobTerminated=$false
    try{
        if($existing -and $existing.State -in @('Running','Queued')){
            Stop-ScheduledTask -TaskName $task.Name;$stopped=$true
        }
        if($job){$job.StopAndJoin();$jobTerminated=$true}
    }finally{if($job){$job.Dispose()}}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($existing -and $existing.State -in @('Running','Queued')){
        if($timer.Elapsed.TotalSeconds -gt 30){throw 'The exact owned observer task did not stop; preserve its evidence.'}
        Start-Sleep -Milliseconds 100
        $existing=Get-ScheduledTask -TaskName $task.Name -ErrorAction SilentlyContinue
    }
    $pins=[Collections.Generic.List[object]]::new()
    foreach($suffix in @('-worker.json','-tool-owner.json','-tool-child.json')){
        $name=$kind+$suffix
        if(Test-Path -LiteralPath (Join-Path $root $name)){$pins.Add((Get-Content -LiteralPath (Join-Path $root $name) -Raw|ConvertFrom-Json))}
    }
    $launcherPath=Join-Path $root ($kind+'-launcher-joined.json')
    if(Test-Path -LiteralPath $launcherPath){
        $launcher=Get-Content -LiteralPath $launcherPath -Raw|ConvertFrom-Json
        if(-not $launcher.Joined){throw 'Recorded launcher was not joined.'}
        if($launcher.Process){$pins.Add($launcher.Process)}
    }
    foreach($file in (Get-ChildItem -LiteralPath $root -Filter '*-process.json' -File)){
        $pins.Add((Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json))
    }
    foreach($pin in $pins){
        $process=Get-Process -Id $pin.ProcessId -ErrorAction SilentlyContinue
        try{if($process -and $process.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$pin.StartedUtc).UtcDateTime -and -not $process.WaitForExit(10000)){throw 'A recorded boot helper remains alive.'}}
        finally{if($process){$process.Dispose()}}
    }
    # A worker may fail before creating its job or launching a child. Do not
    # require pins for launches that never occurred. Any remaining owned job
    # must be opened with its SYSTEM owner and drained before removing the task.
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open(('Global\FluxVault.NEXT002.'+$RunId),'S-1-5-18')
    try{if($job){$job.StopAndJoin();$jobTerminated=$true}}finally{if($job){$job.Dispose()}}
    $joinPath=Join-Path $root ($kind+'-job-joined.json')
    $workerJoined=if(Test-Path -LiteralPath $joinPath){(Get-Content -LiteralPath $joinPath -Raw|ConvertFrom-Json).Joined}else{$false}
    $requestExists=Test-Path -LiteralPath (Join-Path $root ($kind+'-tool-request.json'))
    $processJoinPath=Join-Path $root ($kind+'-processes-joined.json')
    $previousJoin=$false
    if(Test-Path -LiteralPath $processJoinPath){
        $prior=Get-Content -LiteralPath $processJoinPath -Raw|ConvertFrom-Json
        if($prior.TaskName -cne $task.Name -or -not $prior.RecordedProcessesExited){throw 'Earlier collection identity changed.'}
        $previousJoin=$prior.WorkerJobJoined -or $prior.JobJoinedByCollector
    }
    if($requestExists -and -not $workerJoined -and -not $jobTerminated -and -not $previousJoin){throw 'Child launch was requested but a job join cannot be established; retain the stopped task for investigation.'}
    if(-not(Test-Path -LiteralPath $processJoinPath)){
        Write-NewJson ($kind+'-processes-joined.json') @{TaskName=$task.Name;RecordedProcessesExited=$true;WorkerJobJoined=[bool]$workerJoined;JobJoinedByCollector=$jobTerminated;CompletedUtc=[DateTimeOffset]::UtcNow}
    }
    if($existing){Unregister-ScheduledTask -TaskName $task.Name -Confirm:$false}
    if(Get-ScheduledTask -TaskName $task.Name -ErrorAction SilentlyContinue){throw 'Owned observer task remains.'}
    if(-not(Test-Path -LiteralPath (Join-Path $root ($kind+'-task-removed.json')))){
        Write-NewJson ($kind+'-task-removed.json') @{Name=$task.Name;DefinitionSha256=$task.DefinitionSha256;ConfirmedAbsentUtc=[DateTimeOffset]::UtcNow}
    }
    if($CollectionInterruption -eq 'AfterTaskRemoval'){throw 'Injected collection interruption after exact task removal.'}
    $baselineRestored=$false
    if(Test-Path -LiteralPath (Join-Path $userRoot 'baseline.json')){
        # Resuming after a successful restore is read-only: validate the current
        # configuration/history against baseline instead of replaying mutations.
        $phase=if(Test-Path -LiteralPath (Join-Path $userRoot 'baseline-restored.json')){'verify-baseline'}else{'restore-baseline'}
        Invoke-Joined $testHost @('--mode','installed-automatic-acceptance','--root',$userRoot,'--phase',$phase) 135 ('baseline-'+$phase+'-joined-'+[guid]::NewGuid().ToString('N')+'.json')
        $baselineRestored=$true
    }
    if($CollectionInterruption -eq 'AfterBaselineRestoration'){if(-not $baselineRestored){throw 'Baseline restoration was not exercised.'};throw 'Injected collection interruption after verified baseline restoration.'}
    if(-not(Test-Path -LiteralPath (Join-Path $root ($kind+'-collected.json')))){
    Write-NewJson ($kind+'-collected.json') @{TaskRemoved=$true;RecordedProcessesExited=$true;RecordedProcessCount=$pins.Count;TaskStoppedByCollector=$stopped;
        WorkerJobJoined=[bool]$workerJoined;JobJoinedByCollector=$jobTerminated;ChildLaunchRequested=$requestExists;BaselineRestored=$baselineRestored;
        Acceptance=$(if($kind -eq 'rehearsal'){'Inspect snapshot.json; cleanup does not establish acceptance.'}else{'Inspect boot-capture-verified.json; cleanup does not establish acceptance.'});CollectedUtc=[DateTimeOffset]::UtcNow}
    }
    if($kind -eq 'rehearsal' -and -not(Test-Path -LiteralPath (Join-Path $root 'snapshot.json'))){throw 'Read-only boot rehearsal failed; its task and helpers have been collected.'}
}
