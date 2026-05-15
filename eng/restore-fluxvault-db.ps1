param(
    [Parameter(Mandatory = $true)]
    [string]$BackupRoot,
    [string]$DatabaseName = "fluxvault_metadata",
    [string]$HostName = "localhost",
    [int]$Port = 5432,
    [string]$Username = "fluxvault",
    [switch]$CreateDatabase,
    [string]$PgRestorePath = "pg_restore",
    [string]$CreatedbPath = "createdb",
    [string]$PsqlPath = "psql",
    [string]$ConfigDestination = "$env:ProgramData\FluxVault\config.json"
)

$ErrorActionPreference = "Stop"

function Resolve-Tool([string]$ToolPath) {
    $command = Get-Command $ToolPath -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$ToolPath was not found. Add PostgreSQL bin to PATH or pass an explicit tool path."
    }

    return $command.Source
}

if (-not (Test-Path -LiteralPath $BackupRoot)) {
    throw "BackupRoot was not found: $BackupRoot"
}

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535."
}

$pgRestore = Resolve-Tool $PgRestorePath
$psql = Resolve-Tool $PsqlPath
$dumpPath = Join-Path $BackupRoot "fluxvault-metadata.dump"
$manifestPath = Join-Path $BackupRoot "backup-manifest.json"
if (-not (Test-Path -LiteralPath $dumpPath)) {
    throw "Backup dump was not found: $dumpPath"
}

if (Test-Path -LiteralPath $manifestPath) {
    Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json | Out-Null
}

& $psql `
    --host=$HostName `
    --port=$Port `
    --username=$Username `
    --dbname=postgres `
    --command="select version();"
if ($LASTEXITCODE -ne 0) {
    throw "Could not connect to PostgreSQL. Install/start PostgreSQL or update restore parameters."
}

if ($CreateDatabase) {
    $createdb = Resolve-Tool $CreatedbPath
    & $createdb `
        --host=$HostName `
        --port=$Port `
        --username=$Username `
        --encoding=UTF8 `
        $DatabaseName
    if ($LASTEXITCODE -ne 0) {
        Write-Host "createdb returned $LASTEXITCODE. Continuing; the database may already exist."
    }
}

& $pgRestore `
    --clean `
    --if-exists `
    --host=$HostName `
    --port=$Port `
    --username=$Username `
    --dbname=$DatabaseName `
    $dumpPath
if ($LASTEXITCODE -ne 0) {
    throw "pg_restore failed with exit code $LASTEXITCODE."
}

$configSource = Join-Path $BackupRoot "config.json"
if (Test-Path -LiteralPath $configSource) {
    $configDirectory = Split-Path -Parent $ConfigDestination
    New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
    Copy-Item -LiteralPath $configSource -Destination $ConfigDestination -Force
}

$journalRoot = Join-Path $BackupRoot "metadata-journal"
if (Test-Path -LiteralPath $journalRoot) {
    $count = (Get-ChildItem -LiteralPath $journalRoot -Filter "*.fvop" -Recurse | Measure-Object).Count
    Write-Host "Metadata journal files available for replay after service import support is enabled: $count"
}

Write-Host "FluxVault metadata database restored from $BackupRoot"
