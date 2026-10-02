[CmdletBinding()]
param(
    [string]$PostgreSqlBinPath = 'D:\Program Files\PostgreSQL\18\bin',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$CandidateVersion = '1.0.4',
    [string]$PayloadReportPath
)
$ErrorActionPreference = 'Stop'
$oldPassword = $env:PGPASSWORD
$oldPassfile = $env:PGPASSFILE
$oldTimeout = $env:PGCONNECT_TIMEOUT
$probes = [Collections.Generic.List[object]]::new()
function Invoke-PasswordlessProbe([string]$HostName, [string]$Role, [string]$Database, [bool]$ShouldSucceed) {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $PostgreSqlBinPath 'psql.exe'))
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in @('-X','-w','-h',$HostName,'-p','5432','-U',$Role,'-d',$Database,'-t','-A','-c','SELECT current_database(), current_user')) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $pidValue = $process.Id
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(5000)) { throw 'Passwordless authentication probe timed out.' }
        $value = $output.GetAwaiter().GetResult().Trim()
        [void]$errorOutput.GetAwaiter().GetResult()
        if ($ShouldSucceed) {
            if ($process.ExitCode -ne 0 -or $value -ne "$Database|$Role") { throw 'Scoped runtime access failed.' }
        } elseif ($process.ExitCode -eq 0) { throw 'Out-of-scope passwordless access unexpectedly succeeded.' }
        $probes.Add([pscustomobject]@{Host=$HostName; Role=$Role; Database=$Database; ExpectedSuccess=$ShouldSucceed; ExitCode=$process.ExitCode; Pid=$pidValue; Exited=$process.HasExited})
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); if (-not $process.WaitForExit(5000)) { throw 'Owned authentication probe did not exit.' } }
        $process.Dispose()
    }
}
try {
    $env:PGPASSWORD = $null
    $env:PGPASSFILE = Join-Path ([IO.Path]::GetTempPath()) ('fluxvault-absent-passfile-' + [guid]::NewGuid().ToString('N'))
    $env:PGCONNECT_TIMEOUT = '2'
    foreach ($hostName in @('127.0.0.1','::1')) {
        Invoke-PasswordlessProbe $hostName 'fluxvault' 'fluxvault_metadata' $true
        Invoke-PasswordlessProbe $hostName 'postgres' 'postgres' $false
        Invoke-PasswordlessProbe $hostName 'fluxvault' 'postgres' $false
    }
} finally {
    $env:PGPASSWORD = $oldPassword
    $env:PGPASSFILE = $oldPassfile
    $env:PGCONNECT_TIMEOUT = $oldTimeout
}
$preflight = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installed-preflight.json') -Raw | ConvertFrom-Json
$pg = Get-Process -Id $preflight.PostgreSqlPostmasterPid
if ([Math]::Abs(($pg.StartTime.ToUniversalTime() - ([DateTimeOffset]$preflight.PostgreSqlPostmasterStartedUtc).UtcDateTime).TotalMilliseconds) -gt 1) { throw 'Normal PostgreSQL postmaster changed.' }
$setup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installed-postgresql-setup.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $setup.original_hba_backup).Hash.ToLowerInvariant() -ne $preflight.OriginalHbaSha256) { throw 'Original HBA backup changed.' }
$originalLines = @(Get-Content -LiteralPath $setup.original_hba_backup | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$managed = $false
$unrelated = [Collections.Generic.List[string]]::new()
$hba = Get-Content -LiteralPath $preflight.HbaPath
foreach ($line in $hba) {
    if ($line -eq '# FluxVault PostgreSQL local trust BEGIN') { if ($managed) { throw 'Nested runtime authentication block.' }; $managed = $true; continue }
    if ($line -eq '# FluxVault PostgreSQL local trust END') { $managed = $false; continue }
    if (-not $managed -and -not [string]::IsNullOrWhiteSpace($line)) { $unrelated.Add($line) }
}
if ($managed -or ($hba -join "`n") -match 'temporary admin') { throw 'Temporary administrator or malformed managed authentication block remains.' }
if (($originalLines -join "`n") -cne ($unrelated.ToArray() -join "`n")) { throw 'Unrelated authentication lines changed.' }
$service = Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
if ($service.State -ne 'Running' -or $service.StartName -ne 'LocalSystem' -or $service.StartMode -ne 'Auto' -or
    $service.PathName -notlike '*C:\Program Files\FluxVault\service\FluxVault.Service.exe*') { throw 'Installed service state/identity changed.' }
if ((Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\FluxVaultService' -Name DelayedAutoStart).DelayedAutoStart -ne 1) { throw 'Delayed-auto service policy differs.' }
$ipcAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'installed-live-validation.ps1'), [ref]$null, [ref]$null)
foreach ($function in $ipcAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('Invoke-StagingIpc','Assert-IpcSuccess')}, $true)) { . ([scriptblock]::Create($function.Extent.Text)) }
$status = Invoke-StagingIpc @{command=0}
Assert-IpcSuccess $status
if (-not $status.status.isServiceRunning -or $status.status.metadataStore.lastError -or @($status.status.configuration.watchedFolders).Count -ne 0) { throw 'Installed service is not healthy and idle.' }
if ([string]::IsNullOrWhiteSpace($PayloadReportPath)) {
    $PayloadReportPath = Join-Path $PSScriptRoot ('installed-payload-v' + $CandidateVersion.Replace('.', '') + '.json')
}
$payload = Get-Content -LiteralPath $PayloadReportPath -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $payload.setup).Hash.ToLowerInvariant() -ne $payload.setup_sha256 -or $payload.product_version -ne "$CandidateVersion.0") { throw 'Installed candidate provenance changed.' }
foreach ($file in $payload.payload_file_sha256.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $payload.install_root $file.Name)).Hash.ToLowerInvariant() -ne $file.Value) { throw "Installed payload changed: $($file.Name)" }
}
[pscustomobject]@{
    RecordedUtc=[DateTimeOffset]::UtcNow.ToString('o'); Version=$CandidateVersion; CandidateSha256=$payload.setup_sha256
    VerifiedInstalledPayloadFiles=$payload.matching_payload_files; ServiceState=$service.State; ServicePid=$service.ProcessId
    LocalSystem=$true; DelayedAuto=$true; Metadata=$status.status.metadataStore; WatchedFolderCount=0
    NormalPostgreSqlPid=$pg.Id; NormalPostgreSqlStartedUtc=$pg.StartTime.ToUniversalTime().ToString('o'); NormalPostgreSqlUnchanged=$true
    OriginalHbaBackupSha256=$preflight.OriginalHbaSha256; UnrelatedAuthenticationLinesPreserved=$true
    PasswordlessProbes=$probes.ToArray(); TemporaryAdministratorAccessRemoved=$true; AllProbeProcessesExited=$true
} | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'installed-state-final.json') -Encoding utf8
Write-Output 'Installed payload/service/metadata and scoped passwordless authentication verified; PostgreSQL unchanged; all probe children exited.'
