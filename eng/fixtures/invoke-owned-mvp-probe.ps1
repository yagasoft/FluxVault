#Requires -Version 7.2
param([Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][ValidateSet('roundtrip','sustain','verify-retained','restore-baseline','snapshot')][string]$Phase,
    [int]$TimeoutSeconds=0)
$ErrorActionPreference='Stop'
# Let the probe's cancellation and two-minute baseline restoration finish before
# a last-resort process kill. Join alone is not proof of configuration restoration.
$minimumSeconds=if($Phase -eq 'sustain'){4680}else{1380}
if($TimeoutSeconds -eq 0){$TimeoutSeconds=$minimumSeconds}
if($TimeoutSeconds -lt $minimumSeconds -or $TimeoutSeconds -gt 5400){throw 'Probe deadline must include its cancellation and restoration grace.'}
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$parent=Join-Path ([Environment]::GetFolderPath('UserProfile')) 'FluxVault Staging/MVP'
if([IO.Path]::GetDirectoryName($RunRoot) -ine $parent -or [IO.Path]::GetFileName($RunRoot) -notmatch '^[0-9a-f]{32}$'){throw 'Unowned MVP root.'}
$executable=Join-Path $RunRoot 'probe/FluxVault.TestHost.exe'
$start=[Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($argument in @('--mode','installed-mvp-acceptance','--root',$RunRoot,'--phase',$Phase)){$start.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::new();$process.StartInfo=$start
$operation=[guid]::NewGuid().ToString('N')
$started=$false;$timedOut=$false
try{
    $started=$process.Start();if(-not $started){throw 'Probe did not start.'}
    $pin=@{ProcessId=$process.Id;StartedUtc=$process.StartTime.ToUniversalTime().ToString('o');Executable=$executable;Phase=$Phase}
    $pin|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $RunRoot ('process-'+$operation+'.json')) -Encoding utf8NoBOM
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit($TimeoutSeconds*1000)){$timedOut=$true;$process.Kill($true);$process.WaitForExit()}
    $out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $RunRoot ($Phase+'-'+$operation+'.stdout.log')),$out)
    [IO.File]::WriteAllText((Join-Path $RunRoot ($Phase+'-'+$operation+'.stderr.log')),$err)
    $receipt=@{Phase=$Phase;Process=$pin;Exited=$process.HasExited;ExitCode=$process.ExitCode;TimedOut=$timedOut;JoinedUtc=[DateTimeOffset]::UtcNow;
        ConfigurationRestoration=$(if($timedOut){'Unverified: run restore-baseline and inspect its receipt before any further installed test.'}else{'Inspect the probe baseline-restoration receipt; process exit alone is not proof.'})}
    $receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $RunRoot ('joined-'+$operation+'.json')) -Encoding utf8NoBOM
    $receipt|ConvertTo-Json -Depth 5
    if($timedOut -or $process.ExitCode -ne 0){throw ('Probe failed; inspect retained private logs. '+$err)}
}finally{
    if($started -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
    $process.Dispose()
}
