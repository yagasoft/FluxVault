# Fixed NEXT-004 corrective 1.0.8 -> 1.0.9 update. Check has no installation effects.
# The immediate retained fallback is 1.0.8 (manual recovery works; history preview refresh defect remains).
# The accepted 1.0.7 fallback remains available through update-controls.ps1 after its reconciliation guards.
#Requires -Version 7.2
[CmdletBinding()]
param([ValidateSet('Check','Update','Rollback')][string]$Mode='Check', [switch]$OperationalApprovalRecorded)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'update-controls.ps1') -Mode $Mode -OperationalApprovalRecorded:$OperationalApprovalRecorded
Import-Module (Join-Path $controlsRepo 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $controlsPrevious 'commission-authentication.psm1') -Force
Assert-PresentationHash (Join-Path $controlsPrevious 'normal-installed-context.json') '85DF752EB228EB729523CCEDC04B24A5D2143A0600E7076AC0A67F54F988DC83'
$context=Get-Content (Join-Path $controlsPrevious 'normal-installed-context.json') -Raw|ConvertFrom-Json -AsHashtable -Depth 20
$retained=Join-Path $context.WorkRoot 'retained-candidates'
$context.WorkRoot=Join-Path $context.WorkRoot 'controls-update-1.0.9'
$context.OldProductCode='{890BF985-71FD-44FA-A11D-349285CDEF96}'
$context.NewProductCode='{67CE8AD2-72FD-49A7-89B3-44A670060428}'
foreach($pair in @(@('Old','ddc02916cbfa431a9360f07be9ca3eaa','ED218743B8566822405EE0C9447ADB246A6B43A00352B0E73895E6016717D37E'),
    @('New','3da720718ab04b99afdf43455222e0cf','DA9887839FA9B1DE158747E3FF575BE34DFB03C22A66BDB14F4ED62D037E456B'))){
    $manifest=Join-Path $retained ($pair[1]+'/candidate.json')
    Assert-PresentationHash $manifest $pair[2]
    $context[$pair[0]]=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json -AsHashtable -Depth 12
}
if($context.New.SourceCommit -cne '0a0246bcbc10966ab8e9e7981564bebb61705164' -or $context.New.Version -cne '1.0.9.0' -or $context.Old.Version -cne '1.0.8.0'){throw 'Frozen corrective release identity changed.'}
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
    if($state.Old -eq $state.New){throw 'Exactly one reviewed corrective product must be installed.'}
    $installed=if($state.New){$context.New}else{$context.Old}
    Assert-PresentationPayload $installed
    @{ReadOnly=$true;InstalledVersion=$installed.Version;Registration=$state;MsiTables=$tables;NewCandidate=$context.New;ImmediateFallback=$context.Old;Postmaster=$context.Postmaster}|ConvertTo-Json -Depth 14
    return
}
if(-not $OperationalApprovalRecorded){throw 'Recorded standing operational approval is required.'}
if(Get-Process FluxVault.App -ErrorAction SilentlyContinue){throw 'The desktop must exit and be joined before a corrective installation action.'}
if(-not(Test-Path -LiteralPath $context.WorkRoot)){New-VaultFixtureProtectedDirectory $context.WorkRoot}
Assert-VaultFixtureTrustedPath $context.WorkRoot -AdditionalTrustedOwnerSid $context.OperatorSid
if($Mode -eq 'Update'){
    if(-not $state.Old -or $state.New){throw 'Corrective update requires exactly the frozen 1.0.8 registration.'}
    Assert-PresentationPayload $context.Old
    Stop-ControlsServiceJoined $context
    Invoke-PresentationMsi $context 'Update'
    $state=Get-PresentationRegistration $context
    if($state.Old -or -not $state.New){throw 'Exact 1.0.9 registration was not established.'}
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
Write-Host "$Mode completed; exact corrective payload/service verified. Installed acceptance remains separate."
