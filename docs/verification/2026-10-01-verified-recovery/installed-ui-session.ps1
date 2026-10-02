$ErrorActionPreference = 'Stop'
$exe = 'C:\Program Files\FluxVault\app\FluxVault.App.exe'
if (Get-Process -Name FluxVault.App -ErrorAction SilentlyContinue) { throw 'An existing desktop process is present; do not reuse or close it.' }
$state = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installed-live-state.json') -Raw | ConvertFrom-Json
$scratch = [IO.Path]::GetFullPath($state.Scratch)
if (-not $scratch.StartsWith('C:\ProgramData\FluxVault\staging-validation\', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $scratch) -notmatch '^[a-f0-9]{32}$') { throw 'Unexpected UI session scratch path.' }
$signal = Join-Path $scratch 'stop-installed-ui.signal'
if (Test-Path -LiteralPath $signal) { throw 'UI stop signal already exists.' }
$process = $null
try {
    # Visible because the user explicitly authorised interactive desktop validation.
    $process = Start-Process -FilePath $exe -WindowStyle Normal -PassThru
    $pidValue = $process.Id
    $started = $process.StartTime.ToUniversalTime().ToString('o')
    $deadline = [DateTime]::UtcNow.AddMinutes(20)
    Write-Output "Owned installed UI PID: $pidValue; stop signal: $signal"
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline -and -not (Test-Path -LiteralPath $signal)) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($process) {
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { $process.Kill($true); if (-not $process.WaitForExit(5000)) { throw 'Owned installed UI did not exit.' } }
        }
        $exited = $process.HasExited
        $exitCode = $process.ExitCode
        $process.Dispose()
        [pscustomobject]@{RecordedUtc=[DateTimeOffset]::UtcNow.ToString('o'); ProcessId=$pidValue; StartedUtc=$started; Exe=$exe; Exited=$exited; ExitCode=$exitCode; StopSignal=$signal} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'installed-ui-cleanup.json') -Encoding utf8
        Write-Output "Owned installed UI exit verified: $exited"
    }
}
