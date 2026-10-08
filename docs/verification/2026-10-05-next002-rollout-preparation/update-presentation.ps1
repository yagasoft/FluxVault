# Fixed 1.0.6 -> 1.0.7 operator procedure. Check has no installation effects.
# Update/Rollback require recorded operational approval and the independent review.
#Requires -Version 7.2
param([ValidateSet('Check','Update','Rollback')][string]$Mode='Check',
    [switch]$OperationalApprovalRecorded)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Write-PresentationReceipt($Context,[string]$Name,$Value) {
    $stream=[IO.FileStream]::new((Join-Path $Context.WorkRoot ($Name+'.json')),'CreateNew','Write','None')
    try{$bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 12));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}

function Assert-PresentationHash([string]$Path,[string]$Sha256) {
    Assert-VaultFixtureTrustedPath $Path -AdditionalTrustedOwnerSid 'S-1-5-21-136112424-624261118-1239521417-1001'
    if((Get-FileHash -LiteralPath $Path).Hash -cne $Sha256){throw "Pinned input changed: $Path"}
}

function Read-PresentationMsi([string]$Path) {
    $wi=New-Object -ComObject WindowsInstaller.Installer;$db=$null;$result=@{}
    try {
        $db=$wi.OpenDatabase($Path,0)
        $catalog=$db.OpenView('SELECT `Name` FROM `_Tables`');$names=[Collections.Generic.List[string]]::new()
        try{$null=$catalog.Execute();while($record=$catalog.Fetch()){
            try{$names.Add($record.StringData(1))}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)}
        }}finally{$null=$catalog.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($catalog)}
        foreach($table in @('Property','Upgrade','InstallExecuteSequence','ServiceInstall','ServiceControl','CustomAction')) {
            if($table -cnotin $names){if($table -ceq 'CustomAction'){$result[$table]=@();continue};throw "Required MSI table is missing: $table"}
            $view=$null;$rows=[Collections.Generic.List[object]]::new()
            try {
                $view=$db.OpenView(('SELECT * FROM `'+$table+'`'));$null=$view.Execute()
                while($record=$view.Fetch()) {
                    try{$count=$record.GetType().InvokeMember('FieldCount','GetProperty',$null,$record,$null)
                        $row=@(for($i=1;$i -le $count;$i++){$record.StringData($i)});$rows.Add($row)}
                    finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)}
                }
                $result[$table]=$rows.ToArray()
            }finally{if($null -ne $view){$null=$view.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)}}
        }
    }finally{
        if($null -ne $db){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db)}
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($wi)
    }
    return $result
}

function Get-PresentationRegistration {
    $wi=New-Object -ComObject WindowsInstaller.Installer;$related=$null
    try {
        $related=$wi.RelatedProducts('{4B89B6E7-D41E-49E6-BE42-09C10D6570D6}')
        $codes=@($related)
        foreach($code in $codes){if($code -cnotin @('{1EEE244A-290D-4F25-94FE-E3AA8384F718}','{0D47E056-CFFC-4BA4-91A6-3CD47F3FD688}')){throw 'Unexpected related product; preserve registration and stop.'}}
        $old=$wi.ProductState('{1EEE244A-290D-4F25-94FE-E3AA8384F718}');$new=$wi.ProductState('{0D47E056-CFFC-4BA4-91A6-3CD47F3FD688}')
        if($old -notin @(-1,5) -or $new -notin @(-1,5)){throw 'Product registration is not settled/installed or absent.'}
        return @{Old=($old -eq 5);New=($new -eq 5);Related=$codes}
    }finally{
        if($null -ne $related){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($related)}
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($wi)
    }
}

