param(
    [string]$DatabaseName = "fluxvault_metadata",
    [string]$HostName = "localhost",
    [int]$Port = 5432,
    [string]$Username = "fluxvault",
    [string]$OutputDirectory = "$env:ProgramData\FluxVault\db-backups",
    [string]$ConfigPath = "$env:ProgramData\FluxVault\config.json",
    [string]$RepositoryPath = "$env:ProgramData\FluxVault\repository",
    [switch]$IncludeMetadataJournal,
    [int]$RetentionDays = 30,
    [string]$PgDumpPath = "pg_dump"
)

$ErrorActionPreference = "Stop"

function Resolve-Tool([string]$ToolPath) {
    $command = Get-Command $ToolPath -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$ToolPath was not found. Add PostgreSQL bin to PATH or pass -PgDumpPath."
    }

    return $command.Source
}

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535."
}

if ($RetentionDays -lt 1) {
    throw "RetentionDays must be at least 1."
}

$pgDump = Resolve-Tool $PgDumpPath
$timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
$backupRoot = Join-Path $OutputDirectory "FluxVault-metadata-$timestamp"
$dumpPath = Join-Path $backupRoot "fluxvault-metadata.dump"
$manifestPath = Join-Path $backupRoot "backup-manifest.json"

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

& $pgDump `
    --format=custom `
    --file=$dumpPath `
    --host=$HostName `
    --port=$Port `
    --username=$Username `
    --dbname=$DatabaseName
if ($LASTEXITCODE -ne 0) {
    throw "pg_dump failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $ConfigPath) {
    Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $backupRoot "config.json") -Force
}

if ($IncludeMetadataJournal) {
    $journalRoot = Join-Path $RepositoryPath "metadata-journal"
    if (Test-Path -LiteralPath $journalRoot) {
        Copy-Item -LiteralPath $journalRoot -Destination (Join-Path $backupRoot "metadata-journal") -Recurse -Force
    }
}

[ordered]@{
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("u")
    databaseName = $DatabaseName
    host = $HostName
    port = $Port
    username = $Username
    dump = (Split-Path -Leaf $dumpPath)
    configIncluded = (Test-Path -LiteralPath (Join-Path $backupRoot "config.json"))
    metadataJournalIncluded = (Test-Path -LiteralPath (Join-Path $backupRoot "metadata-journal"))
    schemaVersion = 1
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$cutoff = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem -LiteralPath $OutputDirectory -Directory -Filter "FluxVault-metadata-*" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt $cutoff -and $_.FullName -ne $backupRoot } |
    Remove-Item -Recurse -Force

Write-Host "FluxVault metadata backup written to $backupRoot"
