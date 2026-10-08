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
if ($HostAddress -notin @('127.0.0.1','::1') -or $ClientKind -notin @('CommissionAuthentication','Npgsql','libpq','CallerFiles','Integrity','AccessUser','AccessGroup','AccessReopened','AccessDenied','RestartBefore','RestartAfter','RestartServer','PackagedClient','PackagedCleanup','InterruptedBefore','InterruptedAfter','InterruptedHeldServer','InterruptedReopenedServer')) { throw 'Actor requires one explicit loopback/client probe.' }
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
            if(($configuration.PSObject.Properties.Name -contains 'RunPackagedIdentityTests') -and $configuration.RunPackagedIdentityTests){120000}
            elseif(($configuration.PSObject.Properties.Name -contains 'RunRestartTests') -and $configuration.RunRestartTests){90000}else{60000}
        }elseif($ClientKind -eq 'RestartServer' -or $ClientKind -like 'Interrupted*Server'){70000}elseif($ClientKind -like 'Packaged*'){60000}else{25000}
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
    if($ClientKind -eq 'CommissionAuthentication') {
        if($Actor -ne 'System' -or -not $runtime.ValidatePreparedAuthentication){throw 'Authentication proof requires its exact owned SYSTEM actor.'}
        $proofRoot=Join-Path $Root 'administrator-proof'
        $context=Get-Content -LiteralPath (Join-Path $proofRoot 'context.json') -Raw | ConvertFrom-Json -AsHashtable
        if($context.NormalInstallation -or $context.Port -eq 5432 -or $context.DataDirectory -ine (Join-Path $Root 'data')){throw 'Normal authentication target refused by the fixture.'}
        Import-Module (Join-Path $proofRoot 'commission-administrator.psm1') -Force
        Import-Module (Join-Path $proofRoot 'commission-authentication.psm1') -Force
        $receipt=Invoke-CommissionAuthentication $context
        $results.Add(@{Kind='CommissionAuthentication';Receipt=$receipt})
    } elseif($ClientKind -eq 'Integrity') {
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
    } elseif ($ClientKind -like 'Interrupted*') {
        $server=$ClientKind -like '*Server'
        if(-not $configuration.RunInterruptedEffectTests -or -not $configuration.RunSingleVaultTests -or
            ($server -and $Actor -ne 'System') -or (-not $server -and $Actor -ne 'A')){throw 'Interrupted proof requires its exact owned native actor.'}
        $phase=switch($ClientKind){InterruptedBefore{'before'} InterruptedAfter{'after'} InterruptedHeldServer{'held-server'} InterruptedReopenedServer{'reopened-server'}}
        $result=Invoke-ActorTool $runtime.Dotnet @((Join-Path $Root 'runtime/FluxVault.TestHost.dll'),'--mode','windows-interrupted-effect',
            '--configuration',(Join-Path $Root 'runtime/database-probe.json'),'--actor',$Actor,'--phase',$phase)
        if($ClientKind -eq 'InterruptedHeldServer') {
            $termination=Join-Path $Root 'runtime/interrupted-termination.json'
            $deadline=[DateTime]::UtcNow.AddSeconds(5)
            while(-not(Test-Path -LiteralPath $termination)){if([DateTime]::UtcNow -gt $deadline){throw 'Expected owned termination proof was not published.'};Start-Sleep -Milliseconds 20}
            $fixtureOwner=(Get-Acl -LiteralPath $Root).GetOwner([Security.Principal.SecurityIdentifier]).Value
            Assert-VaultFixtureTrustedPath $termination -AdditionalTrustedOwnerSid $fixtureOwner
            if((Get-Item -LiteralPath $termination).Length -gt 16384){throw 'Termination proof exceeds its bound.'}
            $proof=Get-Content -LiteralPath $termination -Raw | ConvertFrom-Json
            if($proof.FixtureId -ne $configuration.FixtureId -or $proof.RunId -ne $RunId -or
                -not $proof.WithinHoldWindow -or $result.ExitCode -eq 0 -or $result.ExitCode -ne $proof.ExitCode){throw 'Native server exit did not match the owned termination.'}
            $results.Add(@{Kind='InterruptedTermination';ExpectedOwnedTermination=$true;ExitCode=$result.ExitCode})
        } else {
            if($result.ExitCode -ne 0){throw ('Native interrupted proof failed: '+$result.Error)}
            $results.Add(@{Kind='Interrupted';Result=($result.Output | ConvertFrom-Json)})
        }
    } elseif ($ClientKind -like 'Restart*') {
        if(-not $configuration.RunRestartTests -or -not $configuration.RunSingleVaultTests -or
            ($ClientKind -eq 'RestartServer' -and $Actor -ne 'System') -or
            ($ClientKind -ne 'RestartServer' -and $Actor -ne 'A')){throw 'Restart proof requires its exact owned native actor.'}
        $phase=switch($ClientKind){RestartBefore{'before'} RestartAfter{'after'} RestartServer{'server'}}
        $result=Invoke-ActorTool $runtime.Dotnet @((Join-Path $Root 'runtime/FluxVault.TestHost.dll'),'--mode','windows-service-restart',
            '--configuration',(Join-Path $Root 'runtime/database-probe.json'),'--actor',$Actor,'--phase',$phase)
        if($result.ExitCode -ne 0){throw ('Native restart proof failed: '+$result.Error)}
        $results.Add(@{Kind='Restart';Result=($result.Output | ConvertFrom-Json)})
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
