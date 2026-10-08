# Read-only installed-catalogue inspection. The operator owns the SYSTEM job.
#Requires -Version 7.2
param([Parameter(Mandatory)][string]$ContextPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ContextSha256)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne 'S-1-5-18'){throw 'SYSTEM inspection is required.'}
if([IO.Path]::GetFullPath($ContextPath) -ine (Join-Path $PSScriptRoot 'context.json') -or
    (Get-Item -LiteralPath $ContextPath).Length -gt 65536 -or
    (Get-FileHash -LiteralPath $ContextPath).Hash -cne $ContextSha256){throw 'Frozen inspection context changed.'}
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
$context=Get-Content -LiteralPath $ContextPath -Raw | ConvertFrom-Json -AsHashtable
if($context.WorkRoot -ine $PSScriptRoot -or $context.OperatorSid -cne 'S-1-5-21-136112424-624261118-1239521417-1001' -or
    $context.ServiceConnection -cne 'host=127.0.0.1 port=5432 dbname=fluxvault_single user=fluxvault_service connect_timeout=3 require_auth=sspi'){
    throw 'Inspection scope changed.'
}
foreach($name in @('context.json','inspect-rollback-worker.ps1','vault-windows-fixture.psm1','owned-windows-job.cs','commission-authentication.psm1','postgresql-ancestor-acl.cs')){
    Assert-VaultFixtureTrustedPath (Join-Path $PSScriptRoot $name) -AdditionalTrustedOwnerSid $context.OperatorSid
}
Import-VaultFixtureJobType
[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($context.KernelName)
Add-Type -Path (Join-Path $PSScriptRoot 'postgresql-ancestor-acl.cs')
$self=Get-Process -Id $PID
try{$identity=Get-VaultFixtureProcessIdentity $self}finally{$self.Dispose()}
$auth=Import-Module (Join-Path $PSScriptRoot 'commission-authentication.psm1') -Force -PassThru
$reply=& $auth {param($c)
    Assert-CommissionPostmaster $c
    Assert-CommissionPostgresqlPath $c $c.PsqlPath
    Invoke-CommissionTool $c $c.PsqlPath @('-X','-w','-q','-A','-t','-v','ON_ERROR_STOP=1','--dbname',$c.ServiceConnection) `
        "BEGIN READ ONLY; SELECT json_build_object('PendingHistoryDeletions',(SELECT count(*) FROM fv_control.operations WHERE command=39 AND state=0),'OwnerSid',owner_sid,'GrantCount',json_array_length(grants::json)) FROM fv_control.vault WHERE singleton=true; COMMIT;"
} $context
if(-not $reply.Joined -or $reply.ExitCode -ne 0){throw 'Read-only inspection did not complete.'}
$state=$reply.Output|ConvertFrom-Json -AsHashtable
if($state.PendingHistoryDeletions -lt 0 -or $state.GrantCount -lt 0 -or $state.OwnerSid -cnotmatch '^S-1-5-21-\d+-\d+-\d+-\d+$'){throw 'Invalid rollback inspection state.'}
$result=@{PendingHistoryDeletions=[long]$state.PendingHistoryDeletions;OwnerSid=$state.OwnerSid;GrantCount=[int]$state.GrantCount;ReadOnly=$true;Worker=$identity;Tool=$reply;ObservedUtc=[DateTimeOffset]::UtcNow}
$stream=[IO.FileStream]::new((Join-Path $PSScriptRoot 'result.json'),'CreateNew','Write','None')
try{$bytes=[Text.Encoding]::UTF8.GetBytes(($result|ConvertTo-Json -Depth 6));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
