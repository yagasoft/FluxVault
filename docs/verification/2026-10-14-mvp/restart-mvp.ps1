# Single reviewed Windows restart. Check is read-only; Start requires fresh human consent.
# RestoreSignIn is independent of boot acceptance and must run even when collection fails.
#Requires -Version 7.2
param([ValidateSet('Check','Start','RestoreSignIn')][string]$Mode='Check',
    [switch]$RebootAndTemporarySignInChangeConsentRecorded)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$runId='ae4b8f3536d043e0a688e1f4d2d4bc44'
$root='C:\ProgramData\FluxVault.Tests\MVPBoot\'+$runId
$operatorSid='S-1-5-21-136112424-624261118-1239521417-1001'
$candidateId='f97246fc79bc4308bbdb7361e9a38158'
$candidateHash='748E2E2C676B7C4544C5A8FF4D6922BAC432D4D6F67BB9CC5388786E4920D84F'
$subkey='SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$valueName='AutoAdminLogon'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $repository 'eng/fixtures/vault-windows-fixture.psm1') -Force
if($env:COMPUTERNAME -cne 'OS008-HOME'){throw 'This restart is bound to the reviewed staging PC.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{if($identity.User.Value -cne $operatorSid -or -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole('Administrators')){throw 'The elevated creator/operator is required.'}}finally{$identity.Dispose()}
Assert-VaultFixtureTrustedPath $root -AdditionalTrustedOwnerSid $operatorSid
$contextPath=Join-Path $root 'boot-context.json'
$context=Get-Content -LiteralPath $contextPath -Raw|ConvertFrom-Json
$contextHash=(Get-FileHash -LiteralPath $contextPath).Hash
if($context.RunId -cne $runId -or $context.CandidateId -cne $candidateId -or $context.CandidateSha256 -cne $candidateHash){throw 'Boot preparation identity changed.'}
$sealedBootScript=Join-Path $root 'payload/eng/test-mvp-boot.ps1'

function Write-NewReceipt([string]$Name,$Value){
    $stream=[IO.FileStream]::new((Join-Path $root $Name),'CreateNew','Write','None')
    try{$bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 8));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Read-SignInValue {
    $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($subkey,$false)
    try{return @{Kind=$key.GetValueKind($valueName).ToString();Value=$key.GetValue($valueName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}}finally{$key.Dispose()}
}
function Assert-Value($Observed,[string]$Expected){
    if($Observed.Kind -cne 'String' -or $Observed.Value -cne $Expected){throw 'The single reviewed sign-in value/type changed; do not overwrite it.'}
}
function Set-SignInValue([string]$Expected,[string]$NewValue){
    $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($subkey,$true)
    try{
        Assert-Value @{Kind=$key.GetValueKind($valueName).ToString();Value=$key.GetValue($valueName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)} $Expected
        $key.SetValue($valueName,$NewValue,[Microsoft.Win32.RegistryValueKind]::String);$key.Flush()
    }finally{$key.Dispose()}
    Assert-Value (Read-SignInValue) $NewValue
}
function Restore-SignIn {
    $intent=Get-Content -LiteralPath (Join-Path $root 'sign-in-change-intent.json') -Raw|ConvertFrom-Json
    if($intent.Machine -cne 'OS008-HOME' -or $intent.RunId -cne $runId -or $intent.ContextSha256 -cne $contextHash -or
        $intent.RegistrySubkey -cne $subkey -or $intent.ValueName -cne $valueName -or $intent.Kind -cne 'String' -or $intent.Before -cne '1' -or $intent.After -cne '0'){throw 'Sign-in restoration identity differs.'}
    $observed=Read-SignInValue
    if(Test-Path -LiteralPath (Join-Path $root 'sign-in-restored.json')){Assert-Value $observed '1';return}
    if($observed.Kind -ceq 'String' -and $observed.Value -ceq '0'){Set-SignInValue '0' '1'}else{Assert-Value $observed '1'}
    Write-NewReceipt 'sign-in-restored.json' @{RunId=$runId;ValueName=$valueName;Kind='String';Restored='1';VerifiedUtc=[DateTimeOffset]::UtcNow}
}
function Assert-Preparation {
    $boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime()
    if($boot -ne ([DateTimeOffset]$context.BootBefore).UtcDateTime){throw 'A different boot has already occurred; never resubmit.'}
    if((Get-FileHash -LiteralPath (Join-Path $root 'candidate.json')).Hash -cne $candidateHash){throw 'Candidate manifest changed.'}
    $candidate=Get-Content -LiteralPath (Join-Path $root 'candidate.json') -Raw|ConvertFrom-Json
    foreach($entry in $candidate.Payload){
        if($entry.RelativePath -cnotmatch '^(app|cli|service)\\[^:]+$' -or $entry.RelativePath.Split('\') -contains '..'){throw 'Unowned installed payload.'}
        if((Get-FileHash -LiteralPath (Join-Path 'C:\Program Files\FluxVault' $entry.RelativePath)).Hash -cne $entry.Sha256){throw 'Installed candidate changed.'}
    }
    foreach($entry in $context.Payload){
        if([IO.Path]::IsPathRooted($entry.RelativePath) -or $entry.RelativePath.Split([char[]]'\/') -contains '..'){throw 'Unowned observer payload.'}
        if((Get-FileHash -LiteralPath (Join-Path (Join-Path $root 'payload') $entry.RelativePath)).Hash -cne $entry.Sha256){throw 'Sealed observer changed.'}
    }
    $snapshot=Get-Content -LiteralPath (Join-Path $root 'snapshot.json') -Raw|ConvertFrom-Json
    $collected=Get-Content -LiteralPath (Join-Path $root 'rehearsal-collected.json') -Raw|ConvertFrom-Json
    if(-not $snapshot.ReadOnly -or -not $collected.TaskRemoved -or -not $collected.RecordedProcessesExited -or -not $collected.WorkerJobJoined){throw 'Completed rehearsal is required.'}
    if(Get-ScheduledTask -TaskName ('FluxVault-MVP-Boot-'+$runId) -ErrorAction SilentlyContinue){throw 'A startup observer already exists; do not reuse preparation.'}
    if(Get-Process -Name FluxVault.App -ErrorAction SilentlyContinue){throw 'Close FluxVault before the boot test.'}
    $service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
    if($service.State -cne 'Running' -or $service.StartMode -cne 'Auto' -or $service.StartName -cne 'LocalSystem' -or $service.PathName -cne '"C:\Program Files\FluxVault\service\FluxVault.Service.exe"'){throw 'The exact automatic service is required.'}
}
function Invoke-BootHelper([string]$Action,[int]$Seconds){
    if($Action -eq 'RestoreBaseline'){
        $userRoot=Join-Path 'C:\Users\os008\FluxVault Staging\NEXT005' $runId
        if(-not(Test-Path -LiteralPath (Join-Path $userRoot 'baseline.json'))){return}
        $phase=if(Test-Path -LiteralPath (Join-Path $userRoot 'baseline-restored.json')){'verify-baseline'}else{'restore-baseline'}
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'payload/tests/FluxVault.TestHost/bin/Release/net10.0-windows/FluxVault.TestHost.exe'))
        $arguments=@('--mode','installed-automatic-acceptance','--root',$userRoot,'--phase',$phase)
    }else{
        $start=[Diagnostics.ProcessStartInfo]::new('C:\Program Files\PowerShell\7\pwsh.exe')
        $arguments=@('-NoProfile','-NonInteractive','-File',$sealedBootScript,'-Mode',$Action,'-RunId',$runId)
        if($Action -eq 'Arm'){$arguments+='-RebootConsentRecorded'}
    }
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    $pin=$null;$stdout=$null;$stderr=$null;$timedOut=$false;$code=$null
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $pin=Get-VaultFixtureLiveProcessIdentity $process
        # Collect must not discover its own PID in its *-process.json join inventory.
        if($pin){Write-NewReceipt ('restart-'+$Action+'-owner.json') $pin}
        if(-not $process.WaitForExit($Seconds*1000)){$timedOut=$true;$process.Kill($true)}
    }
    finally{
        try{
            if(-not $process.HasExited){$process.Kill($true)}
            if(-not $process.WaitForExit(10000)){throw 'Owned restart preparation helper did not exit.'}
            $code=$process.ExitCode
            Write-NewReceipt ('restart-'+$Action+'-joined.json') @{Process=$pin;Joined=$true;ExitCode=$code;TimedOut=$timedOut;Output=$(if($stdout){$stdout.GetAwaiter().GetResult()}else{''});Error=$(if($stderr){$stderr.GetAwaiter().GetResult()}else{''});CompletedUtc=[DateTimeOffset]::UtcNow}
        }finally{$process.Dispose()}
    }
    if($timedOut -or $code -ne 0){throw 'Owned boot preparation/collection failed; inspect its retained receipt.'}
}
function Remove-UnreceiptedObserver {
    $name='FluxVault-MVP-Boot-'+$runId
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if(-not $task){return}
    $expectedArguments='-NoProfile -NonInteractive -File "'+$sealedBootScript+'" -Mode Worker -RunId '+$runId
    if($task.TaskPath -cne '\' -or $task.Description -cne ('Owned FluxVault MVP boot observer '+$runId) -or
        $task.Principal.UserId -cnotin @('SYSTEM','NT AUTHORITY\SYSTEM','S-1-5-18') -or $task.Principal.RunLevel -ne 1 -or
        @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -cne 'C:\Program Files\PowerShell\7\pwsh.exe' -or
        $task.Actions[0].Arguments -cne $expectedArguments -or $task.Actions[0].WorkingDirectory -or
        @($task.Triggers).Count -ne 1 -or $task.Triggers[0].CimClass.CimClassName -cne 'MSFT_TaskBootTrigger'){
        throw 'An unreceipted exact-name task differs from the owned observer; preserve and inspect it.'
    }
    if($task.State -in @('Running','Queued')){throw 'An unreceipted observer is active; establish and join its owned job before removal.'}
    $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $name))))
    Write-NewReceipt 'restart-unreceipted-task-removal-intent.json' @{Name=$name;DefinitionSha256=$digest;ObservedUtc=[DateTimeOffset]::UtcNow}
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'The owned unreceipted observer remains.'}
    Write-NewReceipt 'restart-unreceipted-task-removed.json' @{Name=$name;DefinitionSha256=$digest;RemovedUtc=[DateTimeOffset]::UtcNow}
}
function Join-RestartSubmissionProcess($Process){
    $naturalExit=$false
    try{
        $naturalExit=$Process.WaitForExit(10000)
        if(-not $naturalExit){$Process.Kill();if(-not $Process.WaitForExit(10000)){throw 'Restart submission process did not exit; its outcome is ambiguous.'}}
        return @{Exited=$true;NaturalExit=$naturalExit;ExitCode=$Process.ExitCode}
    }finally{$Process.Dispose()}
}
function Assert-RestorationActorsExited {
    $name='FluxVault-MVP-Boot-'+$runId
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if($task -and $task.State -in @('Running','Queued')){throw 'Observer task is still active; baseline restoration is refused.'}
    Import-VaultFixtureJobType
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open(('Global\FluxVault.NEXT002.'+$runId),'S-1-5-18')
    $jobJoined=$false
    try{if($job){$job.StopAndJoin();$jobJoined=$true}}finally{if($job){$job.Dispose()}}
    $pins=@(Get-ChildItem -LiteralPath $root -File | Where-Object {
        $_.Name -like '*-process.json' -or $_.Name -like 'restart-*-owner.json' -or
        $_.Name -match '^(boot|rehearsal)-(worker|tool-owner|tool-child)\.json$'
    })
    $timer=[Diagnostics.Stopwatch]::StartNew()
    foreach($file in $pins){
        $pin=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
        $process=Get-Process -Id $pin.ProcessId -ErrorAction SilentlyContinue
        try{
            if(-not $process){continue}
            # A reused PID proves the earlier process exited; never act on the replacement.
            if($process.StartTime.ToUniversalTime() -ne ([DateTimeOffset]$pin.StartedUtc).UtcDateTime){continue}
            if([FluxVault.Fixtures.WindowsProcessIdentity]::ImagePath($process) -ine $pin.Executable){throw 'Pinned actor identity differs; baseline restoration is refused.'}
            $remaining=30000-[int]$timer.ElapsedMilliseconds
            if($remaining -le 0 -or -not $process.WaitForExit($remaining)){throw 'A recorded preparation actor remains alive; baseline restoration is refused.'}
        }finally{if($process){$process.Dispose()}}
    }
    Write-NewReceipt ('restart-preparation-actors-joined-'+[guid]::NewGuid().ToString('N')+'.json') @{RecordedProcessesExited=$true;RecordedPinCount=$pins.Count;ObserverJobJoined=$jobJoined;CompletedUtc=[DateTimeOffset]::UtcNow}
}
function Repair-FailedRestart([bool]$SubmissionAttempted,$Submission){
    $definiteRefusal=$null -ne $Submission -and $Submission.NaturalExit -and $Submission.ExitCode -ne 0
    if($SubmissionAttempted -and -not $definiteRefusal){
        Write-NewReceipt 'restart-submission-uncertain.json' @{RunId=$runId;ObserverRetained=$true;SignInRemainsSuspended=$true;DoNotResubmit=$true;ProcessResult=$Submission;ObservedUtc=[DateTimeOffset]::UtcNow}
        return 'UncertainSubmission'
    }
    try{
        if(Test-Path -LiteralPath (Join-Path $root 'boot-task.json')){Invoke-BootHelper 'Collect' 180}
        else{Remove-UnreceiptedObserver}
    }finally{
        # Arm may have timed out during its own restoration, before any task receipt.
        # This independent probe also verifies an already restored baseline read-only.
        try{Assert-RestorationActorsExited;Invoke-BootHelper 'RestoreBaseline' 180}finally{Restore-SignIn}
    }
    return 'PreparationRolledBack'
}
if($Mode -eq 'RestoreSignIn'){Restore-SignIn;Write-Host 'Original automatic sign-in value restored and verified.';return}
Assert-Preparation
Assert-Value (Read-SignInValue) '1'
foreach($name in @('sign-in-change-intent.json','armed.json','boot-task.json','restart-submission-intent.json')){
    if(Test-Path -LiteralPath (Join-Path $root $name)){throw 'This one-use preparation already contains action evidence; inspect it instead of retrying.'}
}
if($Mode -eq 'Check'){Write-Host 'Read-only check passed: exact candidate, sealed observer, completed rehearsal and original sign-in setting. No restart is armed or submitted.';return}
if(-not $RebootAndTemporarySignInChangeConsentRecorded){throw 'Explicit consent to this reboot and temporary sign-in change is required.'}
Write-NewReceipt 'sign-in-change-intent.json' @{Machine=$env:COMPUTERNAME;RunId=$runId;ContextSha256=$contextHash;RegistrySubkey=$subkey;ValueName=$valueName;Kind='String';Before='1';After='0';ConsentRecorded=$true;PreparedUtc=[DateTimeOffset]::UtcNow}
$submissionAttempted=$false;$submission=$null
try{
    Set-SignInValue '1' '0'
    Write-NewReceipt 'sign-in-suspended.json' @{RunId=$runId;ValueName=$valueName;Kind='String';Verified='0';VerifiedUtc=[DateTimeOffset]::UtcNow}
    Invoke-BootHelper 'Arm' 450
    foreach($name in @('prepare-source-joined.json','capture-joined.json','resume-joined.json')){
        $receipt=Get-Content -LiteralPath (Join-Path $root $name) -Raw|ConvertFrom-Json
        if(-not $receipt.Joined -or $receipt.TimedOut -or $receipt.ExitCode -ne 0){throw 'Preparation helper did not settle successfully.'}
        if($receipt.Process){
            $process=Get-Process -Id $receipt.Process.ProcessId -ErrorAction SilentlyContinue
            try{if($process -and $process.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$receipt.Process.StartedUtc).UtcDateTime -and -not $process.WaitForExit(1000)){throw 'Preparation helper remains alive.'}}finally{if($process){$process.Dispose()}}
        }
    }
    $armed=Get-Content -LiteralPath (Join-Path $root 'armed.json') -Raw|ConvertFrom-Json
    if(-not $armed.ExplicitConsentRecorded -or $armed.RebootSubmitted -or ([DateTimeOffset]::UtcNow-[DateTimeOffset]$armed.PreparedUtc).TotalSeconds -gt 30){throw 'Arming is stale; do not submit a restart.'}
    Assert-Value (Read-SignInValue) '0'
    $task=Get-Content -LiteralPath (Join-Path $root 'boot-task.json') -Raw|ConvertFrom-Json
    if($task.Name -cne ('FluxVault-MVP-Boot-'+$runId) -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $task.Name)))) -cne $task.DefinitionSha256){throw 'Exact startup observer task changed.'}
    Write-NewReceipt 'restart-submission-intent.json' @{RunId=$runId;BootBefore=$context.BootBefore;Executable='C:\Windows\System32\shutdown.exe';Arguments=@('/r','/t','0');ForceCloseApplications=$false;PreparedUtc=[DateTimeOffset]::UtcNow}
    $start=[Diagnostics.ProcessStartInfo]::new('C:\Windows\System32\shutdown.exe');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    foreach($argument in @('/r','/t','0')){$start.ArgumentList.Add($argument)}
    # From the launch attempt onward, absence of a result is not proof of refusal.
    $submissionAttempted=$true
    $process=[Diagnostics.Process]::Start($start)
    try{
        $pin=Get-VaultFixtureLiveProcessIdentity $process
        if($pin){Write-NewReceipt 'restart-submission-process.json' $pin}
    }finally{$submission=Join-RestartSubmissionProcess $process}
    Write-NewReceipt 'restart-submission-result.json' @{Process=$pin;Result=$submission;CompletedUtc=[DateTimeOffset]::UtcNow}
    if(-not $submission.NaturalExit){throw 'Restart submission outcome is ambiguous; do not retry.'}
    if($submission.ExitCode -ne 0){throw 'Windows refused the restart submission.'}
    Write-Host 'Restart submitted once, without force-closing applications. Return after manual sign-in for collection and sign-in-setting restoration.'
}catch{
    $failure=$_
    $disposition=Repair-FailedRestart $submissionAttempted $submission
    if($disposition -eq 'UncertainSubmission'){
        throw [InvalidOperationException]::new('Restart may have been accepted. Keep the observer armed and automatic sign-in suspended; never resubmit. Collect and restore after manual sign-in, or after cancellation is independently established.', $failure.Exception)
    }
    throw $failure
}
