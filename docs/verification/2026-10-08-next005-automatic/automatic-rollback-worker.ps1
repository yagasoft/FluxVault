# Fixed installed NEXT-005 rollback marker, launched only in the existing owned SYSTEM job.
#Requires -Version 7.2
param([Parameter(Mandatory)][string]$ContextPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ContextSha256)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne 'S-1-5-18'){throw 'SYSTEM rollback marker is required.'}
if([IO.Path]::GetFullPath($ContextPath) -ine (Join-Path $PSScriptRoot 'context.json') -or
    (Get-Item -LiteralPath $ContextPath).Length -gt 65536 -or
    (Get-FileHash -LiteralPath $ContextPath).Hash -cne $ContextSha256){throw 'Frozen marker context changed.'}
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
$context=Get-Content -LiteralPath $ContextPath -Raw|ConvertFrom-Json -AsHashtable
if($context.WorkRoot -ine $PSScriptRoot -or -not $context.RequireAutomaticApproval -or
    $context.OperatorSid -cne 'S-1-5-21-136112424-624261118-1239521417-1001' -or
    $context.ServiceConnection -cne 'host=127.0.0.1 port=5432 dbname=fluxvault_single user=fluxvault_service connect_timeout=3 require_auth=sspi'){
    throw 'Rollback marker scope changed.'
}
foreach($name in @('context.json','inspect-rollback-worker.ps1','vault-windows-fixture.psm1','owned-windows-job.cs','commission-authentication.psm1','postgresql-ancestor-acl.cs')){
    Assert-VaultFixtureTrustedPath (Join-Path $PSScriptRoot $name) -AdditionalTrustedOwnerSid $context.OperatorSid
}
Import-VaultFixtureJobType
[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($context.KernelName)
Add-Type -Path (Join-Path $PSScriptRoot 'postgresql-ancestor-acl.cs')
$service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
if($service.State -cne 'Stopped' -or $service.StartMode -cne 'Manual' -or $service.ProcessId -ne 0 -or
    $service.StartName -cne 'LocalSystem' -or $service.PathName -ine '"C:\Program Files\FluxVault\service\FluxVault.Service.exe"' -or
    @(Get-Process FluxVault.Service -ErrorAction SilentlyContinue).Count){throw 'Join the stopped FluxVault runtime before marking rollback.'}
$auth=Import-Module (Join-Path $PSScriptRoot 'commission-authentication.psm1') -Force -PassThru
$reply=& $auth {param($c)
    Assert-CommissionPostmaster $c
    Assert-CommissionPostgresqlPath $c $c.PsqlPath
    $sql=@'
BEGIN;
SET LOCAL synchronous_commit=on;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='3s';
DO $$ DECLARE relations integer; BEGIN
  PERFORM pg_advisory_xact_lock(5068312438547698252);
  IF NOT pg_try_advisory_xact_lock(5068310265394581335) THEN RAISE EXCEPTION 'Automatic owner is still active'; END IF;
  IF (SELECT count(*) FROM fv_control.vault WHERE singleton AND vault_id='67539278-9292-4870-93cd-a011419228dc' AND owner_sid='S-1-5-21-136112424-624261118-1239521417-1001' AND json_array_length(grants::json)=0) <> 1 THEN RAISE EXCEPTION 'Vault authority changed'; END IF;
  IF EXISTS(SELECT FROM fv_control.operations WHERE command=39 AND state=0) THEN RAISE EXCEPTION 'History deletion is unresolved'; END IF;
  SELECT count(*) INTO relations FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='fv_control' AND c.relname IN ('automatic_identity','automatic_policy','automatic_runtime','automatic_scopes','automatic_work');
  IF relations=0 THEN
    PERFORM set_config('fluxvault.rollback_marker_outcome','absent',true);
    PERFORM set_config('fluxvault.rollback_pending','0',true);
    PERFORM set_config('fluxvault.rollback_enabled','false',true);
  ELSE
    IF relations<>5 OR (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner WHERE n.nspname='fv_control' AND c.relname IN ('automatic_identity','automatic_policy','automatic_runtime','automatic_scopes','automatic_work') AND c.relkind='r' AND c.relpersistence='p' AND r.rolname=current_user AND NOT EXISTS(SELECT FROM aclexplode(COALESCE(c.relacl,acldefault('r',c.relowner))) a WHERE a.grantee<>c.relowner))<>5 THEN RAISE EXCEPTION 'Automatic relations are partial or untrusted'; END IF;
    IF (SELECT count(*) FROM fv_control.automatic_identity WHERE singleton AND schema_version=2) <> 1 THEN RAISE EXCEPTION 'Automatic extension version changed'; END IF;
    UPDATE fv_control.automatic_policy SET approval_required=true WHERE singleton AND vault_id='67539278-9292-4870-93cd-a011419228dc';
    IF NOT FOUND THEN RAISE EXCEPTION 'Automatic policy missing'; END IF;
    PERFORM set_config('fluxvault.rollback_marker_outcome','invalidated',true);
    PERFORM set_config('fluxvault.rollback_pending',(SELECT count(*)::text FROM fv_control.automatic_work WHERE pending),true);
    PERFORM set_config('fluxvault.rollback_enabled',(SELECT enabled::text FROM fv_control.automatic_policy WHERE singleton),true);
  END IF;
END $$;
SELECT json_build_object('AutomaticApprovalRequired',current_setting('fluxvault.rollback_marker_outcome')='invalidated',
  'AutomaticExtensionAbsent',current_setting('fluxvault.rollback_marker_outcome')='absent',
  'PreferenceRetained',current_setting('fluxvault.rollback_enabled')::boolean,
  'PendingHistoryDeletions',0,'OwnerSid','S-1-5-21-136112424-624261118-1239521417-1001','GrantCount',0,
  'PendingWork',current_setting('fluxvault.rollback_pending')::bigint);
COMMIT;
'@
    Invoke-CommissionTool $c $c.PsqlPath @('-X','-w','-q','-A','-t','-v','ON_ERROR_STOP=1','--dbname',$c.ServiceConnection) $sql
} $context
if(-not $reply.Joined -or $reply.ExitCode -ne 0){throw 'Durable rollback marker did not complete; do not downgrade.'}
$state=$reply.Output.Trim()|ConvertFrom-Json -AsHashtable
if(-not ($state.AutomaticApprovalRequired -or $state.AutomaticExtensionAbsent)){throw 'Automatic rollback safety was not established.'}
$result=$state
$result.ReadOnly=$false;$result.Tool=$reply;$result.ObservedUtc=[DateTimeOffset]::UtcNow
$stream=[IO.FileStream]::new((Join-Path $PSScriptRoot 'result.json'),'CreateNew','Write','None')
try{$bytes=[Text.Encoding]::UTF8.GetBytes(($result|ConvertTo-Json -Depth 6));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
