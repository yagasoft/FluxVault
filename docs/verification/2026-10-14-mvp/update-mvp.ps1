# Bounded 1.0.11 -> reviewed supplementary MVP candidate; immediate rollback preserves automatic state.
#Requires -Version 7.2
param([ValidateSet('Baseline','Check','Update','Rollback','Restart')][string]$Mode='Baseline',
    [ValidatePattern('^[0-9a-f]{32}$')][string]$CandidateId,
    [ValidatePattern('^[0-9A-F]{64}$')][string]$CandidateSha256,
    [ValidateRange(1,2)][int]$Cycle=1,
    [switch]$OperationalApprovalRecorded)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$automaticRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$preparation=Join-Path $PSScriptRoot '../2026-10-05-next002-rollout-preparation'
$automaticMode=$Mode;$automaticApproval=[bool]$OperationalApprovalRecorded
. (Join-Path $PSScriptRoot '../2026-10-08-next004-controls/update-controls.ps1') -Mode Check
$Mode=$automaticMode;$OperationalApprovalRecorded=$automaticApproval
Import-Module (Join-Path $automaticRepo 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $preparation 'commission-authentication.psm1') -Force
Assert-PresentationHash (Join-Path $preparation 'normal-installed-context.json') '85DF752EB228EB729523CCEDC04B24A5D2143A0600E7076AC0A67F54F988DC83'
$context=Get-Content (Join-Path $preparation 'normal-installed-context.json') -Raw|ConvertFrom-Json -AsHashtable -Depth 20
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{if($identity.User.Value -cne $context.OperatorSid -or -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'The intended elevated creator/operator is required.'}}finally{$identity.Dispose()}
# Verify the current process from the unchanged service/data/port binding; never restart PostgreSQL.
$pgService=Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-18'"
if($pgService.State -cne 'Running' -or $pgService.StartName -cne 'NT AUTHORITY\NetworkService' -or
    $pgService.PathName -notlike '*D:\Program Files\PostgreSQL\18\bin\pg_ctl.exe*' -or $pgService.PathName -notlike '*D:\Program Files\PostgreSQL\18\data*'){throw 'Normal PostgreSQL service binding changed.'}
$lines=[IO.File]::ReadAllLines((Join-Path $context.DataDirectory 'postmaster.pid'))
if($lines.Count -lt 8 -or [IO.Path]::GetFullPath($lines[1]) -ine [IO.Path]::GetFullPath($context.DataDirectory) -or [int]$lines[3] -ne 5432 -or $lines[7].Trim() -cne 'ready'){throw 'Normal PostgreSQL endpoint is not ready.'}
$postmaster=Get-Process -Id ([int]$lines[0])
try{
    $actual=Get-VaultFixtureProcessIdentity $postmaster
    $native=Get-CimInstance Win32_Process -Filter ('ProcessId='+$actual.ProcessId)
    if($actual.Executable -ine $context.Postmaster.Executable -or $native.ParentProcessId -ne $pgService.ProcessId -or
        $native.CommandLine -notlike '*D:\Program Files\PostgreSQL\18\data*'){throw 'Postmaster service/data ancestry changed.'}
    $context.Postmaster=@{ProcessId=$actual.ProcessId;StartedUtc=$actual.StartedUtc;Executable=$actual.Executable;DataDirectory=$context.DataDirectory;Port=5432}
}finally{$postmaster.Dispose()}
$retained=Join-Path $context.WorkRoot 'retained-candidates'
$oldManifest=Join-Path $retained 'de279a32e6fc4d7b9b86e02150bd98cd/candidate.json'
Assert-PresentationHash $oldManifest '35B669189863F0CE265BE622383B3749DB00BCBD6528A749680109125115EECA'
$context.Old=Get-Content $oldManifest -Raw|ConvertFrom-Json -AsHashtable -Depth 12
$context.OldProductCode=''
foreach($entry in $context.Old.Installer){$entry.Path=Join-Path $retained ($context.Old.CandidateId+'/installer/'+[IO.Path]::GetFileName($entry.Path));Assert-PresentationHash $entry.Path $entry.Sha256}
$oldTables=Read-PresentationMsi @($context.Old.Installer|Where-Object Path -like '*.msi')[0].Path
$oldProperties=@{};foreach($row in $oldTables.Property){$oldProperties[$row[0]]=$row[1]}
if($oldProperties.ProductVersion -cne '1.0.11.0' -or $oldProperties.UpgradeCode -cne '{4B89B6E7-D41E-49E6-BE42-09C10D6570D6}'){throw 'Retained immediate fallback identity changed.'}
$context.OldProductCode=$oldProperties.ProductCode
if($Mode -eq 'Baseline'){
    $context.WorkRoot=Join-Path $context.WorkRoot 'mvp-baseline-1.0.11'
    if(-not(Test-Path -LiteralPath $context.WorkRoot)){New-VaultFixtureProtectedDirectory $context.WorkRoot}
    Assert-PresentationPreservation $context
    Assert-PresentationPayload $context.Old
    $inspection=Invoke-ControlsRollbackInspection $context
    @{ReadOnly=$true;InstalledVersion=$context.Old.Version;Postmaster=$context.Postmaster;Inspection=$inspection;ObservedUtc=[DateTimeOffset]::UtcNow}|ConvertTo-Json -Depth 10
    return
}
if(-not $CandidateId -or -not $CandidateSha256){throw 'The independently reviewed concrete candidate identity and manifest hash are required.'}
$newManifest=Join-Path $retained ($CandidateId+'/candidate.json')
Assert-PresentationHash $newManifest $CandidateSha256
$context.New=Get-Content $newManifest -Raw|ConvertFrom-Json -AsHashtable -Depth 12
if($context.New.CandidateId -cne $CandidateId -or $context.New.Version -cnotin @('1.0.12.0') -or $context.New.Failure -or
    @($context.New.Launches|Where-Object {-not $_.Exited -or $_.ExitCode -ne 0}).Count){throw 'The frozen candidate did not complete.'}
