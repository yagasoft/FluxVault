[CmdletBinding()]
param([Parameter(Mandatory)][string]$MarkerPath, [Parameter(Mandatory)][string]$PostgreSqlBinPath)
$ErrorActionPreference='Stop'
$taskMarkerPath=(Resolve-Path -LiteralPath $MarkerPath).Path
$taskOwner=Get-Content -LiteralPath $taskMarkerPath -Raw | ConvertFrom-Json
$taskParent=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'FluxVault.Integrity.PostgreSql'))
$taskRoot=Split-Path -Parent $taskMarkerPath
if ((Split-Path -Parent $taskRoot) -ne $taskParent -or (Split-Path -Leaf $taskRoot) -ne $taskOwner.InstanceId -or $taskOwner.Port -eq 5432) { throw 'Test cluster ownership path invalid.' }
$taskProcess=Get-Process -Id $taskOwner.ProcessId
if ($taskProcess.ProcessName -ne 'postgres' -or [Math]::Abs(($taskProcess.StartTime.ToUniversalTime() - ([DateTimeOffset]$taskOwner.ProcessStartedUtc).UtcDateTime).TotalMilliseconds) -gt 1) { throw 'Test cluster process identity changed.' }
$global:fvSetupTestService='fixture-only'
$global:fvSetupTestBin=(Resolve-Path -LiteralPath $PostgreSqlBinPath).Path
$global:fvSetupTestData=$taskOwner.DataDirectory
function Get-CimInstance { param($ClassName,$Filter,$ErrorAction); if ($ClassName -ne 'Win32_Service' -or $Filter -ne "Name='$global:fvSetupTestService'") { throw 'Unexpected service lookup.' }; return [pscustomobject]@{PathName='"'+(Join-Path $global:fvSetupTestBin 'pg_ctl.exe')+'" runservice -D "'+$global:fvSetupTestData+'"'} }
function Get-Service { param($Name,$ErrorAction); if ($Name -ne $global:fvSetupTestService) { throw 'Unexpected service lookup.' }; return [pscustomobject]@{Status=[System.ServiceProcess.ServiceControllerStatus]::Running} }
function Start-Service { throw 'The rehearsal must not start services.' }
function Restart-Service { throw 'The rehearsal must not restart services.' }
$taskName='fv_setup_'+[guid]::NewGuid().ToString('N')
$taskDataRoot=Join-Path $taskRoot 'setup-program-data'
$taskArguments=@{PostgreSqlBinPath=$global:fvSetupTestBin; ServiceName=$global:fvSetupTestService; Port=$taskOwner.Port; DatabaseName=$taskName; Username=$taskName; ProgramDataPath=$taskDataRoot; RepositoryPath=(Join-Path $taskDataRoot 'repository'); PostgresAdminUsername='fv_test'; RequireFreshDatabase=$true; AllowTemporaryAdminTrustForExistingServer=$true; CliPath=(Join-Path (Get-Location) 'src\FluxVault.Cli\bin\Release\net10.0\FluxVault.Cli.exe')}
$taskHba=Join-Path $taskOwner.DataDirectory 'pg_hba.conf'
$taskOriginalHba=[IO.File]::ReadAllBytes($taskHba)
try {
    # Ordinary trust masked missing scoped rules in the first rehearsal. Require
    # SCRAM on both fallback endpoints in this owned cluster instead.
    $taskFallbackCount=0
    $taskFallbackLines=@(foreach ($line in Get-Content -LiteralPath $taskHba) {
        if ($line -match '^(\s*host\s+all\s+all\s+(?:127\.0\.0\.1/32|::1/128)\s+)trust(\s*(?:#.*)?)$') {
            $taskFallbackCount++
            $line -replace '^(\s*host\s+all\s+all\s+(?:127\.0\.0\.1/32|::1/128)\s+)trust(\s*(?:#.*)?)$', '${1}scram-sha-256${2}'
        } else { $line }
    })
    if ($taskFallbackCount -ne 2) { throw 'Expected two owned loopback fallback rules.' }
    [IO.File]::WriteAllLines($taskHba,$taskFallbackLines,[Text.Encoding]::ASCII)
    & (Join-Path $global:fvSetupTestBin 'pg_ctl.exe') reload -D $taskOwner.DataDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Owned fallback reload failed.' }
    & './eng/setup-fluxvault-postgresql.ps1' @taskArguments
    if ([IO.File]::ReadAllText($taskHba).Contains('# FluxVault PostgreSQL temporary admin trust BEGIN')) { throw 'Temporary administrator trust survived successful provisioning.' }
    # The owned runner deliberately listens on IPv4 loopback only.
    foreach ($endpoint in @('127.0.0.1')) {
        foreach ($probeUser in @($taskName,'fv_test')) {
            $probeInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $global:fvSetupTestBin 'psql.exe'))
            $probeInfo.UseShellExecute=$false
            $probeInfo.CreateNoWindow=$true
            $probeInfo.RedirectStandardOutput=$true
            $probeInfo.RedirectStandardError=$true
            [void]$probeInfo.Environment.Remove('PGPASSWORD')
            $probeInfo.Environment['PGPASSFILE']=Join-Path $taskRoot 'no-probe-password-file'
            $probeDatabase=if ($probeUser -eq $taskName) { $taskName } else { 'postgres' }
            foreach ($argument in @('-X','-w',"--host=$endpoint","--port=$($taskOwner.Port)","--username=$probeUser","--dbname=$probeDatabase",'--tuples-only','--no-align','--command=SELECT 1;')) { $probeInfo.ArgumentList.Add($argument) }
            $probe=[Diagnostics.Process]::Start($probeInfo)
            try {
                if (-not $probe.WaitForExit(5000)) { throw 'Authentication probe timed out.' }
                $probeOut=$probe.StandardOutput.ReadToEnd()
                $probeError=$probe.StandardError.ReadToEnd()
                if ($probeUser -eq $taskName) {
                    if ($probe.ExitCode -ne 0 -or $probeOut.Trim() -ne '1') { throw "Scoped endpoint failed after cleanup: $endpoint" }
                } elseif ($probe.ExitCode -eq 0 -or $probeError -notmatch 'no password supplied') { throw "Administrator endpoint was not denied: $endpoint" }
            } finally {
                if (-not $probe.HasExited) { $probe.Kill($true); if (-not $probe.WaitForExit(5000)) { throw 'Authentication probe did not exit.' } }
                $probe.Dispose()
            }
        }
    }
    $taskBefore=(Get-FileHash -LiteralPath $taskHba -Algorithm SHA256).Hash
    $taskCollisionRefused=$false
    try { & './eng/setup-fluxvault-postgresql.ps1' @taskArguments } catch { if ($_.Exception.Message -notmatch 'Fresh provisioning refused') { throw }; $taskCollisionRefused=$true }
    if (-not $taskCollisionRefused -or (Get-FileHash -LiteralPath $taskHba -Algorithm SHA256).Hash -ne $taskBefore) { throw 'Collision was not refused without authentication changes.' }
    [IO.File]::AppendAllText($taskHba,"`n# Unicode preservation test: $([char]0x4F60)$([char]0x597D)`n",[Text.UTF8Encoding]::new($false))
    $taskUnicodeHash=(Get-FileHash -LiteralPath $taskHba -Algorithm SHA256).Hash
    $taskUnicodeRefused=$false
    try { & './eng/setup-fluxvault-postgresql.ps1' @taskArguments } catch { if ($_.Exception.Message -notmatch 'non-ASCII') { throw }; $taskUnicodeRefused=$true }
    if (-not $taskUnicodeRefused -or (Get-FileHash -LiteralPath $taskHba -Algorithm SHA256).Hash -ne $taskUnicodeHash) { throw 'Non-ASCII authentication file was not preserved.' }
    @{Provisioning=$true; SchemaInitialized=$true; AdministratorTrustRemoved=$true; ScopedIpv4AccessAfterCleanup=$true; PasswordlessAdministratorIpv4Denied=$true; ConfiguredEndpoints=@('127.0.0.1'); OrdinaryFallbackAuthentication='scram-sha-256'; CollisionRefused=$true; CollisionAuthenticationBytesPreserved=$true; NonAsciiInputRefusedAndPreserved=$true; ServerProcessUnchanged=(-not $taskProcess.HasExited); Cluster=$taskOwner.InstanceId; Database=$taskName} | ConvertTo-Json | Set-Content -LiteralPath 'docs\verification\2026-10-01-verified-recovery\setup-rehearsal.json' -Encoding utf8
} finally {
    [IO.File]::WriteAllBytes($taskHba,$taskOriginalHba)
    & (Join-Path $global:fvSetupTestBin 'pg_ctl.exe') reload -D $taskOwner.DataDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Owned original authentication reload failed.' }
    $taskProcess.Dispose()
}
