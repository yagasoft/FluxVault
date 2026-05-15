param(
    [int]$PostgreSqlMajorVersion = 18,
    [string]$WingetPackageId = "PostgreSQL.PostgreSQL.18",
    [string]$ServiceName = "postgresql-x64-18",
    [int]$Port = 5432,
    [string]$DatabaseName = "fluxvault_metadata",
    [string]$Username = "fluxvault",
    [string]$ProgramDataPath = "$env:ProgramData\FluxVault",
    [string]$RepositoryPath = "$env:ProgramData\FluxVault\repository",
    [string]$PostgresAdminUsername = "postgres",
    [string]$PostgresAdminPassword,
    [switch]$AllowTemporaryAdminTrustForExistingServer,
    [switch]$ConfigOnly,
    [string]$CliPath
)

$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-SqlIdentifier([string]$Value, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "$Name must contain only letters, numbers, and underscores, and must not start with a number."
    }
}

function Resolve-CommandPath([string]$CommandName, [string]$FallbackPath) {
    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($FallbackPath) -and (Test-Path -LiteralPath $FallbackPath)) {
        return $FallbackPath
    }

    throw "$CommandName was not found. Add PostgreSQL bin to PATH or install PostgreSQL $PostgreSqlMajorVersion."
}

function Get-ServiceInfo {
    return Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
}

function Get-PostgreSqlBinDirectory {
    $psql = Get-Command "psql" -ErrorAction SilentlyContinue
    if ($null -ne $psql) {
        return Split-Path -Parent $psql.Source
    }

    $candidate = Join-Path ${env:ProgramFiles} "PostgreSQL\$PostgreSqlMajorVersion\bin"
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }

    return $candidate
}

function Get-PostgreSqlDataDirectory {
    $service = Get-ServiceInfo
    if ($null -eq $service) {
        throw "PostgreSQL service was not found: $ServiceName"
    }

    $pathName = $service.PathName
    if ($pathName -match '(?i)(?:^|\s)-D\s+"([^"]+)"') {
        return $Matches[1]
    }

    if ($pathName -match '(?i)(?:^|\s)-D\s+([^\s]+)') {
        return $Matches[1]
    }

    throw "Could not find PostgreSQL data directory in service command line: $pathName"
}

function Wait-ServiceRunning {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
            return
        }

        if ($null -ne $service -and $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Start-Service -Name $ServiceName
        }

        Start-Sleep -Seconds 1
    }

    throw "PostgreSQL service did not reach Running state: $ServiceName"
}

function Install-PostgreSql {
    $winget = Get-Command "winget" -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        throw "winget was not found. Install PostgreSQL $PostgreSqlMajorVersion manually or add winget to PATH."
    }

    if ([string]::IsNullOrWhiteSpace($script:EffectiveAdminPassword)) {
        $script:EffectiveAdminPassword = [Convert]::ToBase64String([Guid]::NewGuid().ToByteArray()) + "Aa1!"
    }

    $override = "--mode unattended --unattendedmodeui none --serverport $Port --servicename `"$ServiceName`" --superpassword `"$script:EffectiveAdminPassword`""
    Write-Host "Installing PostgreSQL $PostgreSqlMajorVersion with winget package $WingetPackageId..."
    & $winget.Source install --id $WingetPackageId --exact --silent --accept-package-agreements --accept-source-agreements --override $override
    if ($LASTEXITCODE -ne 0) {
        throw "winget PostgreSQL install failed with exit code $LASTEXITCODE."
    }
}

