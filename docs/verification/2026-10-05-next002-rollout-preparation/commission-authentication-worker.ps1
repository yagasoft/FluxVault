#Requires -Version 7.2
param([Parameter(Mandatory)][string]$ContextPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ContextSha256)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
trap {
    $message=$_.Exception.Message;if($message.Length -gt 2048){$message=$message.Substring(0,2048)}
    try {
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes((@{Error=$message}|ConvertTo-Json -Compress))
        $errorFile=[IO.FileStream]::new((Join-Path $PSScriptRoot 'worker-error.json'),'CreateNew','Write','None')
        try{$errorFile.Write($bytes);$errorFile.Flush($true)}finally{$errorFile.Dispose()}
    } catch {}
    throw
}
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
if([IO.Path]::GetFullPath($ContextPath) -ine (Join-Path $PSScriptRoot 'context.json') -or
    (Get-Item -LiteralPath $ContextPath).Length -gt 65536 -or (Get-FileHash -LiteralPath $ContextPath).Hash -cne $ContextSha256){throw 'Authentication context changed before dispatch.'}
$context=Get-Content -LiteralPath $ContextPath -Raw | ConvertFrom-Json -AsHashtable -Depth 8
if($context.WorkRoot -ine $PSScriptRoot){throw 'Authentication working root changed.'}
$context.IdentitySha256=$ContextSha256
Assert-VaultFixtureTrustedPath $ContextPath -AdditionalTrustedOwnerSid $context.OperatorSid
if($context.NormalInstallation){Add-Type -Path (Join-Path $PSScriptRoot 'postgresql-ancestor-acl.cs')}
[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($context.WorkerJob.KernelName)
Import-Module (Join-Path $PSScriptRoot 'commission-administrator.psm1') -Force
$auth=Import-Module (Join-Path $PSScriptRoot 'commission-authentication.psm1') -Force -PassThru
& $auth {Assert-CommissionSystemWorker}
$self=Get-Process -Id $PID
try{$identity=Get-VaultFixtureProcessIdentity $self}finally{$self.Dispose()}
& $auth {param($path,$receipt) Write-CommissionAuthenticationReceipt $path $receipt} `
    (Join-Path $PSScriptRoot 'authentication-worker.json') @{ContextSha256=$ContextSha256;Process=$identity;WindowsSid='S-1-5-18'}
Invoke-CommissionAuthentication $context | ConvertTo-Json -Depth 8 | Write-Output
