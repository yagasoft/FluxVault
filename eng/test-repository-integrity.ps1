#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FixtureId,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateRange(60,600)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'

# Run only after authorisation of the named Windows fixture plus the G01 database/control
# extension. Never build, launch unprotected PostgreSQL binaries, enable trust for
# repository connections or change the installed service/cluster from this entry point.
& (Join-Path $PSScriptRoot 'test-windows-database-boundary.ps1') -FixtureId $FixtureId `
    -EvidenceDirectory $EvidenceDirectory -RunIntegrityTests -IntegrityTimeoutSeconds $TimeoutSeconds
