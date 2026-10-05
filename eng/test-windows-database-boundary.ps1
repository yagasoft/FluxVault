#Requires -Version 7.2
[CmdletBinding()]
param([ValidateSet('Run','Cleanup')][string]$Mode = 'Run',
    [string]$FixtureId = '43a32654d27a4e8fb0b20012702300af',
    [switch]$FreshReference,
    [switch]$RunCatalogueTests,
    [switch]$RunMetadataTests,
    [switch]$RunCallerFileTests,
    [switch]$RunSingleVaultTests,
    [switch]$RunNativeAccessTests,
    [switch]$RunRestartTests,
    [switch]$RunPackagedIdentityTests,
    [string]$PackageSdkDirectory = 'E:\Windows Kits\10\bin\10.0.28000.0\x64',
    [switch]$RunIntegrityTests,
    [ValidateRange(60,600)][int]$IntegrityTimeoutSeconds = 300,
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot ("../docs/verification/2026-10-03-next002-windows-fixture/live/$FixtureId")))
$ErrorActionPreference = 'Stop'
if($RunMetadataTests -and -not $RunCatalogueTests){throw 'Metadata proof requires the catalogue contracts.'}
if($RunCallerFileTests -and $RunSingleVaultTests){throw 'Select one native file workflow per fresh fixture.'}
if($RunIntegrityTests -and ($RunCallerFileTests -or $RunSingleVaultTests)){throw 'Run the integrity suite in its own fresh fixture.'}
if($RunNativeAccessTests -and -not $RunSingleVaultTests){throw 'Native access proof requires the single-vault product workflow.'}
if($RunPackagedIdentityTests -and -not $RunSingleVaultTests){throw 'Packaged identity proof requires the single-vault product workflow.'}
if($RunRestartTests -and (-not $RunSingleVaultTests -or $RunNativeAccessTests -or $RunPackagedIdentityTests)){
    throw 'Restart proof requires an exclusive single-vault extension within the existing resource bound.'
}
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'fixtures/vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'fixtures/verified-postgresql-snapshot.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'fixtures/packaged-identity.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'fixtures/fixture-cng-keys.psm1') -Force
Import-VaultFixtureJobType
$fixtureParent = 'C:\ProgramData\FluxVault.Tests\NEXT002'
$fixtureRoot = Resolve-VaultFixtureRoot -Root (Join-Path $fixtureParent $FixtureId) -Parent $fixtureParent -FixtureId $FixtureId
$fixtureBin = Join-Path $fixtureRoot 'postgresql/bin'
$fixturePwsh = (Get-Command pwsh).Source
$fixtureDotnet = (Get-Command dotnet).Source
$fixtureVstest = $null
if($RunIntegrityTests) {
    # Bypass SDK resolution entirely: an ancestor global.json must never choose privileged code.
    $sdkRoot=Join-Path (Split-Path $fixtureDotnet -Parent) 'sdk'
    $sdk=@(Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object {$_.Name -match '^10\.0\.\d+$'} | Sort-Object {[version]$_.Name} -Descending)
    if(-not $sdk.Count){throw 'A trusted installed .NET 10 VSTest runtime is required.'}
    $fixtureVstest=Join-Path $sdk[0].FullName 'vstest.console.dll'
}
$fixtureTask = 'FluxVault-NEXT002-261003-SYSTEM'
$fixtureDescription = "FluxVault N2 $FixtureId" # Windows local-account descriptions allow at most 48 characters.
$fixtureData = Join-Path $fixtureRoot 'data'
$fixtureJournal = $null
$fixturePort = 0
$fixtureJobs = @{}
$fixtureBefore = $null
$fixtureFailure = $null
$fixtureFailureLocation = $null
$fixtureObservations = [Collections.Generic.List[object]]::new()
$fixtureCredentials = @{}
$fixtureEvidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole('Administrators')) { throw 'An elevated, authorised fixture runner is required.' }

function Get-InstallationSnapshot {
    $services = foreach ($name in @('postgresql-x64-18','FluxVaultService')) {
        $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
        if ($null -eq $service -or $service.State -ne 'Running') { throw "Original service unavailable: $name" }
        $process = Get-Process -Id $service.ProcessId
        try { @{ Name=$name; StartName=$service.StartName; PathName=$service.PathName; Identity=(Get-VaultFixtureProcessIdentity $process) } } finally { $process.Dispose() }
    }
    $files = foreach ($path in @('D:\Program Files\PostgreSQL\18\data\pg_hba.conf','D:\Program Files\PostgreSQL\18\data\pg_ident.conf','C:\ProgramData\FluxVault\config.json')) {
        @{ Path=$path; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    $snapshot=@{Services=@($services);Files=@($files)}
    if($RunPackagedIdentityTests -or ($null -ne $fixtureJournal -and @($fixtureJournal.Resources | Where-Object Kind -eq 'PackageUser').Count)) {
        $policy=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
        $developer=if($null -ne $policy){$policy.PSObject.Properties['AllowDevelopmentWithoutDevLicense']}else{$null}
        $sideload=if($null -ne $policy){$policy.PSObject.Properties['AllowAllTrustedApps']}else{$null}
        $snapshot.PackageBoundary=@{DeveloperModePresent=($null -ne $developer);DeveloperMode=$(if($null -ne $developer){$developer.Value}else{$null});
            SideloadPresent=($null -ne $sideload);Sideload=$(if($null -ne $sideload){$sideload.Value}else{$null});
            RunnerTrustedPeople=@(Get-ChildItem Cert:\CurrentUser\TrustedPeople | ForEach-Object Thumbprint | Sort-Object);
            MachineTrustedPeople=@(Get-ChildItem Cert:\LocalMachine\TrustedPeople | ForEach-Object Thumbprint | Sort-Object)}
    }
    return $snapshot
}

function Invoke-OwnedTool {
    param([string]$Executable, [string[]]$Arguments, [string]$InputText, [hashtable]$Environment = @{}, [int]$TimeoutSeconds = 40, [switch]$StartsPostmaster)
    if($StartsPostmaster){Assert-VaultFixturePostmasterLaunch -Root $fixtureRoot -Executable $Executable -Arguments $Arguments -InputText $InputText}
    $name=Add-VaultFixtureToolIntent $fixtureJournal $fixturePwsh
    $request=Join-Path $fixtureRoot ($name+'-request.json')
    $ownerPath=Join-Path $fixtureRoot ($name+'-owner.json')
    $resultPath=Join-Path $fixtureRoot ($name+'-result.json')
    @{Executable=$Executable;Arguments=$Arguments;InputText=$InputText;TimeoutSeconds=$TimeoutSeconds;StartsPostmaster=[bool]$StartsPostmaster;Job=$fixtureJobs.Cluster.Name;OwnerPath=$ownerPath;ResultPath=$resultPath} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $request
    $start = [Diagnostics.ProcessStartInfo]::new($fixturePwsh)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=-not $StartsPostmaster; $start.RedirectStandardError=-not $StartsPostmaster
    $start.RedirectStandardInput=$true
    $start.WorkingDirectory=Join-Path $fixtureRoot 'runtime'
    foreach ($argument in @('-NoProfile','-NonInteractive','-File',(Join-Path $fixtureRoot 'runtime/invoke-owned-tool.ps1'),'-Request',$request)) { $start.ArgumentList.Add($argument) }
    foreach ($key in @($start.Environment.Keys | Where-Object { $_ -like 'PG*' -or $_ -like 'NPGSQL*' })) { $start.Environment.Remove($key) | Out-Null }
    $start.Environment['PATH']=$fixtureBin+';'+(Join-Path $env:SystemRoot 'System32')+';'+$env:SystemRoot
    foreach ($key in $Environment.Keys) { $start.Environment[$key]=$Environment[$key] }
    $identity = $null
    $process = [Diagnostics.Process]::Start($start)
    foreach ($key in $Environment.Keys) { $start.Environment.Remove($key) | Out-Null }
    try {
        if(-not $StartsPostmaster){$stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()}
        $identity = Get-VaultFixtureLiveProcessIdentity $process
        if($null -ne $identity){Set-VaultFixtureResourceState $fixtureJournal Process $name Created $identity}
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(($TimeoutSeconds+10)*1000)) { throw 'Owned tool exceeded its finite deadline.' }
        if($process.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $resultPath)){
            $diagnostic=if($StartsPostmaster){'See owned PostgreSQL startup log.'}else{$stderr.GetAwaiter().GetResult()}
            throw ('Owned supervisor failed: '+$diagnostic)
        }
        if(-not $StartsPostmaster){$null=$stdout.GetAwaiter().GetResult()}
        $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -AsHashtable
        # Tool output has no passwords; bootstrap SQL never includes its generated password.
        $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixtureRoot ($name+'.json'))
        return $result
    } finally {
        if (-not $process.HasExited) { Stop-VaultFixtureProcessTree (Get-VaultFixtureProcessIdentity $process) | Out-Null }
        if (-not $process.WaitForExit(5000)) { throw 'Owned tool did not exit.' }
        if ($null -ne $identity) { Set-VaultFixtureResourceState $fixtureJournal Process $name Removed $identity }
        $process.Dispose()
    }
}

function Get-OwnedPostmaster {
    $pidFile=Join-Path $fixtureData 'postmaster.pid'
    if (-not (Test-Path -LiteralPath $pidFile)) { return $null }
    $lines=@(Get-Content -LiteralPath $pidFile)
    if ($lines.Count -lt 4 -or [IO.Path]::GetFullPath($lines[1]) -ne $fixtureData -or [int]$lines[3] -ne $fixturePort) { throw 'Owned postmaster data/port identity changed.' }
    $process=Get-Process -Id ([int]$lines[0]) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    try {
        $identity=Get-VaultFixtureProcessIdentity $process
        if ($identity.Executable -ne (Join-Path $fixtureBin 'postgres.exe') -or
            [Math]::Abs(([DateTimeOffset]$identity.StartedUtc).ToUnixTimeSeconds()-[long]$lines[2]) -gt 2) { throw 'Owned postmaster executable/start identity changed.' }
        $identity.DataDirectory=$fixtureData; $identity.Port=$fixturePort
        return $identity
    } finally { $process.Dispose() }
}