foreach($entry in $context.New.Installer){$entry.Path=Join-Path $retained ($CandidateId+'/installer/'+[IO.Path]::GetFileName($entry.Path));Assert-PresentationHash $entry.Path $entry.Sha256}
$tables=Read-PresentationMsi @($context.New.Installer|Where-Object Path -like '*.msi')[0].Path
$properties=@{};foreach($row in $tables.Property){$properties[$row[0]]=$row[1]}
if($properties.ProductVersion -cne $context.New.Version -or $properties.UpgradeCode -cne '{4B89B6E7-D41E-49E6-BE42-09C10D6570D6}'){throw 'Candidate MSI identity changed.'}
$context.NewProductCode=$properties.ProductCode
$candidateWorkRoot=Join-Path $context.WorkRoot ('mvp-update-'+([version]$context.New.Version).ToString(3)+'-'+$CandidateId)
if(-not(Test-Path -LiteralPath $candidateWorkRoot)){New-VaultFixtureProtectedDirectory $candidateWorkRoot}
Assert-VaultFixtureTrustedPath $candidateWorkRoot -AdditionalTrustedOwnerSid $context.OperatorSid
# A second explicitly selected cycle retains the first upgrade/rollback receipts.
# Every existing cycle must be settled before another can perform any effect.
foreach($number in 1,2){
    $cycleRoot=Join-Path $candidateWorkRoot ('cycle-'+$number)
    if(Test-Path -LiteralPath $cycleRoot){
        Assert-VaultFixtureTrustedPath $cycleRoot -AdditionalTrustedOwnerSid $context.OperatorSid
        $prior=$context.Clone();$prior.WorkRoot=$cycleRoot;Assert-PresentationSettled $prior
    }
}
$context.WorkRoot=Join-Path $candidateWorkRoot ('cycle-'+$Cycle)
if(-not(Test-Path -LiteralPath $context.WorkRoot)){New-VaultFixtureProtectedDirectory $context.WorkRoot}
Assert-PresentationSettled $context
Assert-PresentationPreservation $context
$state=Get-PresentationRegistration $context
if($state.Old -eq $state.New){throw 'Exactly one accepted predecessor or reviewed candidate must be installed.'}
Assert-PresentationPayload $(if($state.New){$context.New}else{$context.Old})
if($Mode -eq 'Check'){
    @{ReadOnly=$true;Registration=$state;Postmaster=$context.Postmaster;NewCandidate=$context.New;ImmediateFallback=$context.Old;MsiTables=$tables}|ConvertTo-Json -Depth 14
    return
}
if(-not $OperationalApprovalRecorded){throw 'Recorded standing operational approval is required.'}
if($Mode -eq 'Update' -and -not $state.Old){throw 'Update requires the accepted 1.0.11 predecessor.'}
if($Mode -in @('Rollback','Restart') -and -not $state.New){throw 'Rollback/restart requires this exact reviewed candidate.'}
Assert-ControlsClientRecordsResolved (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FluxVault')
$phases=if($Mode -eq 'Update'){@('Update')}elseif($Mode -eq 'Rollback'){@('UninstallNew','InstallOld')}else{@()}
foreach($phase in $phases){
    foreach($suffix in @('-intent.json','-completed.json','.log')){
        if(Test-Path -LiteralPath (Join-Path $context.WorkRoot ($phase+$suffix))){
            throw 'This cycle already contains installer evidence. Select the unused reviewed cycle before stopping the service.'
        }
    }
}
Stop-ControlsServiceJoined $context
if($Mode -eq 'Update'){
    if(-not $state.Old){throw 'Update requires the accepted 1.0.11 predecessor.'}
    Invoke-PresentationMsi $context 'Update'
    $target=$context.New
}elseif($Mode -eq 'Rollback'){
    if(-not $state.New){throw 'Rollback requires this exact reviewed candidate.'}
    Restore-ControlsProduct $context
    $target=$context.Old
}else{
    if(-not $state.New){throw 'The bounded restart applies only to this candidate.'}
    $target=$context.New
}
$state=Get-PresentationRegistration $context
if(($target.CandidateId -ceq $context.New.CandidateId) -ne $state.New -or $state.Old -eq $state.New){throw 'Expected exact registration was not established.'}
Assert-PresentationPayload $target
Assert-PresentationPreservation $context
Assert-PresentationSettled $context
Set-PresentationService $context $true
Assert-PresentationPreservation $context
Write-PresentationReceipt $context ($Mode+'-verified-'+[guid]::NewGuid().ToString('N')) @{Version=$target.Version;PayloadHashesMatched=208;Registration=$state;Postmaster=$context.Postmaster;BootstrapAuthenticationAndAclPreserved=$true;ObservedUtc=[DateTimeOffset]::UtcNow}
Write-Host "$Mode completed; exact payload and joined FluxVault service transition verified. Installed acceptance remains separate."
