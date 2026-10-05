#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][ValidateSet('System','A','B')][string]$Actor,
    [Parameter(Mandatory)][string]$RunId, [string]$HostAddress, [string]$ClientKind, [string]$ParentJob)
trap {
    # Preserve failures before the normal result writer. A/B diagnostics explain failure only; they are not authority.
    $failure=$_
    try {
        $diagnosticId=[guid]::Empty
        if([guid]::TryParseExact((Split-Path $Root -Leaf),'N',[ref]$diagnosticId) -and
            [IO.Path]::GetFullPath($Root) -eq (Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $diagnosticId.ToString('N'))) {
            $diagnosticPath=if($Actor -eq 'System'){Join-Path $Root 'system-actor-error.json'}else{
                $diagnosticRun=[guid]::Empty
                if(-not [guid]::TryParseExact($RunId,'N',[ref]$diagnosticRun)){throw 'Invalid early diagnostic identity.'}
                Join-Path $Root ("output-$Actor/$RunId-error.json")
            }
            $message=$failure.Exception.Message
            @{RunId=$RunId;WindowsSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;ErrorKind=$failure.Exception.GetType().FullName;
                Message=$message.Substring(0,[Math]::Min(4096,$message.Length));Location=$failure.ScriptStackTrace} | ConvertTo-Json -Depth 3 |
                Set-Content -LiteralPath $diagnosticPath
        }
    } catch { }
    exit 1
}
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$parsed = [guid]::Empty
if (-not [guid]::TryParseExact((Split-Path $Root -Leaf), 'N', [ref]$parsed) -or $parsed -eq [guid]::Empty -or
    [IO.Path]::GetFullPath($Root) -ne (Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $parsed.ToString('N'))) { throw 'Actor fixture root mismatch.' }
