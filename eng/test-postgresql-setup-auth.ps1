[CmdletBinding()]
param([string]$SetupScriptPath = (Join-Path $PSScriptRoot 'setup-fluxvault-postgresql.ps1'))

$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $SetupScriptPath).Path
$parseTokens = $null
$parseErrors = $null
$source = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Setup script has parse errors.' }
$functionNames = @('Set-ManagedPgHbaBlock', 'Find-PgHbaInsertionIndex', 'Set-FluxVaultTrust', 'Set-TemporaryAdminTrust')
foreach ($name in $functionNames) {
    $definition = @($source.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object Name -eq $name)
    if ($definition.Count -ne 1) { throw "Expected one setup function: $name" }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}

$DatabaseName = 'fluxvault_metadata'
$Username = 'fluxvault'
$PostgresAdminUsername = 'postgres'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('FluxVault.Setup.AuthRegression/' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$hbaPath = Join-Path $scratch 'pg_hba.conf'
$original = "# Ordinary server authentication`r`nhost all all 127.0.0.1/32 scram-sha-256`r`nhost all all ::1/128 scram-sha-256`r`n"
try {
    [IO.File]::WriteAllText($hbaPath, $original, [Text.Encoding]::ASCII)
    Set-TemporaryAdminTrust -DataDirectory $scratch -Enabled $true
    Set-FluxVaultTrust -DataDirectory $scratch
    Set-TemporaryAdminTrust -DataDirectory $scratch -Enabled $false
    $final = [IO.File]::ReadAllText($hbaPath)
    foreach ($required in @('# FluxVault PostgreSQL local trust BEGIN', 'host fluxvault_metadata fluxvault 127.0.0.1/32 trust', 'host fluxvault_metadata fluxvault ::1/128 trust', '# FluxVault PostgreSQL local trust END')) {
        if (-not $final.Contains($required)) { throw "Scoped runtime access disappeared during administrator cleanup: $required" }
    }
    if ($final.Contains('temporary admin trust') -or $final.Contains('host postgres postgres')) { throw 'Temporary administrator access survived cleanup.' }
    $ordinaryLines = @($final -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -notlike '# FluxVault PostgreSQL local trust *' -and $_ -notlike 'host fluxvault_metadata fluxvault *' })
    if (($ordinaryLines -join "`n") -cne ($original.TrimEnd() -replace "`r`n", "`n")) { throw 'Ordinary server rules changed.' }

    # Repeated provisioning must preserve one independent runtime block.
    Set-TemporaryAdminTrust -DataDirectory $scratch -Enabled $true
    Set-FluxVaultTrust -DataDirectory $scratch
    Set-TemporaryAdminTrust -DataDirectory $scratch -Enabled $false
    $replayed = [IO.File]::ReadAllText($hbaPath)
    if ([regex]::Matches($replayed, '# FluxVault PostgreSQL local trust BEGIN').Count -ne 1) { throw 'Repeated setup duplicated or removed the runtime block.' }
    if ($replayed.Contains('temporary admin trust')) { throw 'Repeated setup retained administrator access.' }
    Write-Output 'PASS: administrator cleanup preserves one scoped runtime block and ordinary authentication rules.'
} finally {
    # This invocation created both the file and its GUID directory.
    if ([IO.File]::Exists($hbaPath)) { [IO.File]::Delete($hbaPath) }
    [IO.Directory]::Delete($scratch, $false)
}
