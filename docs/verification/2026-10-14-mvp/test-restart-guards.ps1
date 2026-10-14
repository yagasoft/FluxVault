# Isolated control-flow checks: no shutdown, registry access or real scheduled tasks.
#Requires -Version 7.2
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$source=Join-Path $PSScriptRoot 'restart-mvp.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Restart wrapper syntax failed.'}
foreach($name in @('Remove-UnreceiptedObserver','Join-RestartSubmissionProcess','Repair-FailedRestart')){
    $definition=$ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$false)
    if(@($definition).Count -ne 1){throw ('Missing unique implementation function '+$name)}
    . ([scriptblock]::Create($definition.Extent.Text))
}
$runId='fixture-only';$root='C:\fixture-only';$sealedBootScript='C:\fixture-only\payload\eng\test-mvp-boot.ps1'
$script:calls=[Collections.Generic.List[string]]::new();$results=[Collections.Generic.List[object]]::new()
$script:task=$null;$script:hasReceipt=$false;$script:failHelper=''
$script:actorsLive=$false
function Write-NewReceipt([string]$Name,$Value){$script:calls.Add('Receipt:'+ $Name)}
function Restore-SignIn {$script:calls.Add('RestoreSignIn')}
function Assert-RestorationActorsExited {if($script:actorsLive){throw 'Injected actor remains alive'}}
function Test-Path {param([string]$LiteralPath) return $script:hasReceipt -and $LiteralPath.EndsWith('boot-task.json')}
function Invoke-BootHelper([string]$Action,[int]$Seconds){$script:calls.Add($Action);if($Action -ceq $script:failHelper){throw ('Injected '+$Action+' failure')}}
function Get-ScheduledTask {param($TaskName,$ErrorAction) $script:calls.Add('GetTask');return $script:task}
function Export-ScheduledTask {param($TaskName) $script:calls.Add('ExportTask');return '<fixture-task />'}
function Unregister-ScheduledTask {param($TaskName,$Confirm) $script:calls.Add('RemoveTask');$script:task=$null}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Case([string]$Name,[scriptblock]$Check){
    $script:calls.Clear();$script:task=$null;$script:hasReceipt=$false;$script:failHelper='';$script:actorsLive=$false
    & $Check
    $results.Add(@{Name=$Name;Passed=$true})
}
function New-FixtureTask {
    return [pscustomobject]@{TaskPath='\';Description=('Owned FluxVault MVP boot observer '+$runId);State='Ready';
        Principal=[pscustomobject]@{UserId='SYSTEM';RunLevel=1};
        Actions=@([pscustomobject]@{Execute='C:\Program Files\PowerShell\7\pwsh.exe';Arguments=('-NoProfile -NonInteractive -File "'+$sealedBootScript+'" -Mode Worker -RunId '+$runId);WorkingDirectory=$null});
        Triggers=@([pscustomobject]@{CimClass=[pscustomobject]@{CimClassName='MSFT_TaskBootTrigger'}})}
}
class FixtureRestartProcess {
    [bool]$FirstWaitSucceeds
    [int]$ExitCode
    [int]$WaitCalls
    [bool]$Killed
    [bool]$Disposed
    FixtureRestartProcess([bool]$firstWaitSucceeds,[int]$exitCode){$this.FirstWaitSucceeds=$firstWaitSucceeds;$this.ExitCode=$exitCode}
    [bool]WaitForExit([int]$milliseconds){$this.WaitCalls++;return $this.FirstWaitSucceeds -or $this.WaitCalls -gt 1}
    [void]Kill(){$this.Killed=$true}
    [void]Dispose(){$this.Disposed=$true}
}
Case 'Partial Arm before task receipt independently restores baseline and sign-in' {
    Assert ((Repair-FailedRestart $false $null) -ceq 'PreparationRolledBack') 'Wrong pre-submission disposition'
    Assert (($script:calls -join ',') -ceq 'GetTask,RestoreBaseline,RestoreSignIn') 'Missing independent baseline restoration'
}
Case 'Receipted preparation collects then independently verifies restoration' {
    $script:hasReceipt=$true; $null=Repair-FailedRestart $false $null
    Assert (($script:calls -join ',') -ceq 'Collect,RestoreBaseline,RestoreSignIn') 'Wrong receipted cleanup order'
}
Case 'Collection failure cannot prevent baseline or sign-in restoration' {
    $script:hasReceipt=$true;$script:failHelper='Collect';$failed=$false
    try{$null=Repair-FailedRestart $false $null}catch{$failed=$_.Exception.Message -ceq 'Injected Collect failure'}
    Assert ($failed -and ($script:calls -join ',') -ceq 'Collect,RestoreBaseline,RestoreSignIn') 'Collection failure bypassed restoration'
}
Case 'Baseline restoration failure cannot prevent sign-in restoration' {
    $script:failHelper='RestoreBaseline';$failed=$false
    try{$null=Repair-FailedRestart $false $null}catch{$failed=$_.Exception.Message -ceq 'Injected RestoreBaseline failure'}
    Assert ($failed -and $script:calls.Contains('RestoreSignIn')) 'Sign-in restoration depended on baseline success'
}
Case 'Exact unreceipted task is identified and removed before independent restoration' {
    $script:task=New-FixtureTask;$null=Repair-FailedRestart $false $null
    Assert ($null -eq $script:task -and $script:calls.Contains('RemoveTask')) 'Owned unreceipted task remains'
    Assert (($script:calls | Select-Object -Last 2) -join ',' -ceq 'RestoreBaseline,RestoreSignIn') 'Missing independent restoration after task removal'
}
Case 'Changed unreceipted task is preserved and reported' {
    $script:task=New-FixtureTask;$script:task.Actions[0].Arguments='unreviewed';$failed=$false
    try{$null=Repair-FailedRestart $false $null}catch{$failed=$_.Exception.Message -like '*differs from the owned observer*'}
    Assert ($failed -and -not $script:calls.Contains('RemoveTask') -and $null -ne $script:task) 'Changed task was removed or silently accepted'
    Assert ($script:calls.Contains('RestoreBaseline') -and $script:calls.Contains('RestoreSignIn')) 'Independent restoration skipped after task refusal'
}
Case 'Active unreceipted task requires joined ownership before removal' {
    $script:task=New-FixtureTask;$script:task.State='Running';$failed=$false
    try{$null=Repair-FailedRestart $false $null}catch{$failed=$_.Exception.Message -like '*establish and join*'}
    Assert ($failed -and -not $script:calls.Contains('RemoveTask')) 'Active unreceipted task removed without joining'
}
Case 'Missing shutdown result preserves observer and suspended sign-in' {
    Assert ((Repair-FailedRestart $true $null) -ceq 'UncertainSubmission') 'Missing result treated as refusal'
    Assert (($script:calls -join ',') -ceq 'Receipt:restart-submission-uncertain.json') 'Ambiguous submission triggered rollback'
}
Case 'Accepted shutdown with lost receipt preserves the observation' {
    $state=@{NaturalExit=$true;ExitCode=0}
    Assert ((Repair-FailedRestart $true $state) -ceq 'UncertainSubmission') 'Accepted shutdown triggered cleanup'
    Assert (-not $script:calls.Contains('RestoreSignIn') -and -not $script:calls.Contains('Collect')) 'Accepted shutdown disabled observation'
}
Case 'Killed shutdown process is not a definite refusal' {
    Assert ((Repair-FailedRestart $true @{NaturalExit=$false;ExitCode=-1}) -ceq 'UncertainSubmission') 'Timeout treated as refusal'
    Assert (-not $script:calls.Contains('RestoreBaseline')) 'Ambiguous timeout restored baseline'
}
Case 'Definite Windows refusal permits preparation rollback' {
    Assert ((Repair-FailedRestart $true @{NaturalExit=$true;ExitCode=5}) -ceq 'PreparationRolledBack') 'Definite refusal not cleaned up'
    Assert ($script:calls.Contains('RestoreBaseline') -and $script:calls.Contains('RestoreSignIn')) 'Refused shutdown left settings changed'
}
Case 'Natural shutdown process exit is joined and disposed' {
    $process=[FixtureRestartProcess]::new($true,0);$result=Join-RestartSubmissionProcess $process
    Assert ($result.NaturalExit -and $result.Exited -and $process.WaitCalls -eq 1 -and $process.Disposed -and -not $process.Killed) 'Natural exit not joined'
}
Case 'Hung submission process is killed, joined and marked ambiguous' {
    $process=[FixtureRestartProcess]::new($false,-1);$result=Join-RestartSubmissionProcess $process
    Assert (-not $result.NaturalExit -and $result.Exited -and $process.WaitCalls -eq 2 -and $process.Killed -and $process.Disposed) 'Timeout not joined as ambiguous'
}
Case 'A pin or receipt exception still joins the submission process in finally' {
    $process=[FixtureRestartProcess]::new($true,0);$failed=$false
    try{try{throw 'Injected pin failure'}finally{$result=Join-RestartSubmissionProcess $process}}catch{$failed=$_.Exception.Message -ceq 'Injected pin failure'}
    Assert ($failed -and $process.WaitCalls -eq 1 -and $process.Disposed -and $result.NaturalExit) 'Pin failure bypassed join'
}
Case 'A live preparation actor blocks baseline mutation without a task receipt' {
    $script:actorsLive=$true;$failed=$false
    try{$null=Repair-FailedRestart $false $null}catch{$failed=$_.Exception.Message -ceq 'Injected actor remains alive'}
    Assert ($failed -and -not $script:calls.Contains('RestoreBaseline')) 'Live preparation actor allowed baseline mutation'
    Assert ($script:calls.Contains('RestoreSignIn')) 'Live actor blocked independent sign-in restoration'
}
Case 'Collection failure with an unresolved actor cannot race baseline restoration' {
    $script:hasReceipt=$true;$script:failHelper='Collect';$script:actorsLive=$true;$failed=$false
    try{$null=Repair-FailedRestart $false $null}catch{$failed=$_.Exception.Message -ceq 'Injected actor remains alive'}
    Assert ($failed -and -not $script:calls.Contains('RestoreBaseline')) 'Collection failure allowed racing baseline mutation'
    Assert ($script:calls.Contains('RestoreSignIn')) 'Collection failure blocked independent sign-in restoration'
}
@{SourceSha256=(Get-FileHash -LiteralPath $source).Hash;NoShutdownInvoked=$true;NoRealRegistryAccess=$true;ScheduledTasksMocked=$true;Passed=$results.Count;Checks=$results;CompletedUtc=[DateTimeOffset]::UtcNow}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Host ($results.Count.ToString()+' restart boundary checks passed without host effects.')
