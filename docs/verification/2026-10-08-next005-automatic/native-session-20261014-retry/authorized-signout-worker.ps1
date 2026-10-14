#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$InvocationSha256)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$signoutRunId='8ce7aacd21554ed7a4784227b75c280e'
$signoutRoot='C:\ProgramData\FluxVault.Tests\NEXT005Session\'+$signoutRunId
$signoutExecutable='C:\Program Files\PowerShell\7\pwsh.exe'
$signoutTaskName='FluxVault-NEXT005-Session-'+$signoutRunId+'-SignOut'
$signoutArguments='-NoProfile -NonInteractive -File "'+(Join-Path $signoutRoot 'authorized-signout-worker.ps1')+'" -InvocationSha256 '+$InvocationSha256
$signoutDescription='Authorised FluxVault native session sign-out '+$signoutRunId
$signoutTaskPin=$null;$signoutWorker=$null;$signoutSession=$null;$signoutObserver=$null
$signoutFailure=$null;$signoutCleanupFailure=$null;$signoutAccepted=$false;$signoutError=$null
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne 'S-1-5-18'){throw 'Sign-out submission requires the independent SYSTEM worker.'}
. (Join-Path $signoutRoot 'payload/eng/test-automatic-session-transition.ps1') -Mode Worker -RunId $signoutRunId
try{
    Assert-VaultFixtureTrustedPath (Join-Path $signoutRoot 'signout-task.json')
    $signoutTaskPin=Get-Content -LiteralPath (Join-Path $signoutRoot 'signout-task.json') -Raw | ConvertFrom-Json
    $signoutSelf=Get-Process -Id $PID
    try{
        if($signoutSelf.SessionId -ne 0){throw 'Sign-out worker must survive in session zero.'}
        $signoutWorker=Get-VaultFixtureProcessIdentity $signoutSelf
    }finally{$signoutSelf.Dispose()}
    $signoutWorker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $signoutRoot 'signout-worker.json')
    $signoutRegistered=Get-ScheduledTask -TaskName $signoutTaskName
    $signoutDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $signoutTaskName))))
    if($signoutTaskPin.DefinitionSha256 -cnotmatch '^[0-9A-F]{64}$' -or $signoutDigest -cne $signoutTaskPin.DefinitionSha256 -or
        $signoutRegistered.Actions.Execute -cne $signoutExecutable -or $signoutRegistered.Actions.Arguments -cne $signoutArguments -or
        $signoutRegistered.Description -cne $signoutDescription -or $signoutRegistered.Principal.UserId -notin @('SYSTEM','S-1-5-18')){throw 'Sign-out task identity changed.'}
    $signoutInvocationPath=Join-Path $signoutRoot 'signout-invocation.json'
    Assert-VaultFixtureTrustedPath $signoutInvocationPath
    if((Get-FileHash -LiteralPath $signoutInvocationPath).Hash -cne $InvocationSha256){throw 'Sign-out invocation pin changed.'}
    $signoutInvocation=Get-Content -LiteralPath $signoutInvocationPath -Raw | ConvertFrom-Json
    if($signoutInvocation.RunId -cne $signoutRunId -or $signoutInvocation.CreatorSid -cne 'S-1-5-21-136112424-624261118-1239521417-1001' -or
        $signoutInvocation.ObserverRequestSha256 -cne '2C70E627E581784C977E2A26BDE79700B6D3C39D5097E30D9F281E7DDEC6F51C' -or
        (Get-FileHash -LiteralPath $PSCommandPath).Hash -cne $signoutInvocation.WorkerScriptSha256){throw 'Sign-out operation identity changed.'}
    if((Get-FileHash -LiteralPath $requestPath).Hash -cne $signoutInvocation.ObserverRequestSha256){throw 'Observer request changed.'}
    $signoutRequest=Read-Request
    if($signoutRequest.Transition -cne 'ObserveSignOut' -or $signoutRequest.CreatorSid -cne $signoutInvocation.CreatorSid){throw 'Wrong observer transition or creator.'}
    foreach($entry in $signoutRequest.InstalledAssemblies){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.Sha256){throw 'Installed product changed.'}}
    $signoutObserverTask=Get-Content -LiteralPath (Join-Path $signoutRoot 'task.json') -Raw | ConvertFrom-Json
    if((Get-TaskDigest) -cne $signoutObserverTask.DefinitionSha256 -or $signoutObserverTask.RequestSha256 -cne $signoutInvocation.ObserverRequestSha256 -or
        (Get-ScheduledTask -TaskName $taskName).State -cne 'Running'){throw 'Observer task is not the pinned live task.'}
    Assert-VaultFixtureTrustedPath (Join-Path $signoutRoot 'worker.json')
    $signoutObserverIdentity=Get-Content -LiteralPath (Join-Path $signoutRoot 'worker.json') -Raw | ConvertFrom-Json
    $signoutObserver=Get-Process -Id $signoutObserverIdentity.ProcessId
    if($signoutObserver.StartTime.ToUniversalTime().ToString('o') -cne ([DateTimeOffset]$signoutObserverIdentity.StartedUtc).UtcDateTime.ToString('o') -or
        [FluxVault.Fixtures.WindowsProcessIdentity]::ImagePath($signoutObserver) -cne $signoutObserverIdentity.Executable -or $signoutObserver.HasExited){throw 'Observer process identity changed.'}
    $signoutReadyPath=Join-Path $fixtureRoot 'runtime/automatic-session-transition.jsonl'
    Assert-VaultFixtureTrustedPath $signoutReadyPath
    if((Get-Item -LiteralPath $signoutReadyPath).Length -gt 65536 -or (Get-FileHash -LiteralPath $signoutReadyPath).Hash -cne $signoutInvocation.ReadySha256){throw 'Ready record changed.'}
    $signoutEvents=@(Get-Content -LiteralPath $signoutReadyPath | ForEach-Object {$_ | ConvertFrom-Json})
    if($signoutEvents.Count -ne 1 -or $signoutEvents[0].Stage -cne 'Ready' -or $signoutEvents[0].FixtureId -cne $signoutRunId -or
        $signoutEvents[0].Detail.AbortRehearsal -or $signoutEvents[0].Detail.SessionId -ne $signoutInvocation.SessionId -or
        $signoutEvents[0].Detail.AuthenticationId -cne $signoutInvocation.AuthenticationId){throw 'Fresh exact Ready record is required.'}
    $signoutReadyUtc=[DateTimeOffset]$signoutEvents[0].ObservedUtc
    if([DateTimeOffset]::UtcNow -lt $signoutReadyUtc -or ([DateTimeOffset]::UtcNow-$signoutReadyUtc).TotalSeconds -gt 120 -or
        [DateTimeOffset]::UtcNow -gt [DateTimeOffset]$signoutInvocation.SubmitBeforeUtc){throw 'Sign-out submission deadline expired.'}
    foreach($marker in @((Join-Path $signoutRoot 'abort'),(Join-Path $signoutRoot 'result.json'),(Join-Path $fixtureRoot 'runtime/native-session-abort'))){if(Test-Path -LiteralPath $marker){throw 'Observer is aborting or complete.'}}
    $signoutBin=Join-Path $payload 'tests/FluxVault.TestHost/bin/Release/net10.0-windows'
    foreach($assembly in @('FluxVault.Abstractions.dll','FluxVault.Core.dll','FluxVault.Windows.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $signoutBin $assembly)) | Out-Null}
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FluxVaultAuthorisedSignOut { [DllImport("wtsapi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool WTSLogoffSession(IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait); }'
    $signoutProvider=[FluxVault.Windows.Security.WindowsAutomaticProtectionSessionProvider]::new()
    $signoutSession=$signoutProvider.AcquireAsync($signoutInvocation.CreatorSid,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if($null -eq $signoutSession -or $signoutSession.Caller.UserSid -cne $signoutInvocation.CreatorSid -or
        $signoutSession.SessionId -ne $signoutInvocation.SessionId -or $signoutSession.AuthenticationId -cne $signoutInvocation.AuthenticationId){throw 'Native target session generation changed.'}
    # Native setup can consume the submission window. Recheck liveness immediately before one-shot admission.
    if([DateTimeOffset]::UtcNow -lt $signoutReadyUtc -or ([DateTimeOffset]::UtcNow-$signoutReadyUtc).TotalSeconds -gt 120 -or
        [DateTimeOffset]::UtcNow -gt [DateTimeOffset]$signoutInvocation.SubmitBeforeUtc){throw 'Sign-out submission deadline expired after native setup.'}
    if($signoutObserver.HasExited -or (Get-ScheduledTask -TaskName $taskName).State -cne 'Running' -or
        (Get-TaskDigest) -cne $signoutObserverTask.DefinitionSha256 -or (Get-FileHash -LiteralPath $signoutReadyPath).Hash -cne $signoutInvocation.ReadySha256){throw 'Observer is no longer the pinned ready operation.'}
    foreach($marker in @((Join-Path $signoutRoot 'abort'),(Join-Path $signoutRoot 'result.json'),(Join-Path $fixtureRoot 'runtime/native-session-abort'))){if(Test-Path -LiteralPath $marker){throw 'Observer aborted or completed during native setup.'}}
    $signoutCall=@{RunId=$signoutRunId;CreatorSid=$signoutInvocation.CreatorSid;SessionId=$signoutSession.SessionId;AuthenticationId=$signoutSession.AuthenticationId;
        SubmissionIntentUtc=[DateTimeOffset]::UtcNow.ToString('o');InvocationSha256=$InvocationSha256;SingleUse=$true;AutomaticRetryPermitted=$false}
    $signoutBytes=[Text.Encoding]::UTF8.GetBytes(($signoutCall | ConvertTo-Json -Compress))
    $signoutIntent=[IO.File]::Open((Join-Path $signoutRoot 'signout-call.json'),'CreateNew','Write','Read')
    try{$signoutIntent.Write($signoutBytes);$signoutIntent.Flush($true)}finally{$signoutIntent.Dispose()}
    # Verify the exact native generation immediately before the session-ID-only API; no intervening IO or wait.
    $signoutSession.VerifyAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $signoutAccepted=[FluxVaultAuthorisedSignOut]::WTSLogoffSession([IntPtr]::Zero,[uint32]$signoutSession.SessionId,$false)
    $signoutError=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
    if(-not $signoutAccepted){throw ('Native sign-out submission failed: '+$signoutError)}
}catch{$signoutFailure=$_.Exception.Message}
finally{
    try{if($null -ne $signoutSession){$signoutSession.DisposeAsync().GetAwaiter().GetResult()};if($null -ne $signoutObserver){$signoutObserver.Dispose()}}catch{$signoutFailure='Token/process handle disposal failed: '+$_.Exception.Message}
    try{
        $signoutRegistered=Get-ScheduledTask -TaskName $signoutTaskName -ErrorAction SilentlyContinue
        if($null -ne $signoutRegistered){
            if($null -eq $signoutTaskPin -or $signoutTaskPin.DefinitionSha256 -cnotmatch '^[0-9A-F]{64}$'){throw 'No trusted sign-out task definition pin; removal refused.'}
            $signoutDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $signoutTaskName))))
            if($signoutDigest -cne $signoutTaskPin.DefinitionSha256 -or $signoutRegistered.Actions.Execute -cne $signoutExecutable -or
                $signoutRegistered.Actions.Arguments -cne $signoutArguments -or $signoutRegistered.Description -cne $signoutDescription -or
                $signoutRegistered.Principal.UserId -notin @('SYSTEM','S-1-5-18')){throw 'Sign-out task ownership changed; removal refused.'}
            Unregister-ScheduledTask -TaskName $signoutTaskName -Confirm:$false
        }
        if(Get-ScheduledTask -TaskName $signoutTaskName -ErrorAction SilentlyContinue){throw 'Sign-out task remains.'}
    }catch{$signoutCleanupFailure=$_.Exception.Message}
    @{RunId=$signoutRunId;WorkerProcess=$signoutWorker;Failure=$signoutFailure;CleanupFailure=$signoutCleanupFailure;SubmissionAccepted=$signoutAccepted;
        NativeSignOutVerified=$false;NativeError=$signoutError;TaskRemoved=(-not(Get-ScheduledTask -TaskName $signoutTaskName -ErrorAction SilentlyContinue));
        CompletedUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $signoutRoot 'signout-result.json')
}
if($signoutFailure -or $signoutCleanupFailure){throw ('Sign-out worker failed: '+$signoutFailure+' '+$signoutCleanupFailure)}