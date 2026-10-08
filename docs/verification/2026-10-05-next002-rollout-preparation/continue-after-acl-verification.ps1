# One recovery checkpoint only: the approved installer and raw ACL writes already
# completed, before any authentication/provisioning effect. Never replays either.
#Requires -Version 7.2
param([ValidateSet('Check','Continue','CheckReset','ResetPreAdmission')][string]$Mode='Check',[switch]$OperationalApprovalRecorded)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$continuationMode=$Mode;$continuationApproval=$OperationalApprovalRecorded
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$root='C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f'
$originalHash='FCCE1B56966471D65A5E62410B99207D1B943FF8EE8F902837EDB2FE5324A963'
$resetCheckpoint=$continuationMode -in @('CheckReset','ResetPreAdmission')
$checkpointHash=if($resetCheckpoint){'B933D862C7489D72111E5FC8F416CBB647E54BFB25EE071823CE0909264483E9'}else{$originalHash}
Import-Module (Join-Path $repo 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$inputs=Get-Content (Join-Path $PSScriptRoot 'normal-setup-inputs.json') -Raw|ConvertFrom-Json
foreach($entry in $inputs.ProcedureFiles) {
    $path=Join-Path $PSScriptRoot $entry.Name
    Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $inputs.CreatorSid
    if((Get-FileHash -LiteralPath $path).Hash -cne $entry.Sha256){throw 'Reviewed recovery procedure changed.'}
}
. (Join-Path $PSScriptRoot 'commission-installation.ps1')
foreach($name in @('commission-authentication.psm1','commission-setup.psm1','commission-rollback.psm1')){Import-Module (Join-Path $PSScriptRoot $name) -Force}
Add-Type -Path (Join-Path $PSScriptRoot 'postgresql-ancestor-acl.cs')
$path=Join-Path $root 'context.json'
Assert-CommissionHash $path $checkpointHash
$context=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable -Depth 12
if(-not $context.NormalInstallation -or $context.WorkRoot -cne $root -or $context.InstallationId -cne '7871ff7f8d1b404db20771f2e742364f' -or
    $context.OperatorSid -cne 'S-1-5-21-136112424-624261118-1239521417-1001'){throw 'Exact original approved checkpoint required.'}
$context.IdentitySha256=$checkpointHash
Assert-CommissionAclHandoff $context
foreach($key in @('Candidate','Baseline')) {
    $path=Join-Path $root ('parent-'+$key+'.json')
    Assert-CommissionHash $path $context.ParentInputHashes[$key]
    $context[$key]=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable -Depth 12
}
$context.Jobs=@{};$context.Tasks=[Collections.Generic.List[object]]::new()
Assert-CommissionInstallerSettled $context
$intent=Read-CommissionOperatorReceipt $context 'installer-Install-intent'
$completed=Read-CommissionOperatorReceipt $context 'installer-Install-completed'
$installer=@($context.Candidate.Installer|Where-Object Path -like '*.exe')[0]
if($intent.Phase -cne 'Install' -or $intent.Path -cne $installer.Path -or $intent.Sha256 -cne $installer.Sha256 -or
    -not $completed.Joined -or $completed.ExitCode -ne 0 -or $completed.Process.ProcessId -ne 51752 -or
    ([DateTimeOffset]$completed.Process.StartedUtc).UtcDateTime -ne ([DateTimeOffset]'2026-10-07T19:45:14.2431536Z').UtcDateTime -or
    $completed.Process.Executable -cne $installer.Path){throw 'Original completed installer fact changed.'}
$allowedJson=@('context.json','parent-Candidate.json','parent-Baseline.json','installation-ticket.template.json',
    'postgresql-ancestor-acl-proposal.json','installer-Install-intent.json','installer-Install-completed.json')
if($resetCheckpoint) {
    # Exactly the failed, finished cleanup invocation. No authentication worker
    # was dispatched; this is not a successful retirement or activation receipt.
    Assert-CommissionHash (Join-Path $root 'cleanup-failed.json') 'FC62F4168A8BE2E1D4D4A31AD26BD65EC8F53D2D4B6D1FE7DA996A453030643D'
    Assert-CommissionHash (Join-Path $root 'cleanup-ready.json') 'DBA6DD148262E9A45E7D1291C938B04D606F58BB8EF10AA0946476CE6AD47A29'
    Assert-CommissionHash (Join-Path $root 'cleanup-error.json') '59A0060D1892E43552D6DBACED4450C05C99172E48C80B22601A668549CB5A27'
    Assert-CommissionHash (Join-Path $root 'acl-handoff-original-context.json') $originalHash
    if(Test-Path (Join-Path $root 'pre-admission-reset')){throw 'The exact pre-admission reset cannot be replayed.'}
    $allowedJson+=@('acl-handoff-intent.json','acl-handoff-completed.json','acl-handoff-original-context.json',
        'acl-handoff-original-installer-Install-intent.json','acl-handoff-original-installer-Install-completed.json',
        'cleanup-ready.json','cleanup-failed.json','cleanup-error.json','task-cleanup-intent.json','task-cleanup-registered.json','task-cleanup-started.json')
    foreach($path in @('original-pg_hba.conf','original-pg_ident.conf','authentication-recovery.txt')){if(Test-Path (Join-Path $root $path)){throw 'An authentication original was captured; pre-admission reset is refused.'}}
}
if(@(Get-ChildItem -LiteralPath $root -Filter '*.json'|Where-Object Name -cnotin $allowedJson).Count -or
    (Test-Path -LiteralPath $context.ActiveRoot) -or (Test-Path -LiteralPath $context.FailedRoot) -or
    @(Get-ScheduledTask|Where-Object TaskName -like ('FV-NEXT002-'+$context.InstallationId+'-*')).Count){throw 'Checkpoint has provisioning, handoff or task effects; no automatic retry.'}
Assert-CommissionHash (Join-Path $context.RollbackRoot 'config.json') $context.LegacyConfigSha256
Assert-CommissionHash $context.LegacySetupPath $context.Baseline.RollbackSetup.Sha256
foreach($entry in $inputs.ProcedureFiles|Where-Object Name -cin @('commission-administrator.psm1','commission-authentication.psm1',
    'commission-authentication-worker.ps1','commission-authentication-cleanup.ps1','commission-setup.psm1','commission-setup-worker.ps1',
    'postgresql-ancestor-acl.cs','postgresql-ancestor-acl-proposal.json','installation-ticket.template.json')) {
    Assert-CommissionHash (Join-Path $root $entry.Name) $entry.Sha256
}
$auth=Get-Module commission-authentication
$actors=@($context.OperatorIdentity)+@(Get-Content (Join-Path $root 'commission-processes.jsonl')|ForEach-Object {$_|ConvertFrom-Json -AsHashtable})
if($resetCheckpoint) {
    $actors+=@((Read-CommissionOperatorReceipt $context 'cleanup-ready').Process)
    $actors+=@((Get-Content (Join-Path $root 'acl-handoff-original-context.json') -Raw|ConvertFrom-Json -AsHashtable).OperatorIdentity)
}
foreach($actor in $actors){if(& $auth {param($identity) Test-CommissionExactProcess $identity} $actor){throw 'An original operator/actor is still alive.'}}
foreach($job in @($context.WorkerJob,$context.SetupJob)) {
    $held=[FluxVault.Fixtures.OwnedWindowsJob]::Open($job.KernelName,$context.OperatorSid)
    if($null -ne $held){try{throw 'An original owned job is still present.'}finally{$held.Dispose()}}
}
Assert-CommissionCandidate $context -Installed
Assert-CommissionServicePolicy $context
Assert-CommissionHash $context.SqlPath $context.SqlSha256
foreach($value in $context.Prepared.Values){Assert-CommissionHash $value.Path $value.Sha256}
foreach($name in $context.OriginalHashes.Keys) {
    $path=Join-Path $context.DataDirectory $name
    & $auth {param($c,$p) Assert-CommissionAuthenticationPath $c $p} $context $path
    Assert-CommissionHash $path $context.OriginalHashes[$name]
}
foreach($path in @($context.PsqlPath,$context.PgCtlPath)){Assert-CommissionPostgresqlPath $context $path}

# Read through the unchanged native helper while pinning the complete component
# chain. Managed canonicalisation is not sufficient for this recovery checkpoint.
$type=[FluxVault.Commissioning.PostgreSqlAncestorAcl]
$flags=[Reflection.BindingFlags]'NonPublic,Static'
$open=$type.GetMethod('Open',$flags);$verify=$type.GetMethod('Verify',$flags);$read=$type.GetMethod('Read',$flags)
$pins=[Collections.Generic.List[object]]::new()
try {
    $proposal=Get-Content (Join-Path $root 'postgresql-ancestor-acl-proposal.json') -Raw|ConvertFrom-Json
    foreach($entry in $proposal) {
        $mount=[IO.Path]::GetPathRoot($entry.Path)
        $current=$open.Invoke($null,@($null,('\??\'+$mount),[uint32]0x120081));$pins.Add($current)
        $null=$verify.Invoke($null,@($current,$mount))
        $resolved=$mount.TrimEnd('\')
        foreach($part in $entry.Path.Substring($mount.Length).Split('\')) {
            $current=$open.Invoke($null,@($current,$part,[uint32]0x120081));$pins.Add($current);$resolved+='\'+$part
            $null=$verify.Invoke($null,@($current,$resolved))
        }
        if($read.Invoke($null,@($current)) -cne $entry.ProposedSddl){throw 'Exact applied raw ancestor descriptor changed.'}
    }
    if($resetCheckpoint) {
        if($continuationMode -eq 'ResetPreAdmission') {
            if(-not $continuationApproval){throw 'Existing exact rollout approval must be recorded before file-only reset.'}
            Publish-CommissionPreAdmissionReset $context
        }
        @{ReadOnly=($continuationMode -eq 'CheckReset');FileOnlyResetCompleted=($continuationMode -eq 'ResetPreAdmission');
            Status='no admission effects; watchdog retirement unconfirmed';CanActivate=$false;FailedContextSha256=$checkpointHash;
            RestoredContextSha256=$originalHash;InstallerWillRunAgain=$false;AclWillApplyAgain=$false;
            OriginalActorsAndJobsAbsent=$true;OriginalAuthenticationUnchanged=$true;Postmaster=$context.Postmaster}|ConvertTo-Json -Depth 5
        return
    }
    if($continuationMode -eq 'Check') {
        @{ReadOnly=$true;ReadyForExactContinuation=$true;OriginalContextSha256=$originalHash;InstallerAlreadyCompleted=$true;
            InstallerWillRunAgain=$false;AclWillApplyAgain=$false;InstalledPayloadHashesMatched=208;OriginalActorsAndJobsAbsent=$true;
            OriginalAuthenticationUnchanged=$true;ProvisioningAbsent=$true;Postmaster=$context.Postmaster}|ConvertTo-Json -Depth 5
        return
    }
    if(-not $continuationApproval){throw 'Existing exact rollout approval must be recorded before continuation.'}
    try {
        Publish-CommissionAclHandoff $context
        $context.Jobs.Auth=[FluxVault.Fixtures.OwnedWindowsJob]::Create($context.WorkerJob.KernelName,'S-1-5-18')
        $context.Jobs.Setup=[FluxVault.Fixtures.OwnedWindowsJob]::Create($context.SetupJob.KernelName,'S-1-5-18')
        $null=Invoke-CommissionAuthenticationTasks $context
        $task=Start-CommissionSetupTask $context
        $creator=Invoke-CommissionCreatorConfirmation $context
        $setup=Complete-CommissionSetupTask $context $task
        Assert-CommissionActivation $context $creator $setup
        Invoke-CommissionServiceAction $context Activate
        Write-CommissionOperatorReceipt $context 'installation-completed' @{ContextSha256=$context.IdentitySha256;Creator=$creator;Setup=$setup;
            AclCheckpointContinuation=$true;InstallerExecutedAgain=$false;LiveWorkflowAccepted=$false}
    }catch {Invoke-CommissionServiceAction $context Stop;throw}
    finally {Stop-CommissionActors $context}
}finally{for($index=$pins.Count-1;$index -ge 0;$index--){$pins[$index].Dispose()}}