function Set-ManagedPgHbaBlock {
    param(
        [string]$PgHbaPath,
        [string]$BeginMarker,
        [string]$EndMarker,
        [string[]]$Lines
    )

    if (-not (Test-Path -LiteralPath $PgHbaPath)) {
        throw "pg_hba.conf was not found: $PgHbaPath"
    }

    $content = Get-Content -LiteralPath $PgHbaPath
    $output = New-Object System.Collections.Generic.List[string]
    $inside = $false
    foreach ($line in $content) {
        if ($line -eq $BeginMarker) {
            $inside = $true
            continue
        }

        if ($line -eq $EndMarker) {
            $inside = $false
            continue
        }

        if (-not $inside) {
            $output.Add($line)
        }
    }

    if ($Lines.Count -gt 0) {
        $insertIndex = Find-PgHbaInsertionIndex -Lines $output.ToArray()
        $managedBlock = New-Object System.Collections.Generic.List[string]
        if ($insertIndex -gt 0 -and -not [string]::IsNullOrWhiteSpace($output[$insertIndex - 1])) {
            $managedBlock.Add("")
        }

        $managedBlock.Add($BeginMarker)
        foreach ($line in $Lines) {
            $managedBlock.Add($line)
        }

        $managedBlock.Add($EndMarker)
        if ($insertIndex -lt $output.Count -and -not [string]::IsNullOrWhiteSpace($output[$insertIndex])) {
            $managedBlock.Add("")
        }

        $output.InsertRange($insertIndex, $managedBlock)
    }

    Set-Content -LiteralPath $PgHbaPath -Value $output -Encoding ASCII
}

function Find-PgHbaInsertionIndex {
    param([string[]]$Lines)

    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $trimmed = $Lines[$index].Trim()
        if ($trimmed -match '^(host|hostssl|hostnossl)\s+') {
            return $index
        }
    }

    return $Lines.Count
}

function Set-FluxVaultTrust {
    param(
        [string]$DataDirectory
    )

    $pgHba = Join-Path $DataDirectory "pg_hba.conf"
    Set-ManagedPgHbaBlock `
        -PgHbaPath $pgHba `
        -BeginMarker "# FluxVault PostgreSQL local trust BEGIN" `
        -EndMarker "# FluxVault PostgreSQL local trust END" `
        -Lines @(
            "host $DatabaseName $Username 127.0.0.1/32 trust",
            "host $DatabaseName $Username ::1/128 trust"
        )
}

function Set-TemporaryAdminTrust {
    param(
        [string]$DataDirectory,
        [bool]$Enabled
    )

    $pgHba = Join-Path $DataDirectory "pg_hba.conf"
    $lines = @()
    if ($Enabled) {
        $lines = @(
            "host postgres $PostgresAdminUsername 127.0.0.1/32 trust",
            "host postgres $PostgresAdminUsername ::1/128 trust"
        )
    }

    Set-ManagedPgHbaBlock `
        -PgHbaPath $pgHba `
        -BeginMarker "# FluxVault PostgreSQL temporary admin trust BEGIN" `
        -EndMarker "# FluxVault PostgreSQL temporary admin trust END" `
        -Lines $lines
}

function Invoke-PgCtlReload {
    param(
        [string]$PgCtlPath,
        [string]$DataDirectory
    )

    & $PgCtlPath reload -D $DataDirectory
    if ($LASTEXITCODE -ne 0) {
        Restart-Service -Name $ServiceName -Force
        Wait-ServiceRunning
    }
}

function Invoke-PostgresTool {
    param(
        [string]$ToolPath,
        [string[]]$Arguments,
        [string]$Password,
        [string]$FailureMessage
    )

    $oldPassword = $env:PGPASSWORD
    try {
        if ([string]::IsNullOrWhiteSpace($Password)) {
            Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:PGPASSWORD = $Password
        }

        & $ToolPath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FailureMessage Exit code: $LASTEXITCODE."
        }
    }
    finally {
        if ($null -eq $oldPassword) {
            Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:PGPASSWORD = $oldPassword
        }
    }
}

function Invoke-PsqlCommand {
    param(
        [string]$PsqlPath,
        [string]$Database,
        [string]$User,
        [string]$Password,
        [string]$Command,
        [switch]$NoPassword
    )

    $args = @(
        "--host=localhost",
        "--port=$Port",
        "--username=$User",
        "--dbname=$Database",
        "--command=$Command"
    )
    if ($NoPassword) {
        $args += "--no-password"
    }

    Invoke-PostgresTool -ToolPath $PsqlPath -Arguments $args -Password $Password -FailureMessage "psql command failed."
}

