# Runs only the local rejecting-client WPF fixture; no service/database changes.
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$FixtureId,
    [ValidateRange(0,50000)][int]$InventoryCount=10000)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$worktree=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$parent=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'FluxVault.Integrity'))
$root=[IO.Path]::GetFullPath((Join-Path $parent $FixtureId))
if([guid]::ParseExact($FixtureId,'N') -eq [guid]::Empty -or [IO.Path]::GetDirectoryName($root) -ne $parent -or (Test-Path -LiteralPath $root)){throw 'Fresh owned GUID root required.'}
function Assert-NoReparse([string]$path) {
    for($current=$path;$current;$current=[IO.Path]::GetDirectoryName($current)) {
        if(Test-Path -LiteralPath $current){if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse component refused.'}}
    }
}
function Read-Normal {
    @{Files=@(foreach($path in @('C:\ProgramData\FluxVault\installation.json','D:\Program Files\PostgreSQL\18\data\pg_hba.conf','D:\Program Files\PostgreSQL\18\data\pg_ident.conf')) {
        @{Path=$path;Sha256=(Get-FileHash -LiteralPath $path).Hash}});
      Services=@(foreach($name in @('FluxVaultService','postgresql-x64-18')) {
        $service=Get-CimInstance Win32_Service -Filter "Name='$name'"
        $native=Get-Process -Id $service.ProcessId
        try{@{Name=$name;ProcessId=$service.ProcessId;StartedUtc=$native.StartTime.ToUniversalTime().ToString('o');Path=$service.PathName;Account=$service.StartName}}
        finally{$native.Dispose()}
    })}
}
Assert-NoReparse $root
$evidence=Join-Path $PSScriptRoot ('desktop-attempt-'+$FixtureId)
Assert-NoReparse $evidence
if(Test-Path -LiteralPath $evidence){throw 'Fresh desktop evidence directory required; earlier observations are preserved.'}
[void][IO.Directory]::CreateDirectory($evidence)
$before=Read-Normal
$before|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $evidence 'before.json')
[void][IO.Directory]::CreateDirectory($root)
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $worktree 'tests/FluxVault.TestHost/bin/Release/net10.0-windows/FluxVault.TestHost.exe'))
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$worktree
foreach($argument in @('--mode','ui-draft-interactive','--scratch',$root,'--inventory-count',$InventoryCount.ToString([Globalization.CultureInfo]::InvariantCulture))){$start.ArgumentList.Add($argument)}
$native=$null;$identity=$null;$exitCode=$null;$failure=$null
try {
    $native=[Diagnostics.Process]::Start($start)
    $identity=@{ProcessId=$native.Id;StartedUtc=$native.StartTime.ToUniversalTime().ToString('o');Executable=$native.MainModule.FileName;FixtureId=$FixtureId;Root=$root}
    $identity|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'process.json')
    if(-not $native.WaitForExit(540000)){throw 'Owned UI exceeded its finite lifetime.'}
    $exitCode=$native.ExitCode
    if($exitCode -ne 0){throw ('Owned UI failed: '+$exitCode)}
} catch {$failure=$_.Exception.Message}
finally {
    if($null -ne $native) {
        try {
            if(-not $native.HasExited) {
                if($native.StartTime.ToUniversalTime().ToString('o') -ne $identity.StartedUtc -or $native.MainModule.FileName -ne $identity.Executable){throw 'Owned UI identity changed; no termination authorised.'}
                $native.Kill($true)
            }
            if(-not $native.WaitForExit(10000)){throw 'Owned UI was not joined.'}
        } finally{$native.Dispose()}
    }
    Assert-NoReparse $root
    foreach($item in Get-ChildItem -LiteralPath $root -Force -Recurse) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Owned UI tree contains a reparse point; preserve it.'}
    }
    foreach($name in @('ui-result.json','ui-process.json','configuration.json','draft.json')) {
        $path=Join-Path $root $name
        if(Test-Path -LiteralPath $path){Copy-Item -LiteralPath $path -Destination (Join-Path $evidence $name)}
    }
    # Recheck the absolute target before recursive removal of this owned root.
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ne $parent -or [IO.Path]::GetFileName($root) -ne $FixtureId){throw 'Cleanup target changed.'}
    Remove-Item -LiteralPath $root -Recurse -Force
    $after=Read-Normal
    $after|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $evidence 'after.json')
    foreach($file in $before.Files){if(@($after.Files|Where-Object Path -eq $file.Path)[0].Sha256 -ne $file.Sha256){throw 'Normal configuration/authentication changed.'}}
    foreach($service in $before.Services){$other=@($after.Services|Where-Object Name -eq $service.Name)[0];foreach($field in @('ProcessId','StartedUtc','Path','Account')){if($other[$field] -ne $service[$field]){throw 'Normal service changed.'}}}
    $live=Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
    try{$same=$null -ne $live -and $live.StartTime.ToUniversalTime().ToString('o') -eq $identity.StartedUtc}finally{if($null -ne $live){$live.Dispose()}}
    @{ProcessJoined=-not $same;RootRemoved=-not(Test-Path -LiteralPath $root);NormalInstallationUnchanged=$true;ExitCode=$exitCode;Failure=$failure}|ConvertTo-Json|
        Set-Content -LiteralPath (Join-Path $evidence 'cleanup.json')
    if($same){throw 'Owned UI identity remains alive.'}
}
if($failure){throw $failure}
