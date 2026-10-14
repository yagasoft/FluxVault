#Requires -Version 7.2
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$signoutRunId='f0d1d52eb0064d1fb9cda2913f76be89'
$signoutRoot='C:\ProgramData\FluxVault.Tests\NEXT005Session\'+$signoutRunId
. (Join-Path $signoutRoot 'payload/eng/test-automatic-session-transition.ps1') -Mode Start -RunId $signoutRunId
if($isSystem -or [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne 'S-1-5-21-136112424-624261118-1239521417-1001'){throw 'The authorised creator/operator must dispatch this single-use task.'}
$signoutTaskName=$taskName+'-SignOut'
$signoutWorkerPath=Join-Path $signoutRoot 'authorized-signout-worker.ps1'
$signoutWorkerHash='7B07373E2B96A6AEDF1CD943FD21B17BC292C4EC61A1DC445DF3AA461CB674C3'
Assert-VaultFixtureTrustedPath $signoutWorkerPath
if((Get-FileHash -LiteralPath $signoutWorkerPath).Hash -cne $signoutWorkerHash){throw 'Reviewed sign-out worker changed.'}
if((Get-FileHash -LiteralPath $requestPath).Hash -cne 'E0D5762315B0BEBE10B936ACF6C79C712A5CC469AB0B7D62E87F2AA01D555150'){throw 'Prepared observer request changed.'}
$signoutRequest=Read-Request
$signoutReadyPath=Join-Path $fixtureRoot 'runtime/automatic-session-transition.jsonl'
Assert-VaultFixtureTrustedPath $signoutReadyPath
$signoutEvents=@(Get-Content -LiteralPath $signoutReadyPath | ForEach-Object {$_ | ConvertFrom-Json})
if($signoutEvents.Count -ne 1 -or $signoutEvents[0].Stage -cne 'Ready' -or $signoutEvents[0].FixtureId -cne $signoutRunId -or $signoutEvents[0].Detail.AbortRehearsal){throw 'Actual-transition Ready is absent.'}
$signoutReady=$signoutEvents[0]
$signoutDeadline=([DateTimeOffset]$signoutReady.ObservedUtc).AddSeconds(110)
if([DateTimeOffset]::UtcNow -gt $signoutDeadline){throw 'Fresh Ready dispatch deadline expired.'}
foreach($file in @('signout-invocation.json','signout-call.json','signout-worker.json','signout-result.json')){if(Test-Path -LiteralPath (Join-Path $signoutRoot $file)){throw 'Single-use sign-out record collision.'}}
if(Get-ScheduledTask -TaskName $signoutTaskName -ErrorAction SilentlyContinue){throw 'Sign-out task collision.'}
$signoutInvocation=@{RunId=$signoutRunId;CreatorSid=$signoutRequest.CreatorSid;SessionId=$signoutReady.Detail.SessionId;AuthenticationId=$signoutReady.Detail.AuthenticationId;
    ReadySha256=(Get-FileHash -LiteralPath $signoutReadyPath).Hash;ObserverRequestSha256='E0D5762315B0BEBE10B936ACF6C79C712A5CC469AB0B7D62E87F2AA01D555150';
    WorkerScriptSha256=$signoutWorkerHash;SubmitBeforeUtc=$signoutDeadline.ToString('o');OperatorConsentSha256=(Get-FileHash -LiteralPath (Join-Path $signoutRoot 'operator-consent.json')).Hash}
$signoutInvocationPath=Join-Path $signoutRoot 'signout-invocation.json'
$signoutBytes=[Text.Encoding]::UTF8.GetBytes(($signoutInvocation | ConvertTo-Json))
$signoutOutput=[IO.File]::Open($signoutInvocationPath,'CreateNew','Write','None')
try{$signoutOutput.Write($signoutBytes);$signoutOutput.Flush($true)}finally{$signoutOutput.Dispose()}
$acl=Get-Acl -LiteralPath $signoutInvocationPath;$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'));Set-Acl -LiteralPath $signoutInvocationPath -AclObject $acl
$signoutInvocationHash=(Get-FileHash -LiteralPath $signoutInvocationPath).Hash
$signoutExe='C:\Program Files\PowerShell\7\pwsh.exe'
$signoutArguments='-NoProfile -NonInteractive -File "'+$signoutWorkerPath+'" -InvocationSha256 '+$signoutInvocationHash
$signoutAction=New-ScheduledTaskAction -Execute $signoutExe -Argument $signoutArguments
$signoutSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 120) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $signoutTaskName -Action $signoutAction -Settings $signoutSettings -User SYSTEM -RunLevel Highest -Description ('Authorised FluxVault native session sign-out '+$signoutRunId) | Out-Null
$signoutTaskPin=@{DefinitionSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $signoutTaskName))));
    InvocationSha256=$signoutInvocationHash;Executable=$signoutExe;Arguments=$signoutArguments;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$signoutTaskPin | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $signoutRoot 'signout-task.json')
$acl=Get-Acl -LiteralPath (Join-Path $signoutRoot 'signout-task.json');$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'));Set-Acl -LiteralPath (Join-Path $signoutRoot 'signout-task.json') -AclObject $acl
$self=Get-Process -Id $PID
try{Get-VaultFixtureProcessIdentity $self | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $signoutRoot 'signout-launcher.json')}finally{$self.Dispose()}
Start-ScheduledTask -TaskName $signoutTaskName
@{RunId=$signoutRunId;Task=$signoutTaskName;Started=$true;InvocationSha256=$signoutInvocationHash;SubmissionIsNotSignOutProof=$true;ObservedUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json