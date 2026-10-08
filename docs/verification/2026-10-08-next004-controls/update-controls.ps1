# Fixed NEXT-004 1.0.7 -> 1.0.8 update. Check has no installation effects.
#Requires -Version 7.2
param([ValidateSet('Check','Update','Rollback')][string]$Mode='Check', [switch]$OperationalApprovalRecorded)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$controlsRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$controlsPrevious=Join-Path $PSScriptRoot '../2026-10-05-next002-rollout-preparation'
. (Join-Path $controlsPrevious 'update-presentation.ps1') -Mode $Mode -OperationalApprovalRecorded:$OperationalApprovalRecorded

function Stop-ControlsServiceJoined($Context) {
    $service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
    $process=$null
    try {
        if($service.ProcessId){$process=Get-Process -Id $service.ProcessId
            if($process.Path -ine 'C:\Program Files\FluxVault\service\FluxVault.Service.exe'){throw 'Service process binding changed.'}}
        Set-PresentationService $Context $false
        if($null -ne $process -and -not $process.WaitForExit(10000)){throw 'Stopped FluxVault process did not exit; preserve and stop.'}
    }finally{if($null -ne $process){$process.Dispose()}}
}

function Invoke-ControlsRollbackInspection($Context) {
    # SYSTEM is the existing authenticated service database principal. Never change PostgreSQL authentication.
    $runId=[guid]::NewGuid().ToString('N')
    $root=Join-Path $Context.WorkRoot ('rollback-read-'+$runId)
    New-VaultFixtureProtectedDirectory $root
    $sources=@{
        'inspect-rollback-worker.ps1'=(Join-Path $PSScriptRoot 'inspect-rollback-worker.ps1')
        'vault-windows-fixture.psm1'=(Join-Path $controlsRepo 'eng/fixtures/vault-windows-fixture.psm1')
        'owned-windows-job.cs'=(Join-Path $controlsRepo 'eng/fixtures/owned-windows-job.cs')
        'commission-authentication.psm1'=(Join-Path $controlsPrevious 'commission-authentication.psm1')
        'postgresql-ancestor-acl.cs'=(Join-Path $controlsPrevious 'postgresql-ancestor-acl.cs')
    }
    foreach($name in $sources.Keys){
        Assert-VaultFixtureTrustedPath $sources[$name] -AdditionalTrustedOwnerSid $Context.OperatorSid
        $hash=(Get-FileHash -LiteralPath $sources[$name]).Hash
        Copy-Item -LiteralPath $sources[$name] -Destination (Join-Path $root $name)
        Assert-PresentationHash (Join-Path $root $name) $hash
    }
    if($null -eq ('FluxVault.Commissioning.PostgreSqlAncestorAcl' -as [type])){Add-Type -Path (Join-Path $root 'postgresql-ancestor-acl.cs')}
    Import-Module (Join-Path $controlsPrevious 'commission-authentication.psm1') -Force
    Assert-CommissionPostgresqlPath $Context $Context.PsqlPath
    Import-VaultFixtureJobType
    $kernel='Global\FluxVault.NEXT002.'+$runId
    $taskCreated=$false;$definitionHash=$null;$name=$null
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Create($kernel,'S-1-5-18')
    try {
        $inspection=@{WorkRoot=$root;KernelName=$kernel;OperatorSid=$Context.OperatorSid;PsqlPath=$Context.PsqlPath;
            ServiceConnection=$Context.ServiceConnection;Postmaster=$Context.Postmaster;DataDirectory=$Context.DataDirectory;
            Port=$Context.Port;ApplicationName=$Context.ApplicationName;NormalInstallation=$true;EmptyPasswordFile=(Join-Path $root 'empty.pgpass')}
        [IO.File]::WriteAllText($inspection.EmptyPasswordFile,'')
        $contextPath=Join-Path $root 'context.json'
        $inspection|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $contextPath
        $hash=(Get-FileHash -LiteralPath $contextPath).Hash
        $name='FluxVault-NEXT004-Read-'+$runId
        if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Inspection task collision.'}
        $arguments='-NoProfile -NonInteractive -File "'+(Join-Path $root 'inspect-rollback-worker.ps1')+'" -ContextPath "'+$contextPath+'" -ContextSha256 '+$hash
        $action=New-ScheduledTaskAction -Execute 'C:\Program Files\PowerShell\7\pwsh.exe' -Argument $arguments
        $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 60) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $name -Action $action -Settings $settings -User SYSTEM -RunLevel Highest -Description ('FluxVault read-only NEXT004 '+$runId)|Out-Null
        $taskCreated=$true
        $definitionHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $name))))
        Start-ScheduledTask -TaskName $name
        $timer=[Diagnostics.Stopwatch]::StartNew()
        do {
            $task=Get-ScheduledTask -TaskName $name
            if($timer.Elapsed.TotalSeconds -gt 60){throw 'Inspection deadline exceeded.'}
            if($task.State -notin @('Running','Queued') -and (Test-Path -LiteralPath (Join-Path $root 'result.json'))){break}
            if($task.State -notin @('Running','Queued') -and $timer.Elapsed.TotalSeconds -gt 10 -and (Get-ScheduledTaskInfo $name).LastTaskResult -ne 0){throw 'Inspection worker failed.'}
            Start-Sleep -Milliseconds 100
        }while($true)
        if((Get-ScheduledTaskInfo $name).LastTaskResult -ne 0){throw 'Inspection task did not exit successfully.'}
        $result=Get-Content -LiteralPath (Join-Path $root 'result.json') -Raw|ConvertFrom-Json -AsHashtable
        if(-not $result.ReadOnly -or -not $result.Tool.Joined -or $result.Tool.ExitCode -ne 0){throw 'Inspection evidence is incomplete.'}
        return $result
    }finally{
        $cleanup=Complete-ControlsInspection $job $name $taskCreated $definitionHash
        $cleanup.RunId=$runId
        $cleanup|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'cleanup.json')
    }
}