function Invoke-PsqlScalar {
    param(
        [string]$PsqlPath,
        [string]$Database,
        [string]$User,
        [string]$Password,
        [string]$Command,
        [switch]$NoPassword
    )

    $oldPassword = $env:PGPASSWORD
    try {
        if ([string]::IsNullOrWhiteSpace($Password)) {
            Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:PGPASSWORD = $Password
        }

        $args = @(
            "--host=localhost",
            "--port=$Port",
            "--username=$User",
            "--dbname=$Database",
            "--tuples-only",
            "--no-align",
            "--command=$Command"
        )
        if ($NoPassword) {
            $args += "--no-password"
        }

        $output = & $PsqlPath @args
        if ($LASTEXITCODE -ne 0) {
            throw "psql query failed. Exit code: $LASTEXITCODE."
        }

        return (($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n").Trim()
    }
    finally {
        if ($null -eq $oldPassword) {
            Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:PGPASSWORD = $oldPassword
        }
    }
}

function Test-FluxVaultConnection {
    param([string]$PsqlPath)

    try {
        Invoke-PsqlCommand `
            -PsqlPath $PsqlPath `
            -Database $DatabaseName `
            -User $Username `
            -Password $null `
            -Command "SELECT 1;" `
            -NoPassword
        return $true
    }
    catch {
        Write-Verbose "FluxVault no-password connection is not ready: $($_.Exception.Message)"
        return $false
    }
}

function Set-JsonProperty {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Value
    )

    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function New-MetadataStoreJson {
    return [pscustomobject]([ordered]@{
        provider = "PostgreSql"
        host = "localhost"
        port = $Port
        databaseName = $DatabaseName
        username = $Username
        serviceName = $ServiceName
        backupDirectory = (Join-Path $ProgramDataPath "db-backups")
        backupRetentionDays = 30
        maxCaptureWorkers = 4
        maxDbWriterConcurrency = 8
        exportLagWarningThreshold = "00:15:00"
    })
}

function New-DefaultConfigurationJson {
    return [pscustomobject]([ordered]@{
        repositoryPath = $RepositoryPath
        mirrorPath = $null
        isEnabled = $true
        watchedFolders = @()
        metadataStore = New-MetadataStoreJson
    })
}

function New-DefaultProfileSetJson {
    return [pscustomobject]([ordered]@{
        activeProfileId = "default"
        profiles = @(
            [pscustomobject]([ordered]@{
                id = "default"
                displayName = "Default"
                isEnabled = $true
                configuration = New-DefaultConfigurationJson
            })
        )
    })
}

function Update-ConfigurationJson {
    param([object]$Configuration)

    Set-JsonProperty -Object $Configuration -Name "repositoryPath" -Value $RepositoryPath
    Set-JsonProperty -Object $Configuration -Name "metadataStore" -Value (New-MetadataStoreJson)
}

function Update-FluxVaultConfig {
    $configPath = Join-Path $ProgramDataPath "config.json"
    New-Item -ItemType Directory -Force -Path $ProgramDataPath | Out-Null
    New-Item -ItemType Directory -Force -Path $RepositoryPath | Out-Null

    if (Test-Path -LiteralPath $configPath) {
        $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
        Copy-Item -LiteralPath $configPath -Destination "$configPath.$timestamp.bak" -Force
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    }
    else {
        $config = New-DefaultProfileSetJson
    }

    if ($config.PSObject.Properties.Name -contains "profiles") {
        $profiles = @($config.profiles) | Where-Object { $null -ne $_ }
        if ($profiles.Count -eq 0) {
            $config = New-DefaultProfileSetJson
            $profiles = @($config.profiles)
        }

        $activeProfileId = "default"
        if ($config.PSObject.Properties.Name -contains "activeProfileId" -and -not [string]::IsNullOrWhiteSpace($config.activeProfileId)) {
            $activeProfileId = $config.activeProfileId
        }

        $profile = $profiles | Where-Object { $_.id -eq $activeProfileId } | Select-Object -First 1
        if ($null -eq $profile) {
            $profile = $profiles | Select-Object -First 1
            Set-JsonProperty -Object $config -Name "activeProfileId" -Value $profile.id
        }

        if (-not ($profile.PSObject.Properties.Name -contains "configuration") -or $null -eq $profile.configuration) {
            Set-JsonProperty -Object $profile -Name "configuration" -Value (New-DefaultConfigurationJson)
        }

        Update-ConfigurationJson -Configuration $profile.configuration
    }
    else {
        Update-ConfigurationJson -Configuration $config
    }

    $config | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $configPath -Encoding UTF8
    Write-Host "FluxVault configuration updated: $configPath"
}

function Invoke-FluxVaultCliMetadataInit {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
    $projectPath = Join-Path $repoRoot "src\FluxVault.Cli\FluxVault.Cli.csproj"
    $publishedCli = Join-Path $repoRoot "artifacts\publish\cli\FluxVault.Cli.exe"

    if (-not [string]::IsNullOrWhiteSpace($CliPath)) {
        & $CliPath metadata-init --program-data $ProgramDataPath
    }
    elseif (Test-Path -LiteralPath $publishedCli) {
        & $publishedCli metadata-init --program-data $ProgramDataPath
    }
    elseif (Test-Path -LiteralPath $projectPath) {
        & dotnet run --project $projectPath -- metadata-init --program-data $ProgramDataPath
    }
    else {
        & FluxVault.Cli metadata-init --program-data $ProgramDataPath
    }

    if ($LASTEXITCODE -ne 0) {
        throw "FluxVault.Cli metadata-init failed with exit code $LASTEXITCODE."
    }
}

Assert-SqlIdentifier -Value $DatabaseName -Name "DatabaseName"
Assert-SqlIdentifier -Value $Username -Name "Username"
Assert-SqlIdentifier -Value $PostgresAdminUsername -Name "PostgresAdminUsername"

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535."
}

if ($ConfigOnly) {
    Update-FluxVaultConfig
    Write-Host "ConfigOnly was specified; PostgreSQL install and smoke checks were skipped."
    return
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required to install/configure the PostgreSQL service and pg_hba.conf."
}

$script:EffectiveAdminPassword = $PostgresAdminPassword
$existingService = Get-ServiceInfo
$existingServer = $null -ne $existingService

$binDirectory = Get-PostgreSqlBinDirectory
$psqlPath = Join-Path $binDirectory "psql.exe"
$pgCtlPath = Join-Path $binDirectory "pg_ctl.exe"
$createdbPath = Join-Path $binDirectory "createdb.exe"

if ((-not $existingServer) -or (-not (Test-Path -LiteralPath $psqlPath))) {
    Install-PostgreSql
    $binDirectory = Get-PostgreSqlBinDirectory
    $psqlPath = Join-Path $binDirectory "psql.exe"
    $pgCtlPath = Join-Path $binDirectory "pg_ctl.exe"
    $createdbPath = Join-Path $binDirectory "createdb.exe"
}

$psqlPath = Resolve-CommandPath "psql" $psqlPath
$pgCtlPath = Resolve-CommandPath "pg_ctl" $pgCtlPath
$createdbPath = Resolve-CommandPath "createdb" $createdbPath

Wait-ServiceRunning
$dataDirectory = Get-PostgreSqlDataDirectory

Set-FluxVaultTrust -DataDirectory $dataDirectory
Invoke-PgCtlReload -PgCtlPath $pgCtlPath -DataDirectory $dataDirectory

$existingFluxVaultConnectionReady = $existingServer `
    -and [string]::IsNullOrWhiteSpace($script:EffectiveAdminPassword) `
    -and (-not $AllowTemporaryAdminTrustForExistingServer) `
    -and (Test-FluxVaultConnection -PsqlPath $psqlPath)
if ($existingServer `
    -and [string]::IsNullOrWhiteSpace($script:EffectiveAdminPassword) `
    -and (-not $AllowTemporaryAdminTrustForExistingServer) `
    -and (-not $existingFluxVaultConnectionReady)) {
    throw "Existing PostgreSQL service '$ServiceName' found, but FluxVault cannot connect without a password. Pass -PostgresAdminPassword or explicitly pass -AllowTemporaryAdminTrustForExistingServer."
}