function Read-ActorResult {
    param([string]$Actor,[string]$RunId,[int]$TimeoutSeconds=70)
    $path=Join-Path $fixtureRoot ("output-$Actor/$RunId-result.json")
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $path)) {
        $earlyError=if($Actor -eq 'System'){Join-Path $fixtureRoot 'system-actor-error.json'}else{Join-Path $fixtureRoot ("output-$Actor/$RunId-error.json")}
        if(Test-Path -LiteralPath $earlyError){
            if((Get-Item -LiteralPath $earlyError).Length -gt 16384){throw 'Actor early diagnostic exceeds its bound.'}
            $diagnostic=Read-VaultFixtureLog $earlyError | ConvertFrom-Json -Depth 4
            if($diagnostic.RunId -notin @($RunId,'mission')){throw 'Actor early diagnostic belongs to another invocation.'}
            throw ('Actor '+$Actor+' failed before its result: '+$diagnostic.Message)
        }
        if([DateTime]::UtcNow -gt $deadline){throw 'Actor deadline exceeded.'}; Start-Sleep -Milliseconds 100
    }
    $result=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($result.RunId -ne $RunId -or $result.Actor -ne $Actor -or -not $result.Complete) { throw "Actor probe incomplete: $($result.Error)" }
    return $result
}

function Invoke-SystemActor {
    param([string]$HostAddress, [string]$ClientKind)
    $runId=[guid]::NewGuid().ToString('N')
    $arguments='-NoProfile -NonInteractive -File "'+(Join-Path $fixtureRoot 'runtime/invoke-windows-database-actor.ps1')+'" -Root "'+$fixtureRoot+'" -Actor System -RunId mission'
    $action=New-ScheduledTaskAction -Execute $fixturePwsh -Argument $arguments
    $taskSeconds=if($RunIntegrityTests){$IntegrityTimeoutSeconds+30}elseif($RunPackagedIdentityTests){150}else{120}
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds $taskSeconds) -MultipleInstances IgnoreNew
    $existing=Get-ScheduledTask -TaskName $fixtureTask -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        $resource=@($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Task' -and $_.Name -eq $fixtureTask -and $_.State -eq 'Created'})
        if ($resource.Count -ne 1 -or (Get-TaskHash) -ne $resource[0].Identity.DefinitionSha256) { throw 'Scheduled task ownership changed.' }
        if($existing.State -eq 'Running'){throw 'Previous SYSTEM actor is still running.'}
    } else {
        Add-VaultFixtureIntent $fixtureJournal Task $fixtureTask
        Register-ScheduledTask -TaskName $fixtureTask -Action $action -Settings $settings -User 'SYSTEM' -RunLevel Highest -Description $fixtureDescription | Out-Null
        $identity=@{ DefinitionSha256=(Get-TaskHash); InstanceId='' }
        Set-VaultFixtureResourceState $fixtureJournal Task $fixtureTask Created $identity
    }
    $scheduler=$null;$folder=$null;$registered=$null;$instance=$null
    $instanceId=$null
    try {
        $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
        $folder=$scheduler.GetFolder('\');$registered=$folder.GetTask($fixtureTask)
        Wait-SystemTaskIdle $registered
        # Scheduler instances may have different native parent jobs. Never reuse their nested child job.
        $jobName='system-'+$runId
        $kernelName='Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')
        $jobIdentity=@{KernelName=$kernelName;OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
        Add-VaultFixtureIntent $fixtureJournal Job $jobName $jobIdentity
        $fixtureJobs[$jobName]=[FluxVault.Fixtures.OwnedWindowsJob]::Create($kernelName,$actors.System)
        Set-VaultFixtureResourceState $fixtureJournal Job $jobName Created $jobIdentity
        # A previous task must finish before its protected mission can be replaced.
        @{RunId=$runId;HostAddress=$HostAddress;ClientKind=$ClientKind;Job=$kernelName} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/system-mission.json')
        $instance=$registered.Run($null)
        $instanceId=$instance.InstanceGuid
        $parsedInstance=[guid]::Empty
        if(-not [guid]::TryParse($instanceId,[ref]$parsedInstance) -or $parsedInstance -eq [guid]::Empty){throw 'SYSTEM dispatch did not return a task instance identity.'}
        Set-VaultFixtureTaskInstance $fixtureJournal $fixtureTask (Get-TaskHash) $instanceId
        if ($ClientKind -eq 'CallerFiles') {
            $ready=Join-Path $fixtureRoot 'runtime/caller-files-ready.json'
            $deadline=[DateTime]::UtcNow.AddSeconds(15)
            while (-not (Test-Path -LiteralPath $ready)) {
                if((Test-Path -LiteralPath (Join-Path $fixtureRoot ("output-System/$runId-result.json"))) -or
                    (Test-Path -LiteralPath (Join-Path $fixtureRoot 'system-actor-error.json'))){
                    $early=Read-ActorResult System $runId
                    throw 'Native server finished before publishing readiness.'
                }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'Caller file server readiness exceeded its deadline.' }
                Start-Sleep -Milliseconds 100
            }
            foreach ($actor in @('A','B')) {
                $native=Invoke-UserActor $actor $HostAddress $ClientKind
                $fixtureObservations.Add(@{NativeCallerFiles=$native})
            }
            if($RunNativeAccessTests){Invoke-NativeAccessProof}
            if($RunRestartTests){Invoke-RestartGrant 'grant-group'}
            if($RunPackagedIdentityTests) {
                foreach($actor in @('A','B')) {
                    $packaged=Invoke-UserActor $actor '127.0.0.1' 'PackagedClient'
                    $proof=@($packaged.Results | Where-Object Kind -eq 'Packaged')
                    if($proof.Count -ne 1 -or -not $proof[0].Result.Proof.NativePackagedVerified -or
                        -not $proof[0].Result.PackageRemoved -or -not $proof[0].Result.CertificateRemoved){throw 'Packaged native validation or cleanup is incomplete.'}
                    $fixtureObservations.Add(@{NativePackaged=$packaged})
                }
            }
            'stop' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/caller-files-stop')
        } elseif($ClientKind -eq 'RestartServer') {
            $ready=Join-Path $fixtureRoot 'runtime/restart-ready.json'
            $deadline=[DateTime]::UtcNow.AddSeconds(15)
            while(-not(Test-Path -LiteralPath $ready)) {
                if((Test-Path -LiteralPath (Join-Path $fixtureRoot ("output-System/$runId-result.json"))) -or
                    (Test-Path -LiteralPath (Join-Path $fixtureRoot 'system-actor-error.json'))){
                    $early=Read-ActorResult System $runId
                    throw 'Replacement server finished before publishing readiness.'
                }
                if([DateTime]::UtcNow -gt $deadline){throw 'Replacement server readiness exceeded its deadline.'}
                Start-Sleep -Milliseconds 100
            }
            $replacementIdentity=Read-SystemServerIdentity $runId -ReadyPath $ready
            Assert-ProcessAbsent $fixtureOriginalServer
            if($replacementIdentity.ProcessId -eq $fixtureOriginalServer.ProcessId -and
                $replacementIdentity.StartedUtc -eq $fixtureOriginalServer.StartedUtc){throw 'Server process was not replaced.'}
            $fixtureObservations.Add(@{NativeRestartIdentity=@{Original=$fixtureOriginalServer;Replacement=$replacementIdentity;OriginalJoinedBeforeReplacement=$true}})
            $creator=Invoke-UserActor 'A' '127.0.0.1' 'RestartAfter'
            Assert-RestartCreator $creator 'after' 17
            $fixtureObservations.Add(@{NativeRestart=$creator})
            Invoke-RestartGrant 'revoke'
            'stop' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/restart-stop')
        }
        $result=Read-ActorResult System $runId -TimeoutSeconds $(if($ClientKind -eq 'Integrity'){$IntegrityTimeoutSeconds+10}elseif($RunPackagedIdentityTests -and $ClientKind -eq 'CallerFiles'){130}elseif($RunRestartTests){100}else{70})
        Wait-SystemTaskIdle $registered
        Join-ActorProcesses System $runId
        if($fixtureJobs[$jobName].ProcessIds().Length){throw 'SYSTEM invocation job is not empty after completion.'}
        Stop-OwnedJob (@($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Job' -and $_.Name -eq $jobName})[0])
        return $result
    } finally {
        try {
            $info=Get-ScheduledTaskInfo -TaskName $fixtureTask
            $state=(Get-ScheduledTask -TaskName $fixtureTask).State.ToString()
            $instanceState=if($null -eq $instance){'NotDispatched'}else{try{$instance.Refresh();$instance.State.ToString()}catch{'NoLongerAvailable'}}
            @{RunId=$runId;InstanceId=$instanceId;InstanceState=$instanceState;TaskState=$state;LastTaskResult=$info.LastTaskResult;LastRunTime=$info.LastRunTime.ToUniversalTime().ToString('o')} |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot ("system-scheduler-$runId.json"))
        } finally {
            foreach($reference in @($instance,$registered,$folder,$scheduler)){if($null -ne $reference -and [Runtime.InteropServices.Marshal]::IsComObject($reference)){$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($reference)}}
        }
    }
}

function Wait-SystemTaskIdle {
    param($RegisteredTask)
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $instances=$RegisteredTask.GetInstances(0)
        try {if($instances.Count -eq 0){return}} finally {$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($instances)}
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    throw 'Previous SYSTEM task instance did not exit within its deadline.'
}

