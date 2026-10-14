#Requires -Version 7.2
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$nativeRoot='C:\ProgramData\FluxVault.Tests\NEXT005Session\dcae496cfd194462af01839b12cf08af'
. (Join-Path $nativeRoot 'payload/eng/test-automatic-session-transition.ps1') -Mode Collect -RunId dcae496cfd194462af01839b12cf08af
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $nativeRoot 'start-authorized-signout.ps1'),[ref]$tokens,[ref]$errors)
$dispatch=$ast.Find({param($node)$node -is [Management.Automation.Language.TryStatementAst] -and $node.Body.Extent.Text.Contains('Register-ScheduledTask')},$true).Extent.Text
function Register-ScheduledTask {param($TaskName,$Action,$Settings,$User,$RunLevel,$Description)
    $script:registered=$true;if($script:scenario -like 'registration-*'){throw 'Injected uncertain registration acknowledgement.'}}
function Export-ScheduledTask {param($TaskName) if($script:changed){'changed definition'}else{'pinned definition'}}
function Get-ScheduledTask {param($TaskName,$ErrorAction)
    if(-not $script:registered){return}
    [pscustomobject]@{State=$(if($script:scenario -in @('start-ack','registration-running')){'Running'}else{'Ready'});
        Actions=[pscustomobject]@{Execute=$signoutExe;Arguments=$signoutArguments};Description=('Authorised FluxVault native session sign-out '+$signoutRunId);
        Principal=[pscustomobject]@{UserId='SYSTEM'}}}
function Start-ScheduledTask {param($TaskName) throw 'Injected uncertain start acknowledgement.'}
function Unregister-ScheduledTask {param($TaskName,[switch]$Confirm) $script:registered=$false;$script:removed=$true}
function Set-Content {param($LiteralPath,[Parameter(ValueFromPipeline)]$Value)
    process{
        if($LiteralPath.EndsWith('signout-task.json') -and $script:scenario -in @('pin-io','changed-pin')){
            $script:changed=$script:scenario -eq 'changed-pin';throw 'Injected pin write failure.'}
        if($LiteralPath.EndsWith('\abort')){$script:aborted=$true}
        if($LiteralPath.EndsWith('signout-dispatch-failure.json')){$script:receipt=[string]$Value}
    }}
function Get-Acl {param($LiteralPath) [Security.AccessControl.FileSecurity]::new()}
function Set-Acl {param($LiteralPath,$AclObject)}
$checks=@()
foreach($scenario in @('pin-io','start-ack','changed-pin','registration-ready','registration-running')){
    $script:scenario=$scenario;$script:registered=$false;$script:removed=$false;$script:changed=$false;$script:aborted=$false;$script:receipt=$null
    $signoutRoot=$nativeRoot;$signoutRunId='dcae496cfd194462af01839b12cf08af';$signoutTaskName='isolated mock task'
    $signoutAction=$null;$signoutSettings=$null;$signoutExe='pinned executable';$signoutArguments='pinned arguments';$signoutInvocationHash='A'*64
    $signoutRegistrationAttempted=$false;$signoutCreated=$false;$signoutStartAttempted=$false;$signoutCollectRequired=$false;$signoutTaskPin=$null
    $caught=$false;try{Invoke-Expression $dispatch}catch{$caught=$true}
    if(-not $caught -or $null -eq $script:receipt){throw 'Failure receipt was not retained.'}
    $result=$script:receipt | ConvertFrom-Json
    if($script:removed -ne ($scenario -eq 'pin-io')){throw 'Unexpected task removal.'}
    if($script:aborted -ne ($scenario -in @('start-ack','registration-running'))){throw 'Unexpected abort admission.'}
    if($result.CollectAndJoinRequired -ne ($scenario -in @('start-ack','registration-running'))){throw 'Incorrect collection requirement.'}
    if([bool]$result.CleanupFailure -ne ($scenario -in @('changed-pin','registration-ready','registration-running'))){throw 'Unresolved ownership was not reported.'}
    if($result.AutomaticRetryPermitted -or -not $result.RegistrationAttempted){throw 'Invalid dispatch gate.'}
    $checks+=@{Scenario=$scenario;Passed=$true;TaskRemoved=$script:removed;AbortRequested=$script:aborted;CollectAndJoinRequired=$result.CollectAndJoinRequired;UnresolvedOwnership=[bool]$result.CleanupFailure}
}
@{Passed=$checks.Count;Checks=$checks;RealTasksCreated=0;NativeSignOutSubmitted=$false;ObservedUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5