function Complete-ControlsInspection($Job,[string]$TaskName,[bool]$TaskCreated,[string]$DefinitionHash,
    [ValidateRange(1,10000)][int]$TimeoutMilliseconds=10000) {
    $removed=$false
    try {
        if($TaskCreated){
            $actualHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $TaskName))))
            if($actualHash -cne $DefinitionHash){throw 'Inspection task ownership changed; do not remove it.'}
            if((Get-ScheduledTask -TaskName $TaskName).State -in @('Running','Queued')){Stop-ScheduledTask -TaskName $TaskName}
            $timer=[Diagnostics.Stopwatch]::StartNew()
            while((Get-ScheduledTask -TaskName $TaskName).State -in @('Running','Queued')){
                if($timer.ElapsedMilliseconds -ge $TimeoutMilliseconds){throw 'Owned inspection task did not exit; do not claim cleanup.'}
                Start-Sleep -Milliseconds 100
            }
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            $removed=$true
        }
    }finally{
        try{$Job.StopAndJoin()}finally{$Job.Dispose()}
    }
    return @{OwnedJobJoined=$true;TaskRemoved=$removed}
}

function Restore-ControlsProduct($Context) {
    if(Get-Service FluxVaultService -ErrorAction SilentlyContinue){Stop-ControlsServiceJoined $Context}
    $inspection=Invoke-ControlsRollbackInspection $Context
    if($inspection.PendingHistoryDeletions -ne 0){throw 'Unresolved admitted history deletion blocks downgrade; retain 1.0.8 and reconcile the original outcome.'}
    if($inspection.OwnerSid -cne $Context.OperatorSid -or $inspection.GrantCount -ne 0){throw 'This fixed rollback requires the sole intended creator. Additional access grants require checked client-profile reconciliation before downgrade.'}
    Assert-ControlsClientRecordsResolved (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FluxVault')
    Restore-PresentationProduct $Context
}

function Assert-ControlsClientRecordsResolved([string]$StateRoot) {
    if(Test-Path -LiteralPath (Join-Path $StateRoot 'pending-protection-save.json')){
        throw 'A pending client save, pause or deletion record blocks downgrade. Preserve it and check the original outcome in 1.0.8 first.'
    }
}

if($MyInvocation.InvocationName -eq '.'){return}
Import-Module (Join-Path $controlsRepo 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $controlsPrevious 'commission-authentication.psm1') -Force
Assert-PresentationHash (Join-Path $controlsPrevious 'normal-installed-context.json') '85DF752EB228EB729523CCEDC04B24A5D2143A0600E7076AC0A67F54F988DC83'
$context=Get-Content (Join-Path $controlsPrevious 'normal-installed-context.json') -Raw|ConvertFrom-Json -AsHashtable -Depth 20
$retained=Join-Path $context.WorkRoot 'retained-candidates'
$context.WorkRoot=Join-Path $context.WorkRoot 'controls-update-1.0.8'
$context.OldProductCode='{0D47E056-CFFC-4BA4-91A6-3CD47F3FD688}'
$context.NewProductCode='{890BF985-71FD-44FA-A11D-349285CDEF96}'
foreach($pair in @(@('Old','c4e807860b1c49e798b2380652ca5926','151024FCF7CDA53ED0345A85707E042276A244B80A7FFE71FFB1275276393BE0'),
    @('New','ddc02916cbfa431a9360f07be9ca3eaa','ED218743B8566822405EE0C9447ADB246A6B43A00352B0E73895E6016717D37E'))){
    $manifest=Join-Path $retained ($pair[1]+'/candidate.json')
    Assert-PresentationHash $manifest $pair[2]
    $context[$pair[0]]=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json -AsHashtable -Depth 12
}
if($context.New.SourceCommit -cne '11f9a915f83f4f56b2d0b152947145491d290e7b' -or $context.New.Version -cne '1.0.8.0' -or $context.Old.Version -cne '1.0.7.0'){throw 'Frozen release identity changed.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{if($identity.User.Value -cne $context.OperatorSid -or -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'The intended elevated creator/operator is required.'}}finally{$identity.Dispose()}
$tables=@{}
foreach($kind in @('Old','New')){
    $candidate=$context[$kind]
    if($candidate.Failure -or @($candidate.Launches|Where-Object {-not $_.Exited -or $_.ExitCode -ne 0}).Count){throw 'Candidate build did not complete.'}
    foreach($entry in $candidate.Installer){
        $entry.Path=Join-Path $retained ($candidate.CandidateId+'/installer/'+[IO.Path]::GetFileName($entry.Path))
        Assert-PresentationHash $entry.Path $entry.Sha256
    }
    $msi=@($candidate.Installer|Where-Object Path -like '*.msi')[0]
    $tables[$kind]=Read-PresentationMsi $msi.Path
    $properties=@{};foreach($row in $tables[$kind].Property){$properties[$row[0]]=$row[1]}
    if($properties.ProductVersion -cne $candidate.Version -or $properties.ProductCode -cne $context[$kind+'ProductCode'] -or $properties.UpgradeCode -cne '{4B89B6E7-D41E-49E6-BE42-09C10D6570D6}'){throw 'Sealed MSI identity changed.'}
}
Assert-PresentationSettled $context
Assert-PresentationPreservation $context
$state=Get-PresentationRegistration $context
if($Mode -eq 'Check'){
    if($state.Old -eq $state.New){throw 'Exactly one reviewed product must be installed.'}
    $installed=if($state.New){$context.New}else{$context.Old}
    Assert-PresentationPayload $installed
    @{ReadOnly=$true;InstalledVersion=$installed.Version;Registration=$state;MsiTables=$tables;NewCandidate=$context.New;RollbackCandidate=$context.Old;Postmaster=$context.Postmaster}|ConvertTo-Json -Depth 14
    return
}
if(-not $OperationalApprovalRecorded){throw 'Recorded standing operational approval is required.'}
if(-not(Test-Path -LiteralPath $context.WorkRoot)){New-VaultFixtureProtectedDirectory $context.WorkRoot}
Assert-VaultFixtureTrustedPath $context.WorkRoot -AdditionalTrustedOwnerSid $context.OperatorSid
if($Mode -eq 'Update'){
    if(-not $state.Old -or $state.New){throw 'Update requires exactly the accepted 1.0.7 registration.'}
    Assert-PresentationPayload $context.Old
    Stop-ControlsServiceJoined $context
    Invoke-PresentationMsi $context 'Update'
    $state=Get-PresentationRegistration $context
    if($state.Old -or -not $state.New){throw 'Exact 1.0.8 registration was not established.'}
    $target=$context.New
}else{
    Restore-ControlsProduct $context
    $target=$context.Old
}
Assert-PresentationPayload $target
Assert-PresentationPreservation $context
Assert-PresentationSettled $context
Set-PresentationService $context $true
Assert-PresentationPreservation $context
$service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
Write-PresentationReceipt $context ($Mode+'-verified') @{Version=$target.Version;PayloadHashesMatched=208;Registration=(Get-PresentationRegistration $context);Service=$service|Select-Object State,StartMode,StartName,ProcessId,PathName;BootstrapAuthenticationAndAclPreserved=$true;Postmaster=$context.Postmaster;ObservedUtc=[DateTimeOffset]::UtcNow}
Write-Host "$Mode completed; exact payload/service verified. Installed workflow acceptance remains separate."
