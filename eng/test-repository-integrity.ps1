[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PostgreSqlBinPath,
    [switch]$RunFullSuite,
    [switch]$InspectUi,
    [string]$Filter = 'Category=RequiresPostgreSql'
)
$ErrorActionPreference = 'Stop'
if ($InspectUi -and $RunFullSuite) { throw 'Choose either native UI inspection or the automated suite.' }
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskBin = (Resolve-Path -LiteralPath $PostgreSqlBinPath).Path
foreach ($taskTool in @('postgres.exe', 'initdb.exe', 'pg_ctl.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskBin $taskTool) -PathType Leaf)) { throw "PostgreSQL prerequisite missing: $taskTool in $taskBin" }
}
$taskId = [guid]::NewGuid().ToString('N')
$taskScratch = Join-Path ([IO.Path]::GetTempPath()) "FluxVault.Integrity.PostgreSql/$taskId"
$taskData = Join-Path $taskScratch 'data'
$taskResults = Join-Path $taskScratch 'results'
New-Item -ItemType Directory -Path $taskScratch, $taskResults | Out-Null
$taskPortLease = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$taskPortLease.Start()
$taskPort = $taskPortLease.LocalEndpoint.Port
$taskPortLease.Stop()
if ($taskPort -eq 5432) { throw 'Refusing the normal PostgreSQL port.' }
$taskProcess = $null
$taskLauncher = $null
$taskLaunchStarted = $null
$taskChildren = [Collections.Generic.List[Diagnostics.Process]]::new()
$taskExit = 1
$taskOldMarker = $env:FLUXVAULT_TEST_PG_MARKER
$taskOldHost = $env:FLUXVAULT_INTEGRITY_HOST
function Get-OwnedPostmaster {
    $taskPidPath = Join-Path $taskData 'postmaster.pid'
    if (-not (Test-Path -LiteralPath $taskPidPath)) { return $null }
    $taskPidLines = Get-Content -LiteralPath $taskPidPath
    if ($null -eq $taskLaunchStarted -or $taskPidLines.Count -lt 3 -or
        [IO.Path]::GetFullPath($taskPidLines[1]) -ne [IO.Path]::GetFullPath($taskData)) {
        throw 'Generated PostgreSQL data-directory identity changed.'
    }
    $taskCandidate = Get-Process -Id ([int]$taskPidLines[0]) -ErrorAction SilentlyContinue
    if ($null -eq $taskCandidate) { return $null }
    if ($taskCandidate.ProcessName -ne 'postgres' -or $taskCandidate.StartTime.ToUniversalTime() -lt $taskLaunchStarted.AddSeconds(-2) -or
        [Math]::Abs(([DateTimeOffset]$taskCandidate.StartTime.ToUniversalTime()).ToUnixTimeSeconds() - [long]$taskPidLines[2]) -gt 2 -or
        [IO.Path]::GetFullPath($taskCandidate.MainModule.FileName) -ne [IO.Path]::GetFullPath((Join-Path $taskBin 'postgres.exe'))) {
        $taskCandidate.Dispose()
        throw 'Generated PostgreSQL process identity did not match its launcher.'
    }
    return $taskCandidate
}
try {
    & (Join-Path $taskBin 'initdb.exe') -D $taskData -U fv_test --auth=trust --encoding=UTF8 --no-locale 2>&1 | Tee-Object -FilePath (Join-Path $taskScratch 'initdb.log')
    if ($LASTEXITCODE -ne 0) { throw 'Disposable initdb failed; see initdb.log.' }
    @"
listen_addresses = '127.0.0.1'
port = $taskPort
shared_buffers = '32MB'
fsync = on
synchronous_commit = on
full_page_writes = on
cluster_name = 'FluxVault.Integrity.$taskId'
fluxvault.test_instance = '$taskId'
"@ | Add-Content -LiteralPath (Join-Path $taskData 'postgresql.conf') -Encoding utf8
    # pg_ctl creates PostgreSQL's restricted Windows token, including from an elevated shell.
    # https://github.com/postgres/postgres/blob/REL_18_STABLE/src/bin/pg_ctl/pg_ctl.c
    # Start only this generated data directory; never SCM or the machine-wide setup.
    $taskLaunchStarted = [DateTime]::UtcNow
    $taskLauncher = Start-Process -FilePath (Join-Path $taskBin 'pg_ctl.exe') -ArgumentList @('-D', ('"' + $taskData + '"'), '-l', ('"' + (Join-Path $taskScratch 'postgres.log') + '"'), '-w', '-t', '30', 'start') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskScratch 'pg-ctl-start.stdout.log') -RedirectStandardError (Join-Path $taskScratch 'pg-ctl-start.stderr.log')
    # Wait for pg_ctl only; PowerShell -Wait also waits for its long-lived server descendants.
    if (-not $taskLauncher.WaitForExit(35000)) { throw 'PostgreSQL launcher exceeded its startup timeout.' }
    $taskProcess = Get-OwnedPostmaster
    if ($null -ne $taskProcess) { $taskStarted = $taskProcess.StartTime.ToUniversalTime() }
    $taskPidLines = if ($null -ne $taskProcess) { Get-Content -LiteralPath (Join-Path $taskData 'postmaster.pid') } else { @() }
    if ($taskLauncher.ExitCode -ne 0 -or $null -eq $taskProcess -or $taskPidLines.Count -lt 8 -or $taskPidLines[7].Trim() -ne 'ready') {
        throw 'Disposable PostgreSQL failed readiness; see postgres.log and pg-ctl-start.stderr.log.'
    }
    $taskMarker = Join-Path $taskScratch 'owner.json'
    @{ InstanceId = $taskId; DataDirectory = $taskData; Port = $taskPort; ProcessId = $taskProcess.Id; ProcessStartedUtc = $taskStarted.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $taskMarker -Encoding utf8
    $env:FLUXVAULT_TEST_PG_MARKER = $taskMarker
    $env:FLUXVAULT_INTEGRITY_HOST = Join-Path $taskRepo 'tests/FluxVault.TestHost/bin/Release/net10.0-windows/FluxVault.TestHost.dll'
    & dotnet --version | Set-Content -LiteralPath (Join-Path $taskScratch 'dotnet-version.txt')
    & (Join-Path $taskBin 'postgres.exe') --version | Set-Content -LiteralPath (Join-Path $taskScratch 'postgres-version.txt')
    & git -C $taskRepo rev-parse HEAD | Set-Content -LiteralPath (Join-Path $taskScratch 'source-head.txt')
    & git -C $taskRepo diff --stat | Set-Content -LiteralPath (Join-Path $taskScratch 'source-diff-stat.txt')
    if ($InspectUi) {
        $taskUiId = [guid]::NewGuid().ToString('N')
        $taskUiScratch = Join-Path ([IO.Path]::GetTempPath()) "FluxVault.Integrity/$taskUiId"
        $taskUiDatabase = "fv_test_$taskUiId"
        $taskUiPipe = "FluxVault.Integrity.$taskUiId"
        New-Item -ItemType Directory -Path (Join-Path $taskUiScratch 'working'), (Join-Path $taskUiScratch 'restored') | Out-Null
        [IO.File]::WriteAllText((Join-Path $taskUiScratch 'working/source.bin'), ('verified working file ' * 10000))
        & (Join-Path $taskBin 'psql.exe') -h 127.0.0.1 -p $taskPort -U fv_test -d postgres -w -v ON_ERROR_STOP=1 -c "CREATE DATABASE $taskUiDatabase;"
        if ($LASTEXITCODE -ne 0) { throw 'Native fixture database creation failed.' }
        foreach ($taskMode in @('serve', 'ui')) {
            $taskArguments = @(('"' + $env:FLUXVAULT_INTEGRITY_HOST + '"'), '--mode', $taskMode, '--scratch', ('"' + $taskUiScratch + '"'), '--db-port', $taskPort, '--database', $taskUiDatabase, '--pipe', $taskUiPipe)
            $taskChild = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $taskArguments -WindowStyle $(if ($taskMode -eq 'ui') { 'Normal' } else { 'Hidden' }) -PassThru -RedirectStandardOutput (Join-Path $taskScratch "$taskMode.stdout.log") -RedirectStandardError (Join-Path $taskScratch "$taskMode.stderr.log")
            $taskChildren.Add($taskChild)
            if ($taskMode -eq 'serve') {
                $taskReadyDeadline = [DateTime]::UtcNow.AddSeconds(30)
                while (-not ((Get-Content -LiteralPath (Join-Path $taskScratch 'serve.stdout.log') -Raw) -match 'READY')) {
                    if ($taskChild.HasExited -or [DateTime]::UtcNow -gt $taskReadyDeadline) { throw 'Native fixture server failed readiness.' }
                    Start-Sleep -Milliseconds 100
                }
            }
        }
        @{ Scratch = $taskUiScratch; Database = $taskUiDatabase; Pipe = $taskUiPipe; Port = $taskPort; Processes = @($taskChildren | ForEach-Object { $_.Id }); StopSignal = (Join-Path $taskScratch 'stop-ui.signal') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskScratch 'ui-fixture.json') -Encoding utf8
        Write-Output "Native UI fixture: $taskScratch"
        $taskUiDeadline = [DateTime]::UtcNow.AddMinutes(20)
        while (-not (Test-Path -LiteralPath (Join-Path $taskScratch 'stop-ui.signal')) -and [DateTime]::UtcNow -lt $taskUiDeadline -and -not $taskChildren[1].HasExited) { Start-Sleep -Seconds 1 }
        if ($taskChildren[1].HasExited -and $taskChildren[1].ExitCode -ne 0) { throw 'Native UI fixture failed; see ui.stderr.log.' }
        $taskExit = 0
    } elseif ($RunFullSuite) {
        & dotnet test (Join-Path $taskRepo 'FluxVault.slnx') -c Release --logger trx --results-directory $taskResults 2>&1 | Tee-Object -FilePath (Join-Path $taskScratch 'tests.log')
        $taskExit = $LASTEXITCODE
    } else {
        & dotnet test (Join-Path $taskRepo 'tests/FluxVault.Integration.Tests/FluxVault.Integration.Tests.csproj') -c Release --filter $Filter --logger trx --results-directory $taskResults 2>&1 | Tee-Object -FilePath (Join-Path $taskScratch 'tests.log')
        $taskExit = $LASTEXITCODE
    }
} finally {
    for ($taskChildIndex = $taskChildren.Count - 1; $taskChildIndex -ge 0; $taskChildIndex--) {
        $taskChild = $taskChildren[$taskChildIndex]
        if (-not $taskChild.HasExited) { $taskChild.Kill($true) }
        if (-not $taskChild.WaitForExit(5000)) { throw "Owned fixture process did not exit: $($taskChild.Id)" }
        $taskChild.Dispose()
    }
    $env:FLUXVAULT_TEST_PG_MARKER = $taskOldMarker
    $env:FLUXVAULT_INTEGRITY_HOST = $taskOldHost
    # Even a startup timeout can leave a server behind. Stop the held launcher first,
    # then discover only the postmaster identified by our generated data directory.
    if ($null -ne $taskLauncher) {
        if (-not $taskLauncher.HasExited) {
            $taskLauncher.Kill()
            if (-not $taskLauncher.WaitForExit(5000)) { throw "Owned launcher did not exit. Retained $taskScratch" }
        }
        $taskLauncher.Dispose()
    }
    if ($null -eq $taskProcess -and $null -ne $taskLaunchStarted) {
        $taskProcess = Get-OwnedPostmaster
        if ($null -ne $taskProcess) { $taskStarted = $taskProcess.StartTime.ToUniversalTime() }
    }
    if ($null -ne $taskProcess) {
        $taskProcess.Refresh()
        if (-not $taskProcess.HasExited) {
            $taskResolvedData = [IO.Path]::GetFullPath($taskData)
            $taskExpectedData = [IO.Path]::GetFullPath((Join-Path $taskScratch 'data'))
            $taskPidFile = Join-Path $taskData 'postmaster.pid'
            if ($taskResolvedData -ne $taskExpectedData -or $taskProcess.StartTime.ToUniversalTime() -ne $taskStarted -or
                -not (Test-Path -LiteralPath $taskPidFile) -or [int](Get-Content -LiteralPath $taskPidFile -TotalCount 1) -ne $taskProcess.Id) {
                throw "Cleanup refused: generated PostgreSQL process identity changed. Retained $taskScratch"
            }
            & (Join-Path $taskBin 'pg_ctl.exe') -D $taskData -w stop -m fast 2>&1 | Tee-Object -FilePath (Join-Path $taskScratch 'shutdown.log')
            if ($LASTEXITCODE -ne 0) { throw "Owned test-server shutdown failed. Retained $taskScratch" }
            if (-not $taskProcess.WaitForExit(5000)) { throw "Owned test-server process did not exit. Retained $taskScratch" }
        }
        $taskProcess.Dispose()
    }
    Write-Output "Integrity test evidence: $taskScratch"
}
exit $taskExit
