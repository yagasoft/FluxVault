#Requires -Version 7.2
param([Parameter(Mandatory)][string]$ContextPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ContextSha256,
    [ValidateRange(15,180)][int]$TimeoutSeconds=120)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
trap {
    $message=$_.Exception.Message;if($message.Length -gt 2048){$message=$message.Substring(0,2048)}
    try {
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes((@{Error=$message}|ConvertTo-Json -Compress))
        $errorFile=[IO.FileStream]::new((Join-Path $PSScriptRoot 'cleanup-error.json'),'CreateNew','Write','None')
        try{$errorFile.Write($bytes);$errorFile.Flush($true)}finally{$errorFile.Dispose()}
    } catch {}
    throw
}
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
if([IO.Path]::GetFullPath($ContextPath) -ine (Join-Path $PSScriptRoot 'context.json') -or
    (Get-Item -LiteralPath $ContextPath).Length -gt 65536 -or (Get-FileHash -LiteralPath $ContextPath).Hash -cne $ContextSha256){throw 'Cleanup context changed before dispatch.'}
$context=Get-Content -LiteralPath $ContextPath -Raw | ConvertFrom-Json -AsHashtable -Depth 8
if($context.WorkRoot -ine $PSScriptRoot){throw 'Cleanup working root changed.'}
$context.IdentitySha256=$ContextSha256
Assert-VaultFixtureTrustedPath $ContextPath -AdditionalTrustedOwnerSid $context.OperatorSid
if($context.NormalInstallation){Add-Type -Path (Join-Path $PSScriptRoot 'postgresql-ancestor-acl.cs')}
Import-Module (Join-Path $PSScriptRoot 'commission-authentication.psm1') -Force
# This task must be independent of the operator/authentication kill-on-close job.
# Its bounded psql/reload children are joined by the shared operator procedure.
Invoke-CommissionAuthenticationCleanupWatch $context -TimeoutSeconds $TimeoutSeconds | ConvertTo-Json -Depth 8 | Write-Output
