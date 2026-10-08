# Read-only installation/rollback observations. This script does not provision, stop, reload or move anything.
param([string]$OutputPath=(Join-Path $PSScriptRoot 'staging-preflight.json'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$worktree=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $worktree 'eng/fixtures/vault-windows-fixture.psm1') -Force
$installationRoot='C:\Program Files\FluxVault'
$legacyRoot='C:\ProgramData\FluxVault'
$targetId='7871ff7f8d1b404db20771f2e742364f'
$manifest=Get-Content -LiteralPath (Join-Path $worktree 'docs/verification/2026-10-01-verified-recovery/installed-payload-v104.json') -Raw | ConvertFrom-Json -AsHashtable
$payload=@(foreach($relative in $manifest.payload_file_sha256.Keys){
    $path=[IO.Path]::GetFullPath((Join-Path $installationRoot $relative))
    if(-not $path.StartsWith($installationRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Retained payload path escapes the installation.'}
    $item=Get-Item -LiteralPath $path
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Retained payload is a reparse point.'}
    $hash=(Get-FileHash -LiteralPath $path).Hash
    if($hash -ne $manifest.payload_file_sha256[$relative]){throw 'Installed v1.0.4 payload differs from retained rollback evidence.'}
    @{Path=$relative;Sha256=$hash;Bytes=$item.Length}
})
$setup='E:\Drive\Work (1)\Code\FluxVault\artifacts\release-integrity-v104\Yagasoft-FluxVault-v1.0.4-win-x64-Setup.exe'
$setupHash=(Get-FileHash -LiteralPath $setup).Hash
if($setupHash -ne 'DD4C47DC6B20C0B348691289481965002466ACDF373CEA26E5D823AB9AF84F61'){throw 'Retained v1.0.4 setup differs.'}
$services=@(foreach($name in 'postgresql-x64-18','FluxVaultService'){
    $service=Get-CimInstance Win32_Service -Filter "Name='$name'"
    if($service.State -ne 'Running'){throw 'An existing staging service is not running.'}
    $process=Get-Process -Id $service.ProcessId
    try{@{Name=$name;State=$service.State;StartMode=$service.StartMode;StartName=$service.StartName;
        PathName=$service.PathName;Identity=(Get-VaultFixtureProcessIdentity $process)}}finally{$process.Dispose()}
})
$files=@(foreach($path in 'C:\ProgramData\FluxVault\config.json','D:\Program Files\PostgreSQL\18\data\pg_hba.conf','D:\Program Files\PostgreSQL\18\data\pg_ident.conf'){
    @{Path=$path;Sha256=(Get-FileHash -LiteralPath $path).Hash}
})
$legacy=Get-Item -LiteralPath $legacyRoot
if($legacy.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Legacy root is a reparse point.'}
$acl=Get-Acl -LiteralPath $legacyRoot
$configuration=Get-Content -LiteralPath (Join-Path $legacyRoot 'config.json') -Raw | ConvertFrom-Json
$active=$configuration.activeProfile.configuration
$clients=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and
    $_.ExecutablePath.StartsWith($installationRoot+'\',[StringComparison]::OrdinalIgnoreCase) -and
    $_.ProcessId -notin @($services.Identity.ProcessId)} | Select-Object Name,ProcessId,ExecutablePath)
$paths=@(foreach($path in @(('C:\ProgramData\FluxVault.Rollback.'+$targetId),('C:\ProgramData\FluxVault.Failed.'+$targetId))){
    $resolved=[IO.Path]::GetFullPath($path)
    if(-not $resolved.StartsWith('C:\ProgramData\FluxVault.',[StringComparison]::OrdinalIgnoreCase)){throw 'Proposed preservation path is outside its explicit target.'}
    if(Test-Path -LiteralPath $resolved){throw 'Proposed preservation path already exists.'}
    @{Path=$resolved;Absent=$true}
})
if($paths.Count -ne 2 -or @($paths | Where-Object {$_.Path -notmatch '^C:\\ProgramData\\FluxVault\.(Rollback|Failed)\.[a-f0-9]{32}$'}).Count){
    throw 'Exact two preservation paths are required.'
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{$creator=$identity.User.Value}finally{$identity.Dispose()}
if($creator -ne 'S-1-5-21-136112424-624261118-1239521417-1001'){throw 'The intended native operator changed.'}
$serviceRegistry=Get-Item -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\FluxVaultService'
try{
    $servicePolicy=@(foreach($valueName in @('ImagePath','ObjectName','Start','Type','ErrorControl','DelayedAutoStart','FailureActions',
        'FailureActionsOnNonCrashFailures','ServiceSidType','RequiredPrivileges','DependOnService','PreshutdownTimeout')){
        if($serviceRegistry.GetValueNames() -contains $valueName){
            @{Name=$valueName;Kind=$serviceRegistry.GetValueKind($valueName).ToString();Value=$serviceRegistry.GetValue($valueName)}
        }
    })
}finally{$serviceRegistry.Dispose()}
@{ObservedUtc=[DateTime]::UtcNow.ToString('o');ReadOnly=$true;TargetId=$targetId;NativeIntendedCreatorSid=$creator;
    ExistingServices=$services;ExistingServicePolicy=$servicePolicy;ExistingFiles=$files;InstalledV104Payload=$payload;MatchedPayloadFiles=$payload.Count;
    RollbackSetup=@{Path=$setup;Sha256=$setupHash};LegacyRoot=@{Path=$legacyRoot;Owner=$acl.Owner;Sddl=$acl.Sddl;
        Protected=$acl.AreAccessRulesProtected;RepositoryPath=$active.repositoryPath;MetadataHost=$active.metadataStore.host;
        MetadataDatabase=$active.metadataStore.databaseName;MetadataRole=$active.metadataStore.username};
    LiveInstalledClients=$clients;ProposedPreservationPaths=$paths;
    ProposedFreshEndpoint=@{Host='localhost';Port=5432;Database='fluxvault_single';Role='fluxvault_service';NameCollisionsChecked=$false};
    OperationalApproval='pending';ProvisioningImplemented=$true} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
'Retained installed payload and setup match; read-only staging/rollback inputs recorded. No system change performed.'
