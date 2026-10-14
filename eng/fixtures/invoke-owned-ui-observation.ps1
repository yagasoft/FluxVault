# Opens inactive WPF windows through the existing read-only observation probe.
# No mouse/keyboard input or activation is requested. Every child is joined.
#Requires -Version 7.2
param([ValidatePattern('^[0-9a-f]{32}$')][string]$RunId=([guid]::NewGuid().ToString('N')),
    [ValidateRange(3,90)][int]$ObservationSeconds=5)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testHost=Join-Path $repository 'tests/FluxVault.TestHost/bin/Release/net10.0-windows/FluxVault.TestHost.exe'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('FluxVault.Integrity/'+$RunId)
if(Test-Path -LiteralPath $scratch){throw 'Use a fresh observation identity.'}
for($path=$scratch;$path;$path=[IO.Path]::GetDirectoryName($path)){
    if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse observation path refused.'}
}
if(Get-Process -Name FluxVault.App -ErrorAction SilentlyContinue){throw 'An existing desktop is open; do not interfere with it.'}
$null=New-Item -ItemType Directory -Path $scratch
$start=[Diagnostics.ProcessStartInfo]::new($testHost)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($argument in @('--mode','ui-normal-startup-observe','--scratch',$scratch,'--observation-seconds',[string]$ObservationSeconds,'--mvp-ui','true')){$start.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($start)
$pin=@{ProcessId=$process.Id;StartedUtc=$process.StartTime.ToUniversalTime().ToString('O');Executable=$testHost}
$pin|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $scratch 'launcher-process.json') -Encoding utf8NoBOM
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();$timedOut=$false
try{if(-not $process.WaitForExit(($ObservationSeconds+45)*1000)){$timedOut=$true;$process.Kill($true)}}
finally{
    if(-not $process.HasExited){$process.Kill($true)}
    if(-not $process.WaitForExit(10000)){throw 'Owned WPF observer did not exit.'}
    $receipt=@{Process=$pin;Joined=$true;TimedOut=$timedOut;ExitCode=$process.ExitCode;CompletedUtc=[DateTimeOffset]::UtcNow}
    [IO.File]::WriteAllText((Join-Path $scratch 'stdout.log'),$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $scratch 'stderr.log'),$stderr.GetAwaiter().GetResult())
    $process.Dispose()
    $receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $scratch 'launcher-joined.json') -Encoding utf8NoBOM
}
if($timedOut -or $receipt.ExitCode -ne 0 -or (Test-Path -LiteralPath (Join-Path $scratch 'ui-startup-error.json')) -or
    -not(Test-Path -LiteralPath (Join-Path $scratch 'ui-startup-joined.json'))){throw ('UI observation did not pass; inspect '+$scratch)}
@{Root=$scratch;Joined=$true;NoInputRequested=$true}|ConvertTo-Json