function Invoke-NativeAccessProof {
    # Only existing fixture-owned principals and the protected private runtime are affected.
    $ownedGroup=@($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Group' -and $_.Name -eq 'FVGate_261003' -and $_.State -eq 'Created'})
    if($ownedGroup.Count -ne 1){throw 'Missing owned access-test group.'}
    $currentGroup=Get-LocalGroup -Name $ownedGroup[0].Name
    if($currentGroup.SID.Value -ne $ownedGroup[0].Identity.Sid -or $currentGroup.Description -ne $fixtureDescription){throw 'Owned group changed.'}
    $currentUser=Get-LocalUser -Name 'FVGateB_261003'
    if($currentUser.SID.Value -ne $actors.B -or $currentUser.Description -ne $fixtureDescription){throw 'Owned B account changed.'}
    if(@(Get-LocalGroupMember -SID $currentGroup.SID).Count){throw 'Access-test group already has members.'}
    $currentGroup.SID.Value | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/access-group-sid')
    foreach($phase in @('grant-user','grant-group','revoke')) {
        $native=Invoke-OwnedTool $fixtureDotnet @((Join-Path $fixtureRoot 'runtime/FluxVault.TestHost.dll'),'--mode','windows-access-grant',
            '--configuration',(Join-Path $fixtureRoot 'runtime/database-probe.json'),'--actor','Elevated','--phase',$phase,'--group-sid',$currentGroup.SID.Value)
        if($native.ExitCode -ne 0){throw ('Elevated native access proof failed: '+$native.Error)}
        $proof=$native.Output | ConvertFrom-Json
        if(-not $proof.NativeAccessVerified -or -not $proof.NativeElevated -or $proof.Phase -ne $phase -or $proof.Passed -ne 3){throw 'Elevated native access proof is incomplete.'}
        $grantOperation=[guid]::Parse($proof.OperationId)
        if($grantOperation -eq [guid]::Empty){throw 'Access grant receipt is missing.'}
        $grantOperation.ToString('N') | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/access-operation-id')
        $fixtureObservations.Add(@{NativeAccess=$proof})
        if($phase -eq 'grant-group') {
            # Replacing the direct B grant must deny B before membership; otherwise
            # a retained user grant could masquerade as successful group evaluation.
            $notMember=Invoke-UserActor 'B' '127.0.0.1' 'AccessDenied'
            $notMemberProof=@($notMember.Results | Where-Object Kind -eq 'Access')
            if($notMemberProof.Count -ne 1 -or -not $notMemberProof[0].Result.NativeAccessVerified -or $notMemberProof[0].Result.NativeElevated){throw 'Nonmember denial proof is incomplete.'}
            $fixtureObservations.Add(@{NativeAccess=$notMember})
            Add-LocalGroupMember -SID $currentGroup.SID -Member $currentUser
        }
        $clientKind=switch($phase){'grant-user'{'AccessUser'} 'grant-group'{'AccessGroup'} 'revoke'{'AccessDenied'}}
        $ordinary=Invoke-UserActor 'B' '127.0.0.1' $clientKind
        $ordinaryProof=@($ordinary.Results | Where-Object Kind -eq 'Access')
        if($ordinaryProof.Count -ne 1 -or -not $ordinaryProof[0].Result.NativeAccessVerified -or $ordinaryProof[0].Result.NativeElevated){throw 'Ordinary native access proof is incomplete.'}
        $fixtureObservations.Add(@{NativeAccess=$ordinary})
        if($phase -eq 'grant-group') {
            'reopen' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/caller-access-reopen')
            $deadline=[datetime]::UtcNow.AddSeconds(8)
            while(-not (Test-Path -LiteralPath (Join-Path $fixtureRoot 'runtime/caller-access-reopened'))) {
                if([datetime]::UtcNow -gt $deadline){throw 'Owned service reopen exceeded its deadline.'}
                Start-Sleep -Milliseconds 50
            }
            $reopened=Invoke-UserActor 'B' '127.0.0.1' 'AccessReopened'
            $fixtureObservations.Add(@{NativeAccess=$reopened})
            $reopenedProof=@($reopened.Results | Where-Object Kind -eq 'Access')
            if($reopenedProof.Count -ne 1 -or -not $reopenedProof[0].Result.NativeAccessVerified -or $reopenedProof[0].Result.NativeElevated){throw 'Reopened native access proof is incomplete.'}
        }
    }
}

function Assert-RestartCreator {
    param($Result,[string]$Phase,[int]$Checks)
    $proof=@($Result.Results | Where-Object Kind -eq 'Restart')
    if($Result.Actor -ne 'A' -or $Result.WindowsSid -ne $actors.A -or $proof.Count -ne 1 -or
        -not $proof[0].Result.NativeRestartVerified -or $proof[0].Result.Phase -ne $Phase -or
        $proof[0].Result.WindowsSid -ne $actors.A -or $proof[0].Result.Passed -ne $Checks){throw 'Native creator restart proof is incomplete.'}
}

function Assert-RestartAccessActor {
    param($Result,[int]$Checks)
    $proof=@($Result.Results | Where-Object Kind -eq 'Access')
    if($Result.Actor -ne 'B' -or $Result.WindowsSid -ne $actors.B -or $proof.Count -ne 1 -or
        -not $proof[0].Result.NativeAccessVerified -or $proof[0].Result.NativeElevated -or
        $proof[0].Result.WindowsSid -ne $actors.B -or $proof[0].Result.Passed -ne $Checks){throw 'Native restart grant/denial proof is incomplete.'}
    $fixtureObservations.Add(@{NativeRestartAccess=$Result})
}

function Invoke-RestartGrant {
    param([ValidateSet('grant-group','revoke')][string]$Phase)
    $owned=@($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Group' -and $_.Name -eq 'FVGate_261003' -and $_.State -eq 'Created'})
    if($owned.Count -ne 1){throw 'Missing owned restart-test group.'}
    $group=Get-LocalGroup -Name $owned[0].Name
    $user=Get-LocalUser -Name 'FVGateB_261003'
    if($group.SID.Value -ne $owned[0].Identity.Sid -or $group.Description -ne $fixtureDescription -or
        $user.SID.Value -ne $actors.B -or $user.Description -ne $fixtureDescription){throw 'Restart-test principal ownership changed.'}
    $members=@(Get-LocalGroupMember -SID $group.SID)
    if($Phase -eq 'grant-group') {
        if($members.Count){throw 'Restart-test group already has members.'}
        $group.SID.Value | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/access-group-sid')
    } else {
        if($members.Count -ne 1 -or $members[0].SID.Value -ne $actors.B){throw 'Restart-test group membership changed.'}
        Assert-RestartAccessActor (Invoke-UserActor 'B' '127.0.0.1' 'AccessGroup') 9
    }
    $native=Invoke-OwnedTool $fixtureDotnet @((Join-Path $fixtureRoot 'runtime/FluxVault.TestHost.dll'),'--mode','windows-access-grant',
        '--configuration',(Join-Path $fixtureRoot 'runtime/database-probe.json'),'--actor','Elevated','--phase',$Phase,'--group-sid',$group.SID.Value)
    if($native.ExitCode -ne 0){throw ('Native restart access change failed: '+$native.Error)}
    $proof=$native.Output | ConvertFrom-Json
    if(-not $proof.NativeAccessVerified -or -not $proof.NativeElevated -or $proof.Phase -ne $Phase -or $proof.Passed -ne 3){throw 'Elevated restart grant proof is incomplete.'}
    $operation=[guid]::Parse($proof.OperationId)
    if($operation -eq [guid]::Empty){throw 'Restart grant receipt is missing.'}
    $operation.ToString('N') | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/access-operation-id')
    $fixtureObservations.Add(@{NativeRestartAccess=$proof})
    Assert-RestartAccessActor (Invoke-UserActor 'B' '127.0.0.1' 'AccessDenied') 2
    if($Phase -eq 'grant-group') {
        Add-LocalGroupMember -SID $group.SID -Member $user
        Assert-RestartAccessActor (Invoke-UserActor 'B' '127.0.0.1' 'AccessGroup') 9
        $creator=Invoke-UserActor 'A' '127.0.0.1' 'RestartBefore'
        Assert-RestartCreator $creator 'before' 8
        $fixtureObservations.Add(@{NativeRestart=$creator})
    }
}

function Assert-ProcessAbsent {
    param($Identity)
    $process=Get-Process -Id $Identity.ProcessId -ErrorAction SilentlyContinue
    if($null -ne $process) {
        try {
            if($process.StartTime.ToUniversalTime().ToString('o') -eq $Identity.StartedUtc){throw 'Previous native server process remains.'}
        } finally {$process.Dispose()}
    }
}

function Read-SystemServerIdentity {
    param([string]$RunId,[string]$ReadyPath)
    $path=Join-Path $fixtureRoot ("output-System/$RunId-processes.jsonl")
    Assert-VaultFixtureTrustedPath $path
    if((Get-Item -LiteralPath $path).Length -gt 65536){throw 'SYSTEM process identity log exceeds its bound.'}
    $identities=@(Get-Content -LiteralPath $path | ForEach-Object {$_ | ConvertFrom-Json -AsHashtable} | Where-Object Executable -eq $fixtureDotnet)
    if($identities.Count -ne 1){throw 'Expected one native service host process.'}
    $identity=$identities[0]
    $identity.StartedUtc=([DateTimeOffset]$identity.StartedUtc).UtcDateTime.ToString('o')
    if($ReadyPath) {
        Assert-VaultFixtureTrustedPath $ReadyPath
        if((Get-Item -LiteralPath $ReadyPath).Length -gt 16384){throw 'Restart readiness exceeds its bound.'}
        $ready=Get-Content -LiteralPath $ReadyPath -Raw | ConvertFrom-Json
        if(-not $ready.NativeRestartServer -or $ready.FixtureId -ne $FixtureId -or $ready.ProcessId -ne $identity.ProcessId -or
            ([DateTimeOffset]$ready.StartedUtc).UtcDateTime.ToString('o') -ne $identity.StartedUtc -or $ready.Executable -ne $identity.Executable){throw 'Replacement readiness/process identity mismatch.'}
        $process=Get-Process -Id $identity.ProcessId
        try {
            $actual=Get-VaultFixtureProcessIdentity $process
            $native=Get-CimInstance Win32_Process -Filter ("ProcessId="+$identity.ProcessId)
            if($actual.StartedUtc -ne $identity.StartedUtc -or $actual.Executable -ne $identity.Executable -or
                (Invoke-CimMethod -InputObject $native -MethodName GetOwnerSid).Sid -ne $actors.System){throw 'Replacement process native identity changed.'}
        } finally {$process.Dispose()}
    }
    return $identity
}

function Invoke-UserActor {
    param([ValidateSet('A','B')][string]$Actor, [string]$HostAddress, [string]$ClientKind)
    $runId=[guid]::NewGuid().ToString('N')
    $name='actor-'+$Actor+'-'+$runId
    $jobName='user-'+$Actor+'-'+$runId
    $kernelName='Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')
    $jobIdentity=@{KernelName=$kernelName;OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
    Add-VaultFixtureIntent $fixtureJournal Job $jobName $jobIdentity
    $actorJob=[FluxVault.Fixtures.OwnedWindowsJob]::CreateSupervisedActor($kernelName,$actors[$Actor])
    $fixtureJobs[$jobName]=$actorJob
    Set-VaultFixtureResourceState $fixtureJournal Job $jobName Created $jobIdentity
    Add-VaultFixtureIntent $fixtureJournal Process $name @{StartedUtc=[DateTime]::UtcNow.ToString('o');Executable=$fixturePwsh}
    $arguments=@('-NoProfile','-NonInteractive','-File',('"'+(Join-Path $fixtureRoot 'runtime/invoke-windows-database-actor.ps1')+'"'),'-Root',('"'+$fixtureRoot+'"'),'-Actor',$Actor,'-RunId',$runId,'-HostAddress',$HostAddress,'-ClientKind',$ClientKind,'-ParentJob',$kernelName)
    $process=$null
    $identity=$null
    try {
        $process=Start-Process -FilePath $fixturePwsh -ArgumentList $arguments -Credential $fixtureCredentials[$Actor] -WorkingDirectory (Join-Path $fixtureRoot 'runtime') -WindowStyle Hidden -PassThru -LoadUserProfile:($ClientKind -like 'Packaged*')
        $identity=Get-VaultFixtureProcessIdentity $process
        Set-VaultFixtureResourceState $fixtureJournal Process $name Created $identity
        $native=Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
        $owner=Invoke-CimMethod -InputObject $native -MethodName GetOwnerSid
        if($owner.ReturnValue -ne 0 -or $owner.Sid -ne $actors[$Actor]){throw 'Actor launcher has the wrong actual Windows token.'}
        $actorJob.Assign($process)
        if($process.Id -notin $actorJob.ProcessIds()){throw 'Parent admission did not contain the actor.'}
        $result=Read-ActorResult $Actor $runId -TimeoutSeconds $(if($ClientKind -like 'Packaged*'){90}else{70})
        if(-not $process.WaitForExit(10000)){throw 'User actor did not exit.'}
        if($process.ExitCode -ne 0){throw 'User actor failed.'}
        foreach($line in Get-Content -LiteralPath (Join-Path $fixtureRoot ("output-$Actor/$runId-processes.jsonl"))) {
            $reported=$line | ConvertFrom-Json -AsHashtable
            $remaining=Get-Process -Id $reported.ProcessId -ErrorAction SilentlyContinue
            if($null -ne $remaining){try{if($remaining.StartTime.ToUniversalTime().ToString('o') -eq ([DateTimeOffset]$reported.StartedUtc).UtcDateTime.ToString('o')){throw 'User actor descendant remains; untrusted output is not authority to stop it.'}}finally{$remaining.Dispose()}}
        }
        foreach($proof in @($result.Results | Where-Object Kind -eq 'Packaged')) {
            $reported=$proof.Result.ProcessIdentity
            if($null -eq $reported){continue}
            $remaining=Get-Process -Id $reported.ProcessId -ErrorAction SilentlyContinue
            if($null -ne $remaining){try{if($remaining.StartTime.ToUniversalTime().ToString('o') -eq ([DateTimeOffset]$reported.StartedUtc).UtcDateTime.ToString('o')){throw 'Packaged apphost remains; actor evidence is not stop authority.'}}finally{$remaining.Dispose()}}
        }
        if($actorJob.ProcessIds().Length){throw 'User actor job contains a surviving child.'}
        $actorJob.StopAndJoin();$actorJob.Dispose();$fixtureJobs.Remove($jobName)
        Set-VaultFixtureResourceState $fixtureJournal Job $jobName Removed $jobIdentity
        return $result
    } finally {
        if($null -ne $process){
            if(-not $process.HasExited){Stop-VaultFixtureProcessTree (Get-VaultFixtureProcessIdentity $process) | Out-Null}
            if(-not $process.WaitForExit(5000)){throw 'User actor launcher remains.'}
            $process.Dispose()
        }
        if($null -ne $identity){Set-VaultFixtureResourceState $fixtureJournal Process $name Removed $identity}
    }
}

function Invoke-CorrelatedActor {
    param([ValidateSet('System','A','B')][string]$Actor, [string]$HostAddress, [string]$ClientKind)
    $logPath=Join-Path $fixtureRoot 'postgres.log'
    $startOffset=(Get-Item -LiteralPath $logPath).Length
    $result=if($Actor -eq 'System'){Invoke-SystemActor $HostAddress $ClientKind}else{Invoke-UserActor $Actor $HostAddress $ClientKind}
    $endOffset=(Get-Item -LiteralPath $logPath).Length
    $probe=@($result.Results | Where-Object {$_.Kind -ne 'ACL'})
    if($probe.Count -ne 1 -or $probe[0].Kind -ne $ClientKind -or $probe[0].Host -ne $HostAddress -or $result.WindowsSid -ne $actors[$Actor]){throw 'Actor returned another probe target or identity.'}
    $result | Add-Member NoteProperty ServerLogStart $startOffset
    $result | Add-Member NoteProperty ServerLogEnd $endOffset
    $fixtureObservations.Add($result)
    return @{Result=$result;Log=(Read-VaultFixtureLog $logPath -StartOffset $startOffset -EndOffset $endOffset)}
}

function Get-TaskHash {
    $bytes=[Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $fixtureTask))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Join-ActorProcesses {
    param([string]$Actor,[string]$RunId)
    # Only the SYSTEM log is a trusted recovery source. A/B launchers are held and checked by their parent.
    if ($Actor -ne 'System') { return }
    $path=Join-Path $fixtureRoot ("output-System/$RunId-processes.jsonl")
    foreach($line in Get-Content -LiteralPath $path) {
        $identity=$line | ConvertFrom-Json -AsHashtable
        $identity.StartedUtc=([DateTimeOffset]$identity.StartedUtc).UtcDateTime.ToString('o')
        $process=Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
        if($null -ne $process) {
            try { if($process.StartTime.ToUniversalTime().ToString('o') -eq $identity.StartedUtc){ Stop-VaultFixtureProcessTree $identity | Out-Null } } finally {$process.Dispose()}
        }
    }
}

function Stop-OwnedJob {
    param($Resource)
    $job=$null
    if($fixtureJobs.ContainsKey($Resource.Name)){$job=$fixtureJobs[$Resource.Name]}
    else {$job=[FluxVault.Fixtures.OwnedWindowsJob]::Open($Resource.Identity.KernelName,$Resource.Identity.OwnerSid)}
    try {if($null -ne $job){$job.StopAndJoin()}}
    finally {if($null -ne $job){$job.Dispose()};$fixtureJobs.Remove($Resource.Name)}
    Set-VaultFixtureResourceState $fixtureJournal Job $Resource.Name Removed $Resource.Identity
}

function Remove-OwnedPackageUsers {
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'PackageUser' -and $_.State -eq 'Created'})) {
        $actor=$resource.Name.Replace('package-','')
        if($actor -notin @('A','B')){throw 'Unknown owned package actor.'}
        $user=Get-LocalUser -Name ("FVGate${actor}_261003") -ErrorAction Stop
        if($user.SID.Value -ne $resource.Identity.Sid -or $user.Description -ne $fixtureDescription){throw 'Package account identity changed.'}
        $actors=@{System='S-1-5-18';A=(Get-LocalUser -Name FVGateA_261003).SID.Value;B=(Get-LocalUser -Name FVGateB_261003).SID.Value}
        if(-not $fixtureCredentials.ContainsKey($actor)) {
            # Recover only the explicitly owned disposable account, never a normal user.
            $password=ConvertTo-SecureString ('aA1!'+[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
            Set-LocalUser -SID $user.SID -Password $password
            $fixtureCredentials[$actor]=[Management.Automation.PSCredential]::new("$env:COMPUTERNAME\$($user.Name)",$password)
        }
        $packageCleanup=Invoke-UserActor $actor '127.0.0.1' 'PackagedCleanup'
        $packageProof=@($packageCleanup.Results | Where-Object Kind -eq 'Packaged')
        if($packageProof.Count -ne 1 -or -not $packageProof[0].Result.PackageRemoved -or -not $packageProof[0].Result.CertificateRemoved){throw 'Owned package or certificate trust remains.'}
        if(@(Get-AppxPackage -User $resource.Identity.Sid -Name $resource.Identity.PackageName).Count){throw 'Owned per-user package remains.'}
        Set-VaultFixtureResourceState $fixtureJournal PackageUser $resource.Name Removed $resource.Identity
    }
    # A surviving B registration must not stop recovery after already-clean A.
    foreach($packageName in @($fixtureJournal.Resources | Where-Object Kind -eq 'PackageUser' | ForEach-Object {$_.Identity.PackageName} | Select-Object -Unique)) {
        if(@(Get-AppxPackage -AllUsers -Name $packageName).Count){throw 'Owned package remains registered or staged.'}
    }
}

function Remove-OwnedPackageProfiles {
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Profile' -and $_.State -eq 'Created'})) {
        $profile=Get-CimInstance Win32_UserProfile -Filter "SID='$($resource.Identity.Sid)'"
        if($profile){if($profile.Loaded -or $profile.LocalPath -ne $resource.Identity.Path){throw 'Owned profile remains loaded or its path changed.'};$profile | Remove-CimInstance}
        if(Get-CimInstance Win32_UserProfile -Filter "SID='$($resource.Identity.Sid)'" -ErrorAction SilentlyContinue){throw 'Owned package profile remains.'}
        if(Test-Path -LiteralPath $resource.Identity.Path){throw 'Owned package profile directory remains.'}
        if(Test-Path -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\'+$resource.Identity.Sid)){throw 'Owned package profile registry entry remains.'}
        Set-VaultFixtureResourceState $fixtureJournal Profile $resource.Name Removed $resource.Identity
    }
}

function Remove-OwnedFixture {
    if ($null -eq $fixtureJournal) { return }
    # Resolve interrupted creation using protected intent plus exact fixture-owned descriptions/paths.
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.State -eq 'Intent'})) {
        $identity=$null
        switch($resource.Kind) {
            Account { $item=Get-LocalUser -Name $resource.Name -ErrorAction SilentlyContinue; if($null -ne $item){if($item.Description -ne $fixtureDescription){throw 'Account intent collision.'};$identity=@{Sid=$item.SID.Value}} }
            Group { $item=Get-LocalGroup -Name $resource.Name -ErrorAction SilentlyContinue; if($null -ne $item){if($item.Description -ne $fixtureDescription){throw 'Group intent collision.'};$identity=@{Sid=$item.SID.Value}} }
            Task { $item=Get-ScheduledTask -TaskName $fixtureTask -ErrorAction SilentlyContinue; if($null -ne $item){if($item.Description -ne $fixtureDescription -or $item.Actions.Execute -ne $fixturePwsh -or -not $item.Actions.Arguments.Contains($fixtureRoot)){throw 'Task intent collision.'};$identity=@{DefinitionSha256=(Get-TaskHash);InstanceId=''}} }
            Postmaster { $script:fixturePort=[int]$resource.Identity.Port; $identity=Get-OwnedPostmaster }
            Job {
                $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open($resource.Identity.KernelName,$resource.Identity.OwnerSid)
                if($null -ne $job){$fixtureJobs[$resource.Name]=$job;$identity=$resource.Identity}
            }
            PackageUser {
                $profile=Get-CimInstance Win32_UserProfile -Filter "SID='$($resource.Identity.Sid)'"
                if($null -ne $profile){$identity=$resource.Identity}
                else {
                    $registered=@(Get-AppxPackage -AllUsers -Name $resource.Identity.PackageName | ForEach-Object {$_.PackageUserInformation} |
                        Where-Object {$_.UserSecurityId.ToString() -eq $resource.Identity.Sid})
                    $trustPath='Registry::HKEY_USERS\'+$resource.Identity.Sid+'\Software\Microsoft\SystemCertificates\TrustedPeople\Certificates\'+$resource.Identity.Thumbprint
                    if($registered.Count -or (Test-Path -LiteralPath $trustPath)){throw 'Owned package or trust has no matching profile; preserve it for review.'}
                }
            }
            Profile {
                $profile=Get-CimInstance Win32_UserProfile -Filter "SID='$($resource.Identity.Sid)'"
                if($null -ne $profile){if($profile.LocalPath -ne $resource.Identity.Path){throw 'Owned package profile path changed.'};$identity=$resource.Identity}
                elseif(Test-Path -LiteralPath $resource.Identity.Path){throw 'A partial profile has no verified SID; preserve it for review.'}
            }
            Process {
                $ownerPath=Join-Path $fixtureRoot ($resource.Name+'-owner.json')
                if(Test-Path -LiteralPath $ownerPath){Assert-VaultFixtureTrustedPath $ownerPath;$identity=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json -AsHashtable;$identity.StartedUtc=([DateTimeOffset]$identity.StartedUtc).UtcDateTime.ToString('o')}
                # Missing publication is resolved only after containment teardown and a quiescent-root check below.
                if($null -eq $identity){continue}
            }
        }
        if($null -eq $identity){ Set-VaultFixtureIntentAbsent $fixtureJournal $resource.Kind $resource.Name }
        else { Set-VaultFixtureResourceState $fixtureJournal $resource.Kind $resource.Name Created $identity }
    }
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Job' -and $_.Name -ne 'Cluster' -and $_.State -eq 'Created'})){Stop-OwnedJob $resource}
    $tasks=@($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Task' -and $_.State -eq 'Created'})
    foreach($resource in $tasks) {
        $registered=Get-ScheduledTask -TaskName $fixtureTask -ErrorAction SilentlyContinue
        if($null -ne $registered){if((Get-TaskHash) -ne $resource.Identity.DefinitionSha256){throw 'Task definition changed before cleanup.'};Stop-ScheduledTask -TaskName $fixtureTask}
        foreach($log in Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'output-System') -Filter '*-processes.jsonl') {
            Join-ActorProcesses System ($log.BaseName.Replace('-processes',''))
        }
        if($null -ne $registered){Unregister-ScheduledTask -TaskName $fixtureTask -Confirm:$false}
        if(Get-ScheduledTask -TaskName $fixtureTask -ErrorAction SilentlyContinue){throw 'Task remains.'}
        Set-VaultFixtureResourceState $fixtureJournal Task $resource.Name Removed $resource.Identity
    }
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Process' -and $_.State -eq 'Created'})) {
        Stop-VaultFixtureProcessTree $resource.Identity | Out-Null
        Set-VaultFixtureResourceState $fixtureJournal Process $resource.Name Removed $resource.Identity
    }
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Postmaster' -and $_.State -eq 'Created'})) {
        $script:fixturePort=[int]$resource.Identity.Port
        $actual=Get-OwnedPostmaster
        $descendants=@()
        if($null -ne $actual) {
            foreach($key in $resource.Identity.Keys){if($actual[$key] -ne $resource.Identity[$key]){throw 'Postmaster ownership changed before shutdown.'}}
            $descendants=@(Get-VaultFixtureProcessTreeIdentity $resource.Identity)
            $stop=Invoke-OwnedTool (Join-Path $fixtureBin 'pg_ctl.exe') @('-D',$fixtureData,'-w','-t','20','stop','-m','fast')
            if($stop.ExitCode -ne 0){throw 'Owned PostgreSQL stop failed.'}
        }
        foreach($identity in $descendants){Stop-VaultFixtureProcess $identity}
        if(Get-OwnedPostmaster){throw 'Postmaster remains.'}
        Set-VaultFixtureResourceState $fixtureJournal Postmaster $resource.Name Removed $resource.Identity
    }
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Job' -and $_.Name -eq 'Cluster' -and $_.State -eq 'Created'})){Stop-OwnedJob $resource}
    # All possible signing tools are joined before provider-managed key recovery.
    Remove-VaultFixtureImportedKeys $fixtureJournal
    Remove-OwnedPackageUsers
    Remove-OwnedPackageProfiles
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Group' -and $_.State -eq 'Created'})) {
        $item=Get-LocalGroup -Name $resource.Name -ErrorAction SilentlyContinue
        if($null -ne $item){if($item.SID.Value -ne $resource.Identity.Sid){throw 'Group SID changed.'};Remove-LocalGroup -SID $item.SID}
        if(Get-LocalGroup -Name $resource.Name -ErrorAction SilentlyContinue){throw 'Group remains.'}
        Set-VaultFixtureResourceState $fixtureJournal Group $resource.Name Removed $resource.Identity
    }
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Account' -and $_.State -eq 'Created'})) {
        $item=Get-LocalUser -Name $resource.Name -ErrorAction SilentlyContinue
        if($null -ne $item){if($item.SID.Value -ne $resource.Identity.Sid){throw 'Account SID changed.'};Disable-LocalUser -SID $item.SID}
        foreach($profile in Get-CimInstance Win32_UserProfile -Filter "SID='$($resource.Identity.Sid)'") {
            if($profile.Loaded){throw 'Owned user profile remains loaded.'}; $profile | Remove-CimInstance
        }
        if($null -ne $item){Remove-LocalUser -SID $item.SID}
        if(Get-LocalUser -Name $resource.Name -ErrorAction SilentlyContinue){throw 'Account remains.'}
        Set-VaultFixtureResourceState $fixtureJournal Account $resource.Name Removed $resource.Identity
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        $left=@(Get-CimInstance Win32_Process | Where-Object {$_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.Contains($fixtureRoot)})
        if(-not $left.Count){break};Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    if($left.Count){throw 'A process still references the fixture root; cleanup refused without terminating unproven ownership.'}
    foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Process' -and $_.State -eq 'Intent'})){Set-VaultFixtureIntentAbsent $fixtureJournal Process $resource.Name}
    if($fixtureJournal.State -ne 'Complete'){Complete-VaultFixtureJournal $fixtureJournal}
    # Published output intentionally excludes the elevated test runner. After all probe
    # accounts/jobs are gone, SYSTEM removes only allowlisted outputs without altering ACLs.
    & (Join-Path $PSScriptRoot 'fixtures/cleanup-windows-fixture-outputs.ps1') -Root $fixtureRoot -FixtureId $FixtureId
    New-Item -ItemType Directory -Path $fixtureEvidence -Force | Out-Null
    Copy-Item -LiteralPath $fixtureJournal.Path -Destination (Join-Path $fixtureEvidence 'completed-owner.json')
    foreach($path in @('postgres.log','before.json','result.json','postgresql-provenance.json','package-sdk-provenance.json','output-cleanup-owner.json','output-cleanup-result.json','output-cleanup-process.json')) { if(Test-Path -LiteralPath (Join-Path $fixtureRoot $path)){Copy-Item -LiteralPath (Join-Path $fixtureRoot $path) -Destination (Join-Path $fixtureEvidence $path)} }
    foreach($pair in @(@{Source='runtime/package-identity.json';Target='package-identity.json'},@{Source='catalogue/package-signing/key-owner.json';Target='signing-key-owner.json'},@{Source='catalogue/package-signing/native-key-cleanup.json';Target='native-key-cleanup.json'})) {
        $source=Join-Path $fixtureRoot $pair.Source
        if(Test-Path -LiteralPath $source){Assert-VaultFixtureTrustedPath $source;Copy-Item -LiteralPath $source -Destination (Join-Path $fixtureEvidence $pair.Target)}
    }
    $callerError=Join-Path $fixtureRoot 'runtime/caller-server-error.json'
    if(Test-Path -LiteralPath $callerError){Assert-VaultFixtureTrustedPath $callerError;Copy-Item -LiteralPath $callerError -Destination (Join-Path $fixtureEvidence 'caller-server-error.json')}
    foreach($diagnostic in Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'output-System') -File -ErrorAction SilentlyContinue | Where-Object {$_.Name -match '^[0-9a-f]{32}-result\.json$'}){
        Assert-VaultFixtureTrustedPath $diagnostic.FullName
        if($diagnostic.Length -gt 1048576){throw 'SYSTEM diagnostic exceeds its evidence bound.'}
        Copy-Item -LiteralPath $diagnostic.FullName -Destination (Join-Path $fixtureEvidence ('system-result-'+$diagnostic.Name))
    }
    # Supervisors persist only redacted exit/output diagnostics, never child environment or passwords.
    foreach($toolResult in Get-ChildItem -LiteralPath $fixtureRoot -File | Where-Object {$_.Name -match '^(tool-[0-9a-f]{32}(-child)?|system-scheduler-[0-9a-f]{32}|system-actor-error)\.json$'}) {
        Assert-VaultFixtureTrustedPath $toolResult.FullName
        Copy-Item -LiteralPath $toolResult.FullName -Destination (Join-Path $fixtureEvidence $toolResult.Name)
    }
    if(Test-Path -LiteralPath (Join-Path $fixtureRoot 'integrity')) {
        foreach($section in @('results','database-intents')) {
            $sectionPath=Join-Path $fixtureRoot ('integrity/'+$section)
            if(-not(Test-Path -LiteralPath $sectionPath)){continue}
            Assert-VaultFixtureTrustedPath $sectionPath
            foreach($entry in Get-ChildItem -LiteralPath $sectionPath -Recurse -Force){Assert-VaultFixtureTrustedPath $entry.FullName}
            Copy-Item -LiteralPath $sectionPath -Destination (Join-Path $fixtureEvidence $section) -Recurse
        }
    }
    Remove-VaultFixtureTree $fixtureRoot $fixtureParent $FixtureId
    $after=Get-InstallationSnapshot
    $after | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $fixtureEvidence 'after.json')
    $unchanged=Test-VaultFixtureInstallationUnchanged $fixtureBefore $after
    @{InstallationUnchanged=$unchanged;RootRemoved=(-not(Test-Path -LiteralPath $fixtureRoot));OwnedJobsJoined=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureEvidence 'cleanup.json')
    if(-not $unchanged){throw 'Original installation changed across fixture execution or teardown.'}
}