$temporaryAdminTrust = $existingServer -and [string]::IsNullOrWhiteSpace($script:EffectiveAdminPassword) -and $AllowTemporaryAdminTrustForExistingServer
if ($temporaryAdminTrust) {
    Set-TemporaryAdminTrust -DataDirectory $dataDirectory -Enabled $true
    Invoke-PgCtlReload -PgCtlPath $pgCtlPath -DataDirectory $dataDirectory
}

try {
    if (-not $existingFluxVaultConnectionReady) {
        $adminNoPassword = [string]::IsNullOrWhiteSpace($script:EffectiveAdminPassword)
        $roleExists = Invoke-PsqlScalar `
            -PsqlPath $psqlPath `
            -Database "postgres" `
            -User $PostgresAdminUsername `
            -Password $script:EffectiveAdminPassword `
            -Command "SELECT 1 FROM pg_roles WHERE rolname = '$Username';" `
            -NoPassword:$adminNoPassword
        if ($roleExists -ne "1") {
            Invoke-PsqlCommand `
                -PsqlPath $psqlPath `
                -Database "postgres" `
                -User $PostgresAdminUsername `
                -Password $script:EffectiveAdminPassword `
                -Command "CREATE ROLE `"$Username`" LOGIN;" `
                -NoPassword:$adminNoPassword
        }

        $databaseExists = Invoke-PsqlScalar `
            -PsqlPath $psqlPath `
            -Database "postgres" `
            -User $PostgresAdminUsername `
            -Password $script:EffectiveAdminPassword `
            -Command "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName';" `
            -NoPassword:$adminNoPassword
        if ($databaseExists -ne "1") {
            Invoke-PostgresTool `
                -ToolPath $createdbPath `
                -Arguments @("--host=localhost", "--port=$Port", "--username=$PostgresAdminUsername", "--owner=$Username", "--encoding=UTF8", $DatabaseName) `
                -Password $script:EffectiveAdminPassword `
                -FailureMessage "createdb failed."
        }

        Invoke-PsqlCommand `
            -PsqlPath $psqlPath `
            -Database "postgres" `
            -User $PostgresAdminUsername `
            -Password $script:EffectiveAdminPassword `
            -Command "ALTER DATABASE `"$DatabaseName`" OWNER TO `"$Username`"; GRANT CONNECT, TEMPORARY, CREATE ON DATABASE `"$DatabaseName`" TO `"$Username`";" `
            -NoPassword:$adminNoPassword
    }
}
finally {
    if ($temporaryAdminTrust) {
        Set-TemporaryAdminTrust -DataDirectory $dataDirectory -Enabled $false
        Invoke-PgCtlReload -PgCtlPath $pgCtlPath -DataDirectory $dataDirectory
    }
}

Update-FluxVaultConfig

Invoke-PsqlCommand `
    -PsqlPath $psqlPath `
    -Database $DatabaseName `
    -User $Username `
    -Password $null `
    -Command "SELECT 1;" `
    -NoPassword

Invoke-FluxVaultCliMetadataInit

$schemaVersion = Invoke-PsqlScalar `
    -PsqlPath $psqlPath `
    -Database $DatabaseName `
    -User $Username `
    -Password $null `
    -Command "SELECT max(version) FROM fluxvault.schema_version;" `
    -NoPassword
if ($schemaVersion -ne "1") {
    throw "Unexpected FluxVault metadata schema version: $schemaVersion"
}

Write-Host "FluxVault PostgreSQL metadata store is ready."
Write-Host "Run the app or start the service. Developer service install: eng\install-service.ps1"