function Assert-PresentationSettled($Context) {
    foreach($name in @('installer-uncertain','reboot-required')) {
        if(Test-Path -LiteralPath (Join-Path $Context.WorkRoot ($name+'.json'))){throw 'Installer state is unresolved; reconcile externally before further effects.'}
    }
    foreach($phase in @('Update','UninstallNew','InstallOld')) {
        $intent=Test-Path -LiteralPath (Join-Path $Context.WorkRoot ($phase+'-intent.json'))
        $done=Test-Path -LiteralPath (Join-Path $Context.WorkRoot ($phase+'-completed.json'))
        if($intent -ne $done){throw 'Installer intent is unresolved; reconcile externally before further effects.'}
        if($done){$receipt=Get-Content -LiteralPath (Join-Path $Context.WorkRoot ($phase+'-completed.json')) -Raw|ConvertFrom-Json
            if(-not $receipt.Joined -or $receipt.ExitCode -in @(1641,3010)){throw 'Installer completion is unresolved.'}}
    }
    if(Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress'){throw 'Windows Installer is unresolved/in progress.'}
}

function Invoke-PresentationNative($Context,[string]$Executable,[string[]]$Arguments,[int]$Seconds=15) {
    Assert-VaultFixtureTrustedPath $Executable -AdditionalTrustedOwnerSid $Context.OperatorSid
    return & (Get-Module commission-authentication) {param($c,$e,$a,$s) Invoke-CommissionTool $c $e $a $null -TimeoutSeconds $s} $Context $Executable $Arguments $Seconds
}

function Invoke-PresentationMsi($Context,[string]$Phase) {
    Assert-PresentationSettled $Context
    $entry=if($Phase -eq 'InstallOld'){$Context.Old.Installer|Where-Object Path -like '*.msi'}else{$Context.New.Installer|Where-Object Path -like '*.msi'}
    Assert-PresentationHash $entry.Path $entry.Sha256
    Write-PresentationReceipt $Context ($Phase+'-intent') @{Path=$entry.Path;Sha256=$entry.Sha256;Phase=$Phase;ObservedUtc=[DateTimeOffset]::UtcNow}
    $verb=if($Phase -eq 'UninstallNew'){'/x'}else{'/i'}
    try{$reply=Invoke-PresentationNative $Context 'C:\Windows\System32\msiexec.exe' @($verb,$entry.Path,'/qn','/norestart','REBOOT=ReallySuppress','/L*v',(Join-Path $Context.WorkRoot ($Phase+'.log'))) 120}
    catch{Write-PresentationReceipt $Context 'installer-uncertain' @{Phase=$Phase;Error=$_.Exception.Message};throw}
    Write-PresentationReceipt $Context ($Phase+'-completed') $reply
    if($reply.ExitCode -in @(1641,3010)){Write-PresentationReceipt $Context 'reboot-required' @{Phase=$Phase;ExitCode=$reply.ExitCode};throw 'Reboot required; runtime and further installer operations remain blocked.'}
    if(-not $reply.Joined -or $reply.ExitCode -ne 0){throw 'MSI failed; inspect settled registration before explicit rollback. Runtime stays stopped.'}
    Assert-PresentationSettled $Context
}

function Restore-PresentationProduct($Context) {
    Assert-PresentationSettled $Context
    $state=Get-PresentationRegistration
    if($state.Old -and $state.New){throw 'Unexpected parallel products; preserve and reconcile before rollback.'}
    if($state.New){Invoke-PresentationMsi $Context 'UninstallNew'}
    $state=Get-PresentationRegistration
    if($state.New){throw 'New product remains registered; no downgrade installation is allowed.'}
    if(-not $state.Old){Invoke-PresentationMsi $Context 'InstallOld'}
    $state=Get-PresentationRegistration
    if(-not $state.Old -or $state.New){throw 'Old product registration was not restored.'}
}

function Assert-PresentationPreservation($Context) {
    foreach($pair in @(@('C:\ProgramData\FluxVault\installation.json','1EC27A382AD49190A7EE67630510E49594807251229702B59EA870B4FB4ED617'),
        @('D:\Program Files\PostgreSQL\18\data\pg_hba.conf','83F8A4844E68DCF26C6F068890794D2327F83544DFDA1E8BF95FB9321628E40E'),
        @('D:\Program Files\PostgreSQL\18\data\pg_ident.conf','DD710C85E5064576F4108CDD6862B1A2FAA077D611ED23E31D76CDB56C40E2B3'))) {
        # PostgreSQL has its accepted daemon writer; do not apply executable trust rules to these two read-only pins.
        if((Get-FileHash -LiteralPath $pair[0]).Hash -cne $pair[1]){throw 'Bootstrap/authentication changed; preserve and stop.'}
    }
    foreach($path in @('C:\ProgramData\FluxVault','C:\ProgramData\FluxVault\repository','C:\ProgramData\FluxVault\state','C:\ProgramData\FluxVault\installation.json')) {
        Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $Context.OperatorSid
        $expected=if($path -like '*.json'){'O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)'}else{'O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)'}
        if((Get-Acl -LiteralPath $path).Sddl -cne $expected){throw 'Creator storage boundary changed.'}
    }
    & (Get-Module commission-authentication) {param($c) Assert-CommissionPostmaster $c} $Context
    if(@(Get-Process FluxVault.App,FluxVault.Cli,FluxVault.TestHost -ErrorAction SilentlyContinue).Count){throw 'Exit and join FluxVault clients, preserving drafts, before installation.'}
}

function Assert-PresentationPayload($Candidate) {
    if($Candidate.Payload.Count -ne 208){throw 'Unexpected payload count.'}
    foreach($entry in $Candidate.Payload) {
        if($entry.RelativePath -cnotmatch '^(app|cli|service)\\[^:]+$' -or $entry.RelativePath.Split('\') -contains '..'){throw 'Unexpected payload path.'}
        Assert-PresentationHash (Join-Path 'C:\Program Files\FluxVault' $entry.RelativePath) $entry.Sha256
    }
}

function Set-PresentationService($Context,[bool]$Activate) {
    $svc=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
    if($null -eq $svc -or $svc.StartName -cne 'LocalSystem' -or $svc.PathName -ine '"C:\Program Files\FluxVault\service\FluxVault.Service.exe"'){throw 'Exact LocalSystem service binding is required.'}
    $sc='C:\Windows\System32\sc.exe'
    $commands=if($Activate){@(@('failure','FluxVaultService','reset=','86400','actions=','restart/60000/restart/60000/none/0'),@('config','FluxVaultService','start=','delayed-auto'))}
        else{@(@('config','FluxVaultService','start=','demand'),@('failure','FluxVaultService','reset=','0','actions=',''))}
    if($Activate -and ($svc.State -ne 'Stopped' -or $svc.StartMode -ne 'Manual')){throw 'Verify stopped/demand service before activation.'}
    foreach($command in $commands){$reply=Invoke-PresentationNative $Context $sc $command
        if($reply.ExitCode -ne 0 -or -not $reply.Joined){throw 'Service policy update failed; do not activate.'}}
    $controller=Get-Service FluxVaultService
    try {
        if($Activate){Start-Service FluxVaultService;$controller.WaitForStatus('Running',[TimeSpan]::FromSeconds(30))}
        else{Stop-Service FluxVaultService;$controller.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))}
    }finally{$controller.Dispose()}
}

if($MyInvocation.InvocationName -eq '.'){return}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $repo 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'commission-authentication.psm1') -Force
Assert-PresentationHash (Join-Path $PSScriptRoot 'normal-installed-context.json') '85DF752EB228EB729523CCEDC04B24A5D2143A0600E7076AC0A67F54F988DC83'
$context=Get-Content (Join-Path $PSScriptRoot 'normal-installed-context.json') -Raw|ConvertFrom-Json -AsHashtable -Depth 20
$retainedCandidates=Join-Path $context.WorkRoot 'retained-candidates'
$context.WorkRoot=Join-Path $context.WorkRoot 'presentation-update-1.0.7'
$retainedManifest=Join-Path $retainedCandidates 'a39b83ad153a49d0a6779a5098bb02f2/candidate.json'
Assert-PresentationHash $retainedManifest '6BD79D60D69D6953EA2EB80EF94557AAAA0E0EBFAB4A5F5B473F9C11B670F43A'
$context.Old=Get-Content $retainedManifest -Raw|ConvertFrom-Json -AsHashtable -Depth 12
$manifest=Join-Path $retainedCandidates 'c4e807860b1c49e798b2380652ca5926/candidate.json'
Assert-PresentationHash $manifest '151024FCF7CDA53ED0345A85707E042276A244B80A7FFE71FFB1275276393BE0'
$context.New=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json -AsHashtable -Depth 12
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{if($identity.User.Value -cne $context.OperatorSid -or -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'The intended elevated creator/operator is required.'}}finally{$identity.Dispose()}
if($context.New.CandidateId -cne 'c4e807860b1c49e798b2380652ca5926' -or $context.New.SourceCommit -cne 'e3b57294f40088209637c7fe69aa9d47441abd14'){throw 'Exact frozen candidate is required.'}
$tables=@{}
foreach($candidate in @($context.Old,$context.New)) {
    # Keep frozen manifest bytes unchanged, but resolve packages outside a disposable checkout.
    foreach($entry in $candidate.Installer){
        $entry.Path=Join-Path $retainedCandidates ($candidate.CandidateId+'/installer/'+[IO.Path]::GetFileName($entry.Path))
        Assert-PresentationHash $entry.Path $entry.Sha256
    }
    $msi=@($candidate.Installer|Where-Object Path -like '*.msi')[0]
    $tables[$candidate.Version]=Read-PresentationMsi $msi.Path
    $properties=@{};foreach($row in $tables[$candidate.Version].Property){$properties[$row[0]]=$row[1]}
    $expectedCode=if($candidate.Version -ceq '1.0.6.0'){'{1EEE244A-290D-4F25-94FE-E3AA8384F718}'}else{'{0D47E056-CFFC-4BA4-91A6-3CD47F3FD688}'}
    if($properties.ProductVersion -cne $candidate.Version -or $properties.ProductCode -cne $expectedCode -or $properties.UpgradeCode -cne '{4B89B6E7-D41E-49E6-BE42-09C10D6570D6}'){throw 'Sealed MSI identity does not match the reviewed upgrade.'}
}
Assert-PresentationSettled $context
Assert-PresentationPreservation $context
$state=Get-PresentationRegistration
if($Mode -eq 'Check') {
    if($state.Old -eq $state.New){throw 'Read-only verification requires exactly one reviewed product installed.'}
    $installed=if($state.New){$context.New}else{$context.Old}
    Assert-PresentationPayload $installed
    @{ReadOnly=$true;InstalledVersion=$installed.Version;NewCandidate=$context.New;Registration=$state;MsiTables=$tables;BootstrapAuthenticationAndAclUnchanged=$true;Postmaster=$context.Postmaster;RollbackRestoresOptionsReloadRace=$true}|ConvertTo-Json -Depth 14
    return
}
if(-not $OperationalApprovalRecorded){throw 'Recorded operational approval is required before effects.'}
if(-not(Test-Path -LiteralPath $context.WorkRoot)){[void][IO.Directory]::CreateDirectory($context.WorkRoot)}
Assert-VaultFixtureTrustedPath $context.WorkRoot -AdditionalTrustedOwnerSid $context.OperatorSid
if($Mode -eq 'Update') {
    if(-not $state.Old -or $state.New){throw 'Update requires exactly the old product registration.'}
    Assert-PresentationPayload $context.Old
    Set-PresentationService $context $false
    Invoke-PresentationMsi $context 'Update'
    $state=Get-PresentationRegistration
    if($state.Old -or -not $state.New){throw 'Exact major upgrade registration was not established.'}
    $target=$context.New
}else {
    # A settled failed upgrade can leave neither product installed.
    if(Get-Service FluxVaultService -ErrorAction SilentlyContinue){Set-PresentationService $context $false}
    Restore-PresentationProduct $context
    $target=$context.Old
}
Assert-PresentationPayload $target
Assert-PresentationPreservation $context
Assert-PresentationSettled $context
Set-PresentationService $context $true
Assert-PresentationPreservation $context
$service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
Write-PresentationReceipt $context ($Mode+'-verified') @{Version=$target.Version;PayloadHashesMatched=208;Registration=(Get-PresentationRegistration);Service=$service|Select-Object State,StartMode,StartName,ProcessId,PathName;PreservedBootstrapAuthenticationAndAcl=$true;Postmaster=$context.Postmaster;ObservedUtc=[DateTimeOffset]::UtcNow}
Write-Host "$Mode completed; exact payload and service verified. Live desktop acceptance is separate."