if($Mode -eq 'Cleanup') {
    Assert-VaultFixtureTrustedPath $fixtureRoot
    $fixtureJournal=Read-VaultFixtureJournal $fixtureRoot $fixtureParent $FixtureId
    if($fixtureJournal.ContainsKey('RunnerIdentity')){
        $runner=Get-Process -Id $fixtureJournal.RunnerIdentity.ProcessId -ErrorAction SilentlyContinue
        if($null -ne $runner){try{if($runner.StartTime.ToUniversalTime().ToString('o') -eq $fixtureJournal.RunnerIdentity.StartedUtc){throw 'Original runner remains active; recovery refuses concurrent teardown.'}}finally{$runner.Dispose()}}
    }
    $fixtureBefore=Get-Content -LiteralPath (Join-Path $fixtureRoot 'before.json') -Raw | ConvertFrom-Json
    Remove-OwnedFixture
    Write-Output 'Owned fixture resources removed and processes joined.'
    exit 0
}

$fixtureBefore=Get-InstallationSnapshot
foreach($path in @($fixturePwsh,$fixtureDotnet)) { Assert-VaultFixtureTrustedPath $path }
if($RunIntegrityTests){Assert-VaultFixtureTrustedPath $fixtureVstest}
foreach($name in @('FVGateA_261003','FVGateB_261003')){if(Get-LocalUser -Name $name -ErrorAction SilentlyContinue){throw 'Fixture account collision.'}}
if(Get-LocalGroup -Name 'FVGate_261003' -ErrorAction SilentlyContinue){throw 'Fixture group collision.'}
if(Get-ScheduledTask -TaskName $fixtureTask -ErrorAction SilentlyContinue){throw 'Fixture task collision.'}
if(Test-Path -LiteralPath $fixtureRoot){throw 'Fixture root collision.'}
if(Test-Path -LiteralPath $fixtureParent){if(@(Get-ChildItem -LiteralPath $fixtureParent -Directory -Force).Count){throw 'Existing fixture root must be resolved before another run.'}}
foreach($path in @('C:\ProgramData\FluxVault.Tests',$fixtureParent)) {
    if(-not(Test-Path -LiteralPath $path)){New-VaultFixtureProtectedDirectory $path -TraverseUsers}
    Assert-VaultFixtureTrustedPath $path
}
New-VaultFixtureProtectedDirectory $fixtureRoot -TraverseUsers
$fixtureJournal=New-VaultFixtureJournal $fixtureRoot $fixtureParent $FixtureId
$fixtureBefore | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'before.json')
try {
    $retainedManifest=Join-Path $PSScriptRoot '../docs/verification/2026-10-03-next002-windows-fixture/live/4b7d6d4a3f6a46c3ad7fb15f3a2f3e66/postgresql-provenance.json'
    if($FreshReference){$fixtureBin=New-VaultFixturePostgreSqlSnapshot -Root $fixtureRoot -InstalledRoot 'D:\Program Files\PostgreSQL\18'}
    else{
        if((Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\PostgreSQL\Installations\postgresql-x64-18' -Name Version).Version -ne '18.6-4'){throw 'Installed distribution changed from the retained authenticated reference.'}
        $fixtureBin=New-VaultFixtureSnapshotFromManifest $fixtureRoot 'D:\Program Files\PostgreSQL\18' $retainedManifest 'AF6B95D45491EC1203AAB26CCCFEB2244D4755CECB18609454BA352815F6DA18'
    }
    foreach($actor in @('A','B')) {
        $name="FVGate${actor}_261003"
        Add-VaultFixtureIntent $fixtureJournal Account $name
        $password=ConvertTo-SecureString ('aA1!'+[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
        $user=New-LocalUser -Name $name -Password $password -Description $fixtureDescription -AccountNeverExpires
        Set-VaultFixtureResourceState $fixtureJournal Account $name Created @{Sid=$user.SID.Value}
        Add-LocalGroupMember -SID ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')) -Member $user
        $fixtureCredentials[$actor]=[Management.Automation.PSCredential]::new("$env:COMPUTERNAME\$name",$password)
    }
    Add-VaultFixtureIntent $fixtureJournal Group 'FVGate_261003'
    $group=New-LocalGroup -Name 'FVGate_261003' -Description $fixtureDescription
    Set-VaultFixtureResourceState $fixtureJournal Group 'FVGate_261003' Created @{Sid=$group.SID.Value}
    $actors=@{System='S-1-5-18';A=(Get-LocalUser -Name FVGateA_261003).SID.Value;B=(Get-LocalUser -Name FVGateB_261003).SID.Value}
    New-VaultFixtureProtectedDirectory (Join-Path $fixtureRoot 'runtime') -ReadSids @($actors.A,$actors.B)
    New-VaultFixtureProtectedDirectory (Join-Path $fixtureRoot 'catalogue')
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'catalogue/private.bin'),'generated fixture sentinel')
    foreach($actor in @('System','A','B')) { New-VaultFixtureProtectedDirectory (Join-Path $fixtureRoot ("output-$actor")) -ModifySids @($actors[$actor]) }
    $hostOutput=Join-Path $PSScriptRoot '../tests/FluxVault.TestHost/bin/Release/net10.0-windows'
    foreach($entry in Get-ChildItem -LiteralPath $hostOutput -Recurse -Force){if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Host payload contains a link.'}}
    Copy-Item -Path (Join-Path $hostOutput '*') -Destination (Join-Path $fixtureRoot 'runtime') -Recurse
    if($RunIntegrityTests) {
        $integrationOutput=Join-Path $PSScriptRoot '../tests/FluxVault.Integration.Tests/bin/Release/net10.0-windows'
        foreach($entry in Get-ChildItem -LiteralPath $integrationOutput -Recurse -Force){if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Integration payload contains a link.'}}
        New-VaultFixtureProtectedDirectory (Join-Path $fixtureRoot 'runtime/integration')
        Copy-Item -Path (Join-Path $integrationOutput '*') -Destination (Join-Path $fixtureRoot 'runtime/integration') -Recurse
        New-VaultFixtureProtectedDirectory (Join-Path $fixtureRoot 'integrity')
        foreach($section in @('temp','results','database-intents')) {New-VaultFixtureProtectedDirectory (Join-Path $fixtureRoot ('integrity/'+$section))}
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/vault-windows-fixture.psm1'),(Join-Path $PSScriptRoot 'fixtures/invoke-windows-database-actor.ps1'),(Join-Path $PSScriptRoot 'fixtures/invoke-owned-tool.ps1'),(Join-Path $PSScriptRoot 'fixtures/owned-windows-job.cs') -Destination (Join-Path $fixtureRoot 'runtime')
    if($RunPackagedIdentityTests){foreach($helper in @('packaged-identity.psm1','invoke-packaged-identity.ps1','fixture-cng-keys.psm1','fixture-cng-keys.cs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('fixtures/'+$helper)) -Destination (Join-Path $fixtureRoot 'runtime')}}
    foreach($actor in @('Cluster')) {
        $kernelName='Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')
        $jobIdentity=@{KernelName=$kernelName;OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
        Add-VaultFixtureIntent $fixtureJournal Job $actor $jobIdentity
        $actorSid=if($actor -eq 'Cluster'){$jobIdentity.OwnerSid}else{$actors[$actor]}
        $fixtureJobs[$actor]=[FluxVault.Fixtures.OwnedWindowsJob]::Create($kernelName,$actorSid)
        Set-VaultFixtureResourceState $fixtureJournal Job $actor Created $jobIdentity
    }
    $lease=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$lease.Start();$fixturePort=$lease.LocalEndpoint.Port;$lease.Stop()
    if($fixturePort -eq 5432){throw 'Normal PostgreSQL port refused.'}
    @{ FixtureId=$FixtureId;Root=$fixtureRoot;Port=$fixturePort;Database='fv_gate_261003';Role='fv_gate_service';TimeoutSeconds=5;Actors=$actors;RunCatalogueTests=[bool]$RunCatalogueTests;RunMetadataTests=[bool]$RunMetadataTests;RunSingleVaultTests=[bool]$RunSingleVaultTests;RunPackagedIdentityTests=[bool]$RunPackagedIdentityTests;RunRestartTests=[bool]$RunRestartTests } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/database-probe.json')
    @{Dotnet=$fixtureDotnet;Vstest=$fixtureVstest;Psql=(Join-Path $fixtureBin 'psql.exe');WorkingDirectory=(Join-Path $fixtureRoot 'runtime');SafePath=($fixtureBin+';'+(Join-Path $env:SystemRoot 'System32')+';'+$env:SystemRoot);IntegrityTimeoutSeconds=$IntegrityTimeoutSeconds} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/actor-runtime.json')
    if($RunPackagedIdentityTests) {
        $package=New-VaultFixtureIdentityPackage $fixtureJournal $PackageSdkDirectory ${function:Invoke-OwnedTool}.GetNewClosure()
        foreach($actor in @('A','B')) {
            $profilePath=Join-Path ([Environment]::ExpandEnvironmentVariables((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList').ProfilesDirectory)) ("FVGate${actor}_261003")
            if((Get-CimInstance Win32_UserProfile -Filter "SID='$($actors[$actor])'" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $profilePath)){throw 'Package profile collision.'}
            Add-VaultFixtureIntent $fixtureJournal Profile ("profile-$actor") @{Sid=$actors[$actor];Path=$profilePath}
            Add-VaultFixtureIntent $fixtureJournal PackageUser ("package-$actor") @{Sid=$actors[$actor];PackageName=$package.PackageName;Publisher=$package.Publisher;Thumbprint=$package.Thumbprint}
        }
    }
    foreach($entry in Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'runtime') -Recurse -Force){Assert-VaultFixtureTrustedPath $entry.FullName}
    $bootstrap=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $passwordFile=Join-Path $fixtureRoot 'bootstrap.pw'
    [IO.File]::WriteAllText($passwordFile,$bootstrap)
    try { $init=Invoke-OwnedTool (Join-Path $fixtureBin 'initdb.exe') @('-D',$fixtureData,'-U','fv_gate_bootstrap','--auth=scram-sha-256','--encoding=UTF8','--no-locale',('--pwfile='+$passwordFile)); if($init.ExitCode -ne 0){throw 'Owned authenticated initdb failed.'} }
    finally {if(Test-Path -LiteralPath $passwordFile){Remove-Item -LiteralPath $passwordFile}}
    @"
listen_addresses = '127.0.0.1,::1'
port = $fixturePort
shared_buffers = '32MB'
fsync = on
synchronous_commit = on
full_page_writes = on
log_connections = 'all'
log_line_prefix = '[%p] %a '
fluxvault.test_instance = '$FixtureId'
"@ | Add-Content -LiteralPath (Join-Path $fixtureData 'postgresql.conf')
    $bootstrapDatabases=if($RunIntegrityTests){'postgres,fv_gate_trust_control'}else{'postgres'}
    @"
host $bootstrapDatabases fv_gate_bootstrap 127.0.0.1/32 scram-sha-256
host all all 127.0.0.1/32 reject
host all all ::1/128 reject
"@ | Set-Content -LiteralPath (Join-Path $fixtureData 'pg_hba.conf')
    Add-VaultFixtureIntent $fixtureJournal Postmaster 'postgres' @{DataDirectory=$fixtureData;Port=$fixturePort}
    $launch=Invoke-OwnedTool (Join-Path $fixtureBin 'pg_ctl.exe') @('-D',$fixtureData,'-l',(Join-Path $fixtureRoot 'postgres.log'),'-w','-t','30','start') -StartsPostmaster
    $postmaster=Get-OwnedPostmaster
    if($null -ne $postmaster){Set-VaultFixtureResourceState $fixtureJournal Postmaster postgres Created $postmaster}
    if($launch.ExitCode -ne 0 -or $null -eq $postmaster){throw 'Owned PostgreSQL readiness failed.'}
    if($postmaster.ProcessId -notin $fixtureJobs.Cluster.ProcessIds()){throw 'Owned PostgreSQL escaped fixture process containment.'}
    if($RunIntegrityTests) {
        @{InstanceId=$FixtureId;DataDirectory=$fixtureData;Port=$fixturePort;ProcessId=$postmaster.ProcessId;ProcessStartedUtc=$postmaster.StartedUtc} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/integrity-cluster.json')
    }
    try {
        $bootstrapConnection="host=127.0.0.1 port=$fixturePort dbname=postgres user=fv_gate_bootstrap connect_timeout=5 require_auth=scram-sha-256"
        $createDb=if($RunIntegrityTests){'CREATEDB'}else{'NOCREATEDB'}
        $sql="CREATE ROLE fv_gate_service LOGIN NOSUPERUSER $createDb NOCREATEROLE NOREPLICATION;`nCREATE DATABASE fv_gate_261003 OWNER fv_gate_service;`nREVOKE ALL ON DATABASE fv_gate_261003 FROM PUBLIC;`n"
        if($RunIntegrityTests) {
            $sql+="CREATE ROLE fv_gate_trust_control LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;`nCREATE DATABASE fv_gate_trust_control OWNER fv_gate_bootstrap;`nREVOKE ALL ON DATABASE fv_gate_trust_control FROM PUBLIC;`nGRANT CONNECT ON DATABASE fv_gate_trust_control TO fv_gate_trust_control;`n"
            # This separate control contains no repository. It exists only to prove RequireAuth rejects actual trust.
            $sql+='\connect fv_gate_trust_control'+"`nREVOKE CREATE ON SCHEMA public FROM PUBLIC;`n"+'\connect postgres'+"`n"
        }
        $sql+="ALTER ROLE fv_gate_bootstrap NOLOGIN PASSWORD NULL;`nSELECT rolcanlogin, rolpassword IS NULL FROM pg_authid WHERE rolname='fv_gate_bootstrap';`n"
        $provision=Invoke-OwnedTool (Join-Path $fixtureBin 'psql.exe') @('-X','-w','-A','-t','-v','ON_ERROR_STOP=1','--dbname',$bootstrapConnection) $sql @{PGPASSWORD=$bootstrap}
        if($provision.ExitCode -ne 0 -or -not $provision.Output.Contains('f|t')){throw 'Bootstrap-role disabling failed.'}
    } finally {$bootstrap=$null; $sql=$null}
    $integrityAdmission=if($RunIntegrityTests){@"
host "/^fv_test_[0-9a-f]{32}$" fv_gate_service 127.0.0.1/32 sspi map=fv_gate_system include_realm=1
host "/^fv_test_[0-9a-f]{32}$" fv_gate_service ::1/128 sspi map=fv_gate_system include_realm=1
host fv_gate_trust_control fv_gate_trust_control 127.0.0.1/32 trust
host fv_gate_trust_control fv_gate_trust_control ::1/128 trust
"@}else{''}
    @"
host fv_gate_261003 fv_gate_service 127.0.0.1/32 sspi map=fv_gate_system include_realm=1
host fv_gate_261003 fv_gate_service ::1/128 sspi map=fv_gate_system include_realm=1
$integrityAdmission
host all all 127.0.0.1/32 reject
host all all ::1/128 reject
"@ | Set-Content -LiteralPath (Join-Path $fixtureData 'pg_hba.conf')
    '# No principal admitted until observed through the owned SYSTEM probe.' | Set-Content -LiteralPath (Join-Path $fixtureData 'pg_ident.conf')
    $reload=Invoke-OwnedTool (Join-Path $fixtureBin 'pg_ctl.exe') @('-D',$fixtureData,'reload');if($reload.ExitCode -ne 0){throw 'Owned reload failed.'}
    $principals=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($hostAddress in @('127.0.0.1','::1')){foreach($clientKind in @('Npgsql','libpq')){
        $attempt=Invoke-CorrelatedActor System $hostAddress $clientKind
        $probe=@($attempt.Result.Results | Where-Object {$_.Kind -eq $clientKind})[0]
        if(($clientKind -eq 'Npgsql' -and ($probe.Result.Authenticated -or $probe.Result.SqlState -ne '28000')) -or
            ($clientKind -eq 'libpq' -and $probe.ExitCode -eq 0)){throw 'SYSTEM did not complete a denied SSPI mapping handshake.'}
        $null=$principals.Add((Assert-VaultFixtureSspiRefusal $attempt.Log -ExpectedSid $actors.System -HostAddress $hostAddress))
    }}
    if($principals.Count -ne 1){throw 'A single qualified SYSTEM SSPI principal could not be established; no map was added.'}
    $principal=@($principals)[0]
    ('fv_gate_system "'+$principal+'" fv_gate_service') | Set-Content -LiteralPath (Join-Path $fixtureData 'pg_ident.conf')
    $reload=Invoke-OwnedTool (Join-Path $fixtureBin 'pg_ctl.exe') @('-D',$fixtureData,'reload');if($reload.ExitCode -ne 0){throw 'Owned exact-map reload failed.'}
    foreach($hostAddress in @('127.0.0.1','::1')){foreach($clientKind in @('Npgsql','libpq')){
        $attempt=Invoke-CorrelatedActor System $hostAddress $clientKind
        $probe=@($attempt.Result.Results | Where-Object {$_.Kind -eq $clientKind})[0]
        if($clientKind -eq 'Npgsql'){
            if(-not $probe.Result.Authenticated -or -not $probe.Result.FixtureVerified){throw 'SYSTEM password-free Npgsql authentication failed.'}
            if($RunCatalogueTests -and ($null -eq $probe.Result.Catalogue -or $probe.Result.Catalogue.Passed -lt 40 -or
                -not $probe.Result.Catalogue.PolicyActorsAreDoubles -or -not $probe.Result.Catalogue.MirrorDrainCatalogueVerified -or
                -not $probe.Result.Catalogue.DiagnosticsCatalogueVerified -or -not $probe.Result.Catalogue.SelectionCatalogueVerified -or
                -not $probe.Result.Catalogue.ProtectionStateCatalogueVerified)) {
                throw 'Required current catalogue/drain contracts did not complete; rebuild the Release test host before running.'
            }
            # Multi-vault collision/coexistence cases are retired. Retain all single-repository binding/integrity contracts.
            if($RunMetadataTests -and ($null -eq $probe.Result.Catalogue.Metadata -or $probe.Result.Catalogue.Metadata.Passed -lt 18 -or
                -not $probe.Result.Catalogue.Metadata.RecentVersionsVerified -or -not $probe.Result.Catalogue.Metadata.HistoryPagingVerified -or
                -not $probe.Result.Catalogue.Metadata.CurrentProjectionVerified -or -not $probe.Result.Catalogue.Metadata.CurrentPagingVerified -or $probe.Result.Catalogue.StoreHost -ne $hostAddress)){throw 'Required bound metadata, recent/history paging and current projection contracts did not complete on their loopback.'}
            if($RunMetadataTests -and ($null -eq $probe.Result.Catalogue.Repository -or $probe.Result.Catalogue.Repository.Passed -lt 10)){throw 'Required bound repository capture/recovery contracts did not complete.'}
        }elseif($probe.ExitCode -ne 0 -or $probe.Output -ne "$FixtureId|fv_gate_261003|fv_gate_service"){throw 'SYSTEM password-free libpq authentication failed.'}
    }}
    foreach($actor in @('A','B')) {
        foreach($hostAddress in @('127.0.0.1','::1')){foreach($clientKind in @('Npgsql','libpq')){
            $attempt=Invoke-CorrelatedActor $actor $hostAddress $clientKind
            $probe=@($attempt.Result.Results | Where-Object {$_.Kind -eq $clientKind})[0]
            if(($clientKind -eq 'Npgsql' -and ($probe.Result.Authenticated -or $probe.Result.SqlState -ne '28000' -or $probe.Result.WindowsSid -ne $actors[$actor])) -or
                ($clientKind -eq 'libpq' -and $probe.ExitCode -eq 0)){throw 'Ordinary-user SSPI denial not established.'}
            $deniedPrincipal=Assert-VaultFixtureSspiRefusal $attempt.Log -ExpectedSid $actors[$actor] -HostAddress $hostAddress
            if($deniedPrincipal -eq $principal){throw 'Ordinary user authenticated as the service principal.'}
            $acl=@($attempt.Result.Results | Where-Object {$_.Kind -eq 'ACL'})
            if($acl.Count -ne 1 -or $acl[0].ReadProtected -or $acl[0].WriteRuntime){throw 'Ordinary user bypassed the fixture file boundary.'}
        }}
    }
    if ($RunCallerFileTests -or $RunSingleVaultTests) {
        $restartPostmaster=if($RunRestartTests){Get-OwnedPostmaster}else{$null}
        $native = Invoke-SystemActor '127.0.0.1' 'CallerFiles'
        $fixtureObservations.Add(@{NativeCallerFiles=$native})
        $proof=@($native.Results | Where-Object {$_.Kind -eq 'CallerFiles'})
        if($proof.Count -ne 1 -or $proof[0].Result.Passed -lt 8 -or -not $proof[0].Result.NativeCallerTokens){throw 'Native caller file contracts did not complete.'}
        if($RunSingleVaultTests -and (-not $proof[0].Result.SingleVault -or -not $proof[0].Result.ActualCatalogueAndExecutor -or -not $proof[0].Result.CreatorVerified -or -not $proof[0].Result.UngrantDenied)){throw 'Required native single-vault command flow did not complete.'}
        if($RunSingleVaultTests) {
            $creatorProof=@($fixtureObservations | Where-Object { ($_ -is [Collections.IDictionary] -and $_.Contains('NativeCallerFiles') -or
                    $null -ne $_.PSObject.Properties['NativeCallerFiles']) -and $_.NativeCallerFiles.Actor -eq 'A' } |
                ForEach-Object { $_.NativeCallerFiles.Results | Where-Object Kind -eq 'CallerFiles' })
            $deniedProof=@($fixtureObservations | Where-Object { ($_ -is [Collections.IDictionary] -and $_.Contains('NativeCallerFiles') -or
                    $null -ne $_.PSObject.Properties['NativeCallerFiles']) -and $_.NativeCallerFiles.Actor -eq 'B' } |
                ForEach-Object { $_.NativeCallerFiles.Results | Where-Object Kind -eq 'CallerFiles' })
            if($creatorProof.Count -ne 1 -or -not $creatorProof[0].Result.MaintenanceCommandsVerified -or
                -not $creatorProof[0].Result.DrainCommandsVerified -or -not $creatorProof[0].Result.DiagnosticsCommandsVerified -or
                -not $creatorProof[0].Result.SelectionCommandsVerified -or -not $proof[0].Result.SelectionOutputCleaned -or
                -not $creatorProof[0].Result.ProtectionStateCommandsVerified -or -not $creatorProof[0].Result.HistoryPagingVerified -or -not $creatorProof[0].Result.CurrentPagingVerified -or
                -not $creatorProof[0].Result.LocalProtectionDraftVerified -or
                $deniedProof.Count -ne 1 -or $deniedProof[0].Result.MaintenanceDenied -ne 19 -or -not $proof[0].Result.DiagnosticsOutputCleaned -or
                -not $proof[0].Result.ProtectedRehearsalOutputCleaned -or -not $proof[0].Result.DrainEffectVerified) {
                throw 'Required current maintenance proof is missing; rebuild the Release test host before running.'
            }
        }
        if($RunRestartTests) {
            $fixtureOriginalServer=Read-SystemServerIdentity $native.RunId
            Assert-ProcessAbsent $fixtureOriginalServer
            $replacement=Invoke-SystemActor '127.0.0.1' 'RestartServer'
            $restartProof=@($replacement.Results | Where-Object Kind -eq 'Restart')
            if($restartProof.Count -ne 1 -or -not $restartProof[0].Result.NativeRestartServer -or
                -not $restartProof[0].Result.ExistingBootstrapOpened -or -not $restartProof[0].Result.RequestsJoined){throw 'Actual replacement server proof is incomplete.'}
            $newIdentity=Read-SystemServerIdentity $replacement.RunId
            Assert-ProcessAbsent $newIdentity
            $livePostmaster=Get-OwnedPostmaster
            if($null -eq $restartPostmaster -or $null -eq $livePostmaster -or
                $restartPostmaster.ProcessId -ne $livePostmaster.ProcessId -or $restartPostmaster.StartedUtc -ne $livePostmaster.StartedUtc -or
                $restartPostmaster.Executable -ne $livePostmaster.Executable){throw 'Private PostgreSQL changed during service-process replacement.'}
            $fixtureObservations.Add(@{NativeRestartServer=$replacement;PostmasterUnchanged=$true;BothServersJoined=$true})
        }
    }
    if($RunIntegrityTests) {
        $suite=Invoke-SystemActor '127.0.0.1' 'Integrity'
        $fixtureObservations.Add(@{IntegritySuite=$suite})
        $suiteProof=@($suite.Results | Where-Object {$_.Kind -eq 'Integrity'})
        if($suiteProof.Count -ne 1 -or $suiteProof[0].ExitCode -ne 0){throw 'The owned PostgreSQL integrity suite failed.'}
        $trxPath=Join-Path $fixtureRoot 'integrity/results/PostgreSql.trx'
        Assert-VaultFixtureTrustedPath $trxPath
        [xml]$trx=Get-Content -LiteralPath $trxPath -Raw
        $counters=$trx.TestRun.ResultSummary.Counters
        if([int]$counters.total -lt 40 -or [int]$counters.passed -ne [int]$counters.total -or [int]$counters.notExecuted -ne 0){throw 'Required PostgreSQL tests did not all pass without skips.'}
    }
} catch { $fixtureFailure=$_.Exception.Message; $fixtureFailureLocation=$_.ScriptStackTrace }
finally {
    foreach($credential in $fixtureCredentials.Values){$credential.Password.Dispose()};$fixtureCredentials.Clear(); $password=$null; $bootstrap=$null
    try {
        @{FixtureId=$FixtureId;Failure=$fixtureFailure;FailureLocation=$fixtureFailureLocation;Observations=$fixtureObservations.ToArray()} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'result.json')
    } finally { Remove-OwnedFixture }
}
if($null -ne $fixtureFailure){throw $fixtureFailure}
