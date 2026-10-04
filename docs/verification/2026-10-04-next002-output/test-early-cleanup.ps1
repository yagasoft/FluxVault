# Run from the repository root with the authorised elevated Windows fixture runner.
$ErrorActionPreference='Stop'
Import-Module ./eng/fixtures/vault-windows-fixture.psm1 -Force
$earlyId=[guid]::NewGuid().ToString('N')
$earlyParent='C:\ProgramData\FluxVault.Tests\NEXT002'
$earlyRoot=Resolve-VaultFixtureRoot (Join-Path $earlyParent $earlyId) $earlyParent $earlyId
$earlyJournal=$null
try {
    New-VaultFixtureProtectedDirectory $earlyRoot
    $earlyJournal=New-VaultFixtureJournal $earlyRoot $earlyParent $earlyId
    Complete-VaultFixtureJournal $earlyJournal
    # Reproduce failure before runtime or output directories have been created.
    & ./eng/fixtures/cleanup-windows-fixture-outputs.ps1 -Root $earlyRoot -FixtureId $earlyId
    if(Test-Path -LiteralPath (Join-Path $earlyRoot 'runtime')){throw 'Runtime unexpectedly created.'}
    if(Test-Path -LiteralPath (Join-Path $earlyRoot 'output-cleanup-owner.json')){throw 'Unnecessary helper admitted.'}
    if(Get-ScheduledTask -TaskName 'FluxVault-NEXT002-261003-SYSTEM' -ErrorAction SilentlyContinue){throw 'Helper task remains.'}
    Remove-VaultFixtureTree $earlyRoot $earlyParent $earlyId
    @{FixtureId=$earlyId;EarlyFailureCleanupPassed=$true;RootRemoved=(-not(Test-Path -LiteralPath $earlyRoot));HelperNotCreated=$true} |
        ConvertTo-Json | Set-Content ./docs/verification/2026-10-04-next002-output/early-cleanup.json
} finally {
    if($null -ne $earlyJournal -and (Test-Path -LiteralPath $earlyRoot)){Remove-VaultFixtureTree $earlyRoot $earlyParent $earlyId}
}
