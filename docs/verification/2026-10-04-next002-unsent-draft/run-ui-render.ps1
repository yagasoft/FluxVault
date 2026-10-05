$ErrorActionPreference='Stop'
$workspace=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$parent=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'FluxVault.Integrity'))
$id=[guid]::NewGuid().ToString('N')
$scratch=[IO.Path]::GetFullPath((Join-Path $parent $id))
if([IO.Path]::GetDirectoryName($scratch) -ne $parent -or [IO.Path]::GetFileName($scratch) -ne $id){throw 'Invalid owned UI scratch path.'}
$start=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
$start.UseShellExecute=$false; $start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
foreach($argument in @('exec',(Join-Path $workspace 'tests/FluxVault.TestHost/bin/Release/net10.0-windows/FluxVault.TestHost.dll'),'--mode','ui-draft-render','--scratch',$scratch)){$start.ArgumentList.Add($argument)}
$process=$null
try
{
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(45000)){throw 'Owned UI renderer exceeded its finite deadline.'}
    if($process.ExitCode -ne 0){throw $stderr.GetAwaiter().GetResult()}
    $destination=Join-Path $PSScriptRoot 'ui';New-Item -ItemType Directory -Path $destination -Force|Out-Null
    foreach($name in @('protect-draft.png','options-draft.png','ui-result.json','ui-process.json')){Copy-Item -LiteralPath (Join-Path $scratch $name) -Destination (Join-Path $destination $name)}
    Write-Output 'Owned WPF renderer joined with exit zero.'
}
finally
{
    if($process){try{if(-not $process.HasExited){$process.Kill($true);if(-not $process.WaitForExit(10000)){throw 'Owned UI renderer did not exit.'}}}finally{$process.Dispose()}}
    if(Test-Path -LiteralPath $scratch){Remove-Item -LiteralPath $scratch -Recurse -Force}
}
