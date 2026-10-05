#Requires -Version 7.2
param([Parameter(Mandatory)][string]$ContextPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ContextSha256,
    [switch]$RestoreAuthentication)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
trap {
    try {
        $path=Join-Path $PSScriptRoot 'setup-error.json'
        $stream=[IO.FileStream]::new($path,'CreateNew','Write','None')
        try{$bytes=[Text.Encoding]::UTF8.GetBytes((@{Error=$_.Exception.Message;ContextSha256=$ContextSha256}|ConvertTo-Json -Compress));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
    }catch{}
    throw
}
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
if([IO.Path]::GetFullPath($ContextPath) -ine (Join-Path $PSScriptRoot 'context.json') -or
    (Get-Item -LiteralPath $ContextPath).Length -gt 65536 -or (Get-FileHash -LiteralPath $ContextPath).Hash -cne $ContextSha256){throw 'Setup context changed before dispatch.'}
$context=Get-Content -LiteralPath $ContextPath -Raw|ConvertFrom-Json -AsHashtable -Depth 12
if($context.WorkRoot -ine $PSScriptRoot -or (-not $RestoreAuthentication -and -not $context.NormalInstallation)) {throw 'Setup requires the exact normal working root.'}
if(-not $context.NormalInstallation -and ($context.Port -eq 5432 -or $context.DataDirectory -ieq 'D:\Program Files\PostgreSQL\18\data')){throw 'A disposable rollback cannot address normal PostgreSQL.'}
$context.IdentitySha256=$ContextSha256
Assert-VaultFixtureTrustedPath $ContextPath -AdditionalTrustedOwnerSid $context.OperatorSid
if($context.NormalInstallation){Add-Type -Path (Join-Path $PSScriptRoot 'postgresql-ancestor-acl.cs')}
[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($context.SetupJob.KernelName)
$auth=Import-Module (Join-Path $PSScriptRoot 'commission-authentication.psm1') -Force -PassThru
& $auth {Assert-CommissionSystemWorker}
if($RestoreAuthentication) {
    & $auth {
        param($Context)
        Assert-CommissionPostmaster $Context
        $pins=@{};$original=@{}
        try {
            foreach($entry in @(@('pg_hba.conf','FinalHba','TemporaryHba'),@('pg_ident.conf','FinalIdent','TemporaryIdent'))) {
                $name=$entry[0];$path=Join-Path $Context.DataDirectory $name
                Assert-CommissionAuthenticationPath $Context $path
                $pins[$name]=[IO.FileStream]::new($path,'Open','ReadWrite','Read')
                $stream=$pins[$name];if($stream.Length -gt 1048576){throw 'Rollback authentication exceeds its bound.'}
                $observed=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($observed)
                $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($observed))
                $known=@($Context.OriginalHashes[$name],$Context.Prepared[$entry[1]].Sha256,$Context.Prepared[$entry[2]].Sha256)
                if($name -eq 'pg_ident.conf'){$known+=$Context.Prepared.ProbeIdent.Sha256}
                if($hash -cnotin $known -and -not(Test-CommissionInterruptedWrite $Context $name $observed $known)){throw 'External authentication change; rollback is blocked.'}
                $backup=Join-Path $Context.WorkRoot ('original-'+$name)
                if(Test-Path -LiteralPath $backup) {
                    Assert-VaultFixtureTrustedPath $backup -AdditionalTrustedOwnerSid $Context.OperatorSid
                    if((Get-FileHash -LiteralPath $backup).Hash -cne $Context.OriginalHashes[$name]){throw 'Original rollback bytes changed.'}
                    $original[$name]=[IO.File]::ReadAllBytes($backup)
                }elseif($hash -ceq $Context.OriginalHashes[$name]){$original[$name]=$observed}
                else{throw 'Original rollback bytes are missing.'}
            }
            Set-CommissionAuthenticationBytes $Context $pins $original['pg_hba.conf'] $original['pg_ident.conf']
        }finally{foreach($pin in $pins.Values){$pin.Dispose()}}
        $denied=Invoke-CommissionQuery $Context $Context.Connection 'SELECT 1;' -AllowRefusal
        if($denied.ExitCode -eq 0){throw 'Temporary administrator still admitted after rollback.'}
        foreach($name in $Context.OriginalHashes.Keys){if((Get-FileHash (Join-Path $Context.DataDirectory $name)).Hash -cne $Context.OriginalHashes[$name]){throw 'Original authentication restoration is unconfirmed.'}}
        # Query through the restored legacy admission; never terminate other sessions.
        $baseline=if(Test-Path (Join-Path $Context.WorkRoot 'administrator-baseline.json')){Read-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'administrator-baseline.json') 8192}else{@{BackendPids=@()}}
        $reply=Invoke-CommissionQuery $Context $Context.LegacyConnection "SELECT COALESCE(json_agg(pid),'[]'::json) FROM pg_stat_activity WHERE usename='postgres';"
        $pids=@($reply.Output.Trim()|ConvertFrom-Json)
        if(@($pids|Where-Object {$_ -notin @($baseline.BackendPids)}).Count){throw 'New administrator backend survives; rollback remains stopped.'}
        Assert-CommissionPostmaster $Context
        Write-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'rollback-authentication.json') @{ContextSha256=$Context.IdentitySha256;Restored=$true;AdministratorRetired=$true;Postmaster=$Context.Postmaster}
    } $context
}else {
    $self=Get-Process -Id $PID
    try{$context.SetupWorkerIdentity=Get-VaultFixtureProcessIdentity $self}finally{$self.Dispose()}
    Import-Module (Join-Path $PSScriptRoot 'commission-setup.psm1') -Force
    Invoke-CommissionSetup $context|ConvertTo-Json -Depth 8|Write-Output
}
