#Requires -Version 7.2
param([Parameter(Mandatory)][string]$Request)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$requestData=Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
if($requestData.StartsPostmaster){Assert-VaultFixturePostmasterLaunch -Root (Split-Path $PSScriptRoot) -Executable $requestData.Executable -Arguments $requestData.Arguments -InputText $requestData.InputText}
[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($requestData.Job)
# The parent protected this request/output directory. No child can execute before job admission.
$self=Get-Process -Id $PID
try {Get-VaultFixtureProcessIdentity $self | ConvertTo-Json -Compress | Set-Content -LiteralPath $requestData.OwnerPath} finally {$self.Dispose()}
$start=[Diagnostics.ProcessStartInfo]::new($requestData.Executable)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.WorkingDirectory=$PSScriptRoot
$start.RedirectStandardOutput=-not $requestData.StartsPostmaster;$start.RedirectStandardError=-not $requestData.StartsPostmaster;$start.RedirectStandardInput=$true
foreach($argument in $requestData.Arguments){$start.ArgumentList.Add($argument)}
$child=[Diagnostics.Process]::Start($start)
try {
    if(-not $requestData.StartsPostmaster){$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()}
    if($null -ne $requestData.InputText){$child.StandardInput.Write($requestData.InputText)};$child.StandardInput.Close()
    if(-not $child.WaitForExit($requestData.TimeoutSeconds*1000)){throw 'Owned child deadline exceeded.'}
    $output=if($requestData.StartsPostmaster){''}else{$stdout.GetAwaiter().GetResult()}
    $errorText=if($requestData.StartsPostmaster){''}else{$stderr.GetAwaiter().GetResult()}
    @{ExitCode=$child.ExitCode;Output=$output;Error=$errorText} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $requestData.ResultPath
} finally {if(-not $child.HasExited){$child.Kill($true)};if(-not $child.WaitForExit(5000)){throw 'Owned child remains.'};$child.Dispose()}