$configuration = Get-Content -LiteralPath (Join-Path $Root 'runtime/database-probe.json') -Raw | ConvertFrom-Json
$runtime = Get-Content -LiteralPath (Join-Path $Root 'runtime/actor-runtime.json') -Raw | ConvertFrom-Json
if ($Actor -eq 'System' -and $RunId -eq 'mission') {
    $mission = Get-Content -LiteralPath (Join-Path $Root 'runtime/system-mission.json') -Raw | ConvertFrom-Json
    $RunId = $mission.RunId; $HostAddress = $mission.HostAddress; $ClientKind = $mission.ClientKind
}
$actorJob=if($Actor -eq 'System'){$mission.Job}else{$ParentJob}
try {
    if($Actor -eq 'System'){[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($actorJob)}
    else{[FluxVault.Fixtures.OwnedWindowsJob]::WaitForCurrentAdmission($actorJob)}
}
catch {
    $admissionError=$_.Exception.Message
    try { $admissionState=[FluxVault.Fixtures.OwnedWindowsJob]::CurrentContainment() } catch { $admissionState='Containment query failed: '+$_.Exception.Message }
    throw ($admissionError+'; '+$admissionState)
}
if ($HostAddress -notin @('127.0.0.1','::1') -or $ClientKind -notin @('Npgsql','libpq','CallerFiles','Integrity','AccessUser','AccessGroup','AccessReopened','AccessDenied','PackagedClient','PackagedCleanup')) { throw 'Actor requires one explicit loopback/client probe.' }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($sid -ne $configuration.Actors.$Actor -or -not [guid]::TryParseExact($RunId, 'N', [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw 'Actor identity mismatch.' }
$expectedRoot = Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $configuration.FixtureId
if ([IO.Path]::GetFullPath($Root) -ne $expectedRoot) { throw 'Actor fixture root mismatch.' }
$output = Join-Path $Root ("output-" + $Actor)
$processJournal = Join-Path $output ($RunId + '-processes.jsonl')
$results = [Collections.Generic.List[object]]::new()
$self = Get-Process -Id $PID
try { (Get-VaultFixtureProcessIdentity $self | ConvertTo-Json -Compress) | Add-Content -LiteralPath $processJournal } finally { $self.Dispose() }
function Invoke-ActorTool {
    param([string]$Executable, [string[]]$Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WorkingDirectory=$runtime.WorkingDirectory
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in @($start.Environment.Keys | Where-Object { $_ -match '^(PG|NPGSQL|DOTNET_|MSBUILD|VSTEST|COMPlus_|CORECLR_|COR_|FLUXVAULT_)' })) { $start.Environment.Remove($key) | Out-Null }
    $start.Environment['PATH']=$runtime.SafePath
    if($ClientKind -eq 'Integrity') {
        $start.WorkingDirectory=Join-Path $Root 'integrity'
        $start.Environment['TEMP']=Join-Path $Root 'integrity/temp'
        $start.Environment['TMP']=$start.Environment['TEMP']
        $start.Environment['DOTNET_CLI_HOME']=$start.Environment['TEMP']
        $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
        $start.Environment['DOTNET_NOLOGO']='1'
        $start.Environment['FLUXVAULT_TEST_PG_MARKER']=Join-Path $Root 'runtime/integrity-cluster.json'
        $start.Environment['FLUXVAULT_INTEGRITY_HOST']=Join-Path $Root 'runtime/FluxVault.TestHost.dll'
        $start.Environment['FLUXVAULT_TEST_DOTNET']=$runtime.Dotnet
    }
    $child = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        $childIdentity = Get-VaultFixtureLiveProcessIdentity $child
        if ($null -ne $childIdentity) { ($childIdentity | ConvertTo-Json -Compress) | Add-Content -LiteralPath $processJournal }
        $limit=if($ClientKind -eq 'Integrity'){[int]$runtime.IntegrityTimeoutSeconds*1000}elseif($ClientKind -eq 'CallerFiles'){
            if(($configuration.PSObject.Properties.Name -contains 'RunPackagedIdentityTests') -and $configuration.RunPackagedIdentityTests){120000}else{60000}
        }elseif($ClientKind -like 'Packaged*'){60000}else{20000}
        if (-not $child.WaitForExit($limit)) { throw 'Actor tool exceeded its finite deadline.' }
        return @{ ExitCode=$child.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult() }
    } finally {
        if (-not $child.HasExited) { Stop-VaultFixtureProcessTree (Get-VaultFixtureProcessIdentity $child) | Out-Null }
        if (-not $child.WaitForExit(5000)) { throw 'Actor tool remains.' }
        $child.Dispose()
    }
}
try {
    $probeId = [guid]::NewGuid().ToString('N')
    if($ClientKind -eq 'Integrity') {
        if($Actor -ne 'System' -or $HostAddress -ne '127.0.0.1'){throw 'The owned integrity suite requires the SYSTEM actor.'}
        $rootOwner=(Get-Acl -LiteralPath $Root).GetOwner([Security.Principal.SecurityIdentifier]).Value
        foreach($path in @($Root,(Join-Path $Root 'runtime'),(Join-Path $Root 'integrity'),$runtime.Dotnet,$runtime.Vstest)) {
            Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $rootOwner
        }
        foreach($entry in Get-ChildItem -LiteralPath (Join-Path $Root 'runtime') -Recurse -Force) {
            Assert-VaultFixtureTrustedPath $entry.FullName -AdditionalTrustedOwnerSid $rootOwner
        }
        $suite=Invoke-ActorTool $runtime.Dotnet @('exec',$runtime.Vstest,(Join-Path $Root 'runtime/integration/FluxVault.Integration.Tests.dll'),
            '/TestCaseFilter:Category=RequiresPostgreSql','/Logger:trx;LogFileName=PostgreSql.trx',('/ResultsDirectory:'+(Join-Path $Root 'integrity/results')))
        $results.Add(@{Kind='Integrity';ExitCode=$suite.ExitCode;Output=$suite.Output;Error=$suite.Error})
    } elseif ($ClientKind -like 'Packaged*') {
        if($Actor -notin @('A','B')){throw 'A packaged client requires an ordinary fixture account.'}
        $selfProcess=Get-Process -Id $PID
        try {$pwsh=$selfProcess.Path} finally {$selfProcess.Dispose()}
        $action=if($ClientKind -eq 'PackagedClient'){'Client'}else{'Cleanup'}
        $result=Invoke-ActorTool $pwsh @('-NoProfile','-NonInteractive','-File',(Join-Path $Root 'runtime/invoke-packaged-identity.ps1'),
            '-Root',$Root,'-Actor',$Actor,'-Action',$action)
        if($result.ExitCode -ne 0){throw ('Packaged identity proof failed: '+$result.Error)}
        $results.Add(@{Kind='Packaged';Result=($result.Output | ConvertFrom-Json)})
    } elseif ($ClientKind -like 'Access*') {
        if($Actor -ne 'B'){throw 'Access-grant validation requires the owned ordinary B actor.'}
        $phase=switch($ClientKind){AccessUser{'user'} AccessGroup{'group'} AccessReopened{'reopened'} AccessDenied{'denied'}}
        $groupSid=(Get-Content -LiteralPath (Join-Path $Root 'runtime/access-group-sid') -Raw).Trim()
        $result=Invoke-ActorTool $runtime.Dotnet @((Join-Path $Root 'runtime/FluxVault.TestHost.dll'),'--mode','windows-access-grant',
            '--configuration',(Join-Path $Root 'runtime/database-probe.json'),'--actor','B','--phase',$phase,'--group-sid',$groupSid)
        if($result.ExitCode -ne 0){throw ('Native access proof failed: '+$result.Error)}
        $results.Add(@{Kind='Access';Result=($result.Output | ConvertFrom-Json)})
    } elseif ($ClientKind -eq 'CallerFiles') {
        $singleVault=($configuration.PSObject.Properties.Name -contains 'RunSingleVaultTests') -and $configuration.RunSingleVaultTests
        $mode=if($singleVault){if($Actor -eq 'System'){'windows-single-server'}else{'windows-single-client'}}else{if($Actor -eq 'System'){'windows-file-server'}else{'windows-file-client'}}
        $result=Invoke-ActorTool -Executable $runtime.Dotnet -Arguments @((Join-Path $Root 'runtime/FluxVault.TestHost.dll'),'--mode',$mode,
            '--configuration',(Join-Path $Root 'runtime/database-probe.json'),'--actor',$Actor)
        if($result.ExitCode -ne 0){throw ('Native caller file proof failed: '+$result.Error)}
        $results.Add(@{Kind='CallerFiles';Result=($result.Output | ConvertFrom-Json)})
    } elseif ($ClientKind -eq 'Npgsql') {
        $result = Invoke-ActorTool -Executable $runtime.Dotnet -Arguments @((Join-Path $Root 'runtime/FluxVault.TestHost.dll'), '--mode','windows-db-probe',
            '--configuration',(Join-Path $Root 'runtime/database-probe.json'), '--host',$HostAddress,'--actor',$Actor,'--probe-id',$probeId)
        if ($result.ExitCode -ne 0) { throw "Npgsql probe failed before its result: $($result.Error)" }
        $results.Add(@{ Kind='Npgsql'; Host=$HostAddress; Result=($result.Output | ConvertFrom-Json) })
    } else {
        $connection = "host=$HostAddress port=$($configuration.Port) dbname=fv_gate_261003 user=fv_gate_service connect_timeout=5 require_auth=sspi application_name=FVGate.$probeId.libpq"
        $libpq = Invoke-ActorTool -Executable $runtime.Psql -Arguments @('-X','-w','-A','-t','-v','ON_ERROR_STOP=1','--dbname',$connection,'-c',"SELECT current_setting('fluxvault.test_instance'), current_database(), current_user")
        $results.Add(@{ Kind='libpq'; Host=$HostAddress; ProbeId=$probeId; ExitCode=$libpq.ExitCode; Output=$libpq.Output.Trim(); Error=$libpq.Error.Trim() })
    }
    $readAllowed = $false; $writeAllowed = $false
    try { $stream=[IO.File]::OpenRead((Join-Path $Root 'catalogue/private.bin')); $stream.Dispose(); $readAllowed=$true } catch [UnauthorizedAccessException] { }
    try { $stream=[IO.File]::Open((Join-Path $Root 'runtime/database-probe.json'), 'Open', 'Write', 'None'); $stream.Dispose(); $writeAllowed=$true } catch [UnauthorizedAccessException] { }
    $results.Add(@{ Kind='ACL'; ReadProtected=$readAllowed; WriteRuntime=$writeAllowed; WindowsSid=$sid })
    $resultPath=Join-Path $output ($RunId+'-result.json')
    @{ RunId=$RunId; Actor=$Actor; WindowsSid=$sid; Results=$results.ToArray(); Complete=$true } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($resultPath+'.tmp')
    [IO.File]::Move($resultPath+'.tmp',$resultPath)
} catch {
    $resultPath=Join-Path $output ($RunId+'-result.json')
    @{ RunId=$RunId; Actor=$Actor; WindowsSid=$sid; Complete=$false; Error=$_.Exception.Message } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($resultPath+'.tmp')
    [IO.File]::Move($resultPath+'.tmp',$resultPath)
    exit 1
}
