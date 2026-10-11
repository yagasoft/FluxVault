# Bounded 1.0.9 -> 1.0.10 staging update. Uses existing installation, preservation and joined-job helpers.
#Requires -Version 7.2
param([ValidateSet('Baseline','Check','Update','Rollback','Restart')][string]$Mode='Baseline',
    [ValidatePattern('^[0-9a-f]{32}$')][string]$CandidateId,
    [ValidatePattern('^[0-9A-F]{64}$')][string]$CandidateSha256,
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
# The PC rebooted on 10 October. Verify the current process from the unchanged service/data/port binding.
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
$oldManifest=Join-Path $retained '3da720718ab04b99afdf43455222e0cf/candidate.json'
Assert-PresentationHash $oldManifest 'DA9887839FA9B1DE158747E3FF575BE34DFB03C22A66BDB14F4ED62D037E456B'
$context.Old=Get-Content $oldManifest -Raw|ConvertFrom-Json -AsHashtable -Depth 12
$context.OldProductCode='{67CE8AD2-72FD-49A7-89B3-44A670060428}'
foreach($entry in $context.Old.Installer){$entry.Path=Join-Path $retained ($context.Old.CandidateId+'/installer/'+[IO.Path]::GetFileName($entry.Path));Assert-PresentationHash $entry.Path $entry.Sha256}
if($Mode -eq 'Baseline'){
    $context.WorkRoot=Join-Path $context.WorkRoot 'automatic-baseline-1.0.9'
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
if($context.New.CandidateId -cne $CandidateId -or $context.New.Version -cne '1.0.10.0' -or $context.New.Failure -or
    @($context.New.Launches|Where-Object {-not $_.Exited -or $_.ExitCode -ne 0}).Count){throw 'The frozen candidate did not complete.'}
foreach($entry in $context.New.Installer){$entry.Path=Join-Path $retained ($CandidateId+'/installer/'+[IO.Path]::GetFileName($entry.Path));Assert-PresentationHash $entry.Path $entry.Sha256}
$tables=Read-PresentationMsi @($context.New.Installer|Where-Object Path -like '*.msi')[0].Path
$properties=@{};foreach($row in $tables.Property){$properties[$row[0]]=$row[1]}
if($properties.ProductVersion -cne '1.0.10.0' -or $properties.UpgradeCode -cne '{4B89B6E7-D41E-49E6-BE42-09C10D6570D6}'){throw 'Candidate MSI identity changed.'}
$context.NewProductCode=$properties.ProductCode
$context.WorkRoot=Join-Path $context.WorkRoot ('automatic-update-1.0.10-'+$CandidateId)
if(-not(Test-Path -LiteralPath $context.WorkRoot)){New-VaultFixtureProtectedDirectory $context.WorkRoot}
Assert-VaultFixtureTrustedPath $context.WorkRoot -AdditionalTrustedOwnerSid $context.OperatorSid
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
if($Mode -eq 'Update' -and -not $state.Old){throw 'Update requires the accepted 1.0.9 predecessor.'}
if($Mode -in @('Rollback','Restart') -and -not $state.New){throw 'Rollback/restart requires the reviewed 1.0.10 candidate.'}
Assert-ControlsClientRecordsResolved (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FluxVault')
Stop-ControlsServiceJoined $context
if($Mode -eq 'Update'){
    if(-not $state.Old){throw 'Update requires the accepted 1.0.9 predecessor.'}
    Invoke-PresentationMsi $context 'Update'
    $target=$context.New
}elseif($Mode -eq 'Rollback'){
    if(-not $state.New){throw 'Rollback requires the reviewed 1.0.10 candidate.'}
    $marker=Invoke-ControlsRollbackInspection $context -WorkerSource (Join-Path $PSScriptRoot 'automatic-rollback-worker.ps1') -RequireAutomaticApproval
    Write-PresentationReceipt $context 'automatic-approval-invalidated' $marker
    Restore-ControlsProduct $context
    $target=$context.Old
}else{
    if(-not $state.New){throw 'The bounded restart applies only to this candidate.'}
    $target=$context.New
}
$state=Get-PresentationRegistration $context
if(($target.Version -eq '1.0.10.0') -ne $state.New -or $state.Old -eq $state.New){throw 'Expected exact registration was not established.'}
Assert-PresentationPayload $target
Assert-PresentationPreservation $context
Assert-PresentationSettled $context
Set-PresentationService $context $true
Assert-PresentationPreservation $context
Write-PresentationReceipt $context ($Mode+'-verified-'+[guid]::NewGuid().ToString('N')) @{Version=$target.Version;PayloadHashesMatched=208;Registration=$state;Postmaster=$context.Postmaster;BootstrapAuthenticationAndAclPreserved=$true;ObservedUtc=[DateTimeOffset]::UtcNow}
Write-Host "$Mode completed; exact payload and joined FluxVault service transition verified. Installed acceptance remains separate."
