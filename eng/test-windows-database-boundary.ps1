#Requires -Version 7.2
[CmdletBinding()]
param([ValidateSet('Run','Cleanup')][string]$Mode = 'Run',
    [string]$FixtureId = '43a32654d27a4e8fb0b20012702300af',
    [switch]$FreshReference,
    [switch]$RunCatalogueTests,
    [switch]$RunMetadataTests,
    [switch]$RunCallerFileTests,
    [switch]$RunSingleVaultTests,
    [switch]$RunIntegrityTests,
    [ValidateRange(60,600)][int]$IntegrityTimeoutSeconds = 300,
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot ("../docs/verification/2026-10-03-next002-windows-fixture/live/$FixtureId")))
$ErrorActionPreference = 'Stop'
if($RunMetadataTests -and -not $RunCatalogueTests){throw 'Metadata proof requires the catalogue contracts.'}
if($RunCallerFileTests -and $RunSingleVaultTests){throw 'Select one native file workflow per fresh fixture.'}
if($RunIntegrityTests -and ($RunCallerFileTests -or $RunSingleVaultTests)){throw 'Run the integrity suite in its own fresh fixture.'}
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'fixtures/vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'fixtures/verified-postgresql-snapshot.psm1') -Force
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
    return @{ Services=@($services); Files=@($files) }
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
    $taskSeconds=if($RunIntegrityTests){$IntegrityTimeoutSeconds+30}else{120}
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
            'stop' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/caller-files-stop')
        }
        $result=Read-ActorResult System $runId -TimeoutSeconds $(if($ClientKind -eq 'Integrity'){$IntegrityTimeoutSeconds+10}else{70})
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
        $process=Start-Process -FilePath $fixturePwsh -ArgumentList $arguments -Credential $fixtureCredentials[$Actor] -WorkingDirectory (Join-Path $fixtureRoot 'runtime') -WindowStyle Hidden -PassThru
        $identity=Get-VaultFixtureProcessIdentity $process
        Set-VaultFixtureResourceState $fixtureJournal Process $name Created $identity
        $native=Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
        $owner=Invoke-CimMethod -InputObject $native -MethodName GetOwnerSid
        if($owner.ReturnValue -ne 0 -or $owner.Sid -ne $actors[$Actor]){throw 'Actor launcher has the wrong actual Windows token.'}
        $actorJob.Assign($process)
        if($process.Id -notin $actorJob.ProcessIds()){throw 'Parent admission did not contain the actor.'}
        $result=Read-ActorResult $Actor $runId
        if(-not $process.WaitForExit(10000)){throw 'User actor did not exit.'}
        if($process.ExitCode -ne 0){throw 'User actor failed.'}
        foreach($line in Get-Content -LiteralPath (Join-Path $fixtureRoot ("output-$Actor/$runId-processes.jsonl"))) {
            $reported=$line | ConvertFrom-Json -AsHashtable
            $remaining=Get-Process -Id $reported.ProcessId -ErrorAction SilentlyContinue
            if($null -ne $remaining){try{if($remaining.StartTime.ToUniversalTime().ToString('o') -eq ([DateTimeOffset]$reported.StartedUtc).UtcDateTime.ToString('o')){throw 'User actor descendant remains; untrusted output is not authority to stop it.'}}finally{$remaining.Dispose()}}
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
    foreach($path in @('postgres.log','before.json','result.json','postgresql-provenance.json','output-cleanup-owner.json','output-cleanup-result.json','output-cleanup-process.json')) { if(Test-Path -LiteralPath (Join-Path $fixtureRoot $path)){Copy-Item -LiteralPath (Join-Path $fixtureRoot $path) -Destination (Join-Path $fixtureEvidence $path)} }
    $callerError=Join-Path $fixtureRoot 'runtime/caller-server-error.json'
    if(Test-Path -LiteralPath $callerError){Assert-VaultFixtureTrustedPath $callerError;Copy-Item -LiteralPath $callerError -Destination (Join-Path $fixtureEvidence 'caller-server-error.json')}
    foreach($diagnostic in Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'output-System') -File -ErrorAction SilentlyContinue | Where-Object {$_.Name -match '^[0-9a-f]{32}-result\.json$'}){
        Assert-VaultFixtureTrustedPath $diagnostic.FullName
        if($diagnostic.Length -gt 1048576){throw 'SYSTEM diagnostic exceeds its evidence bound.'}
        Copy-Item -LiteralPath $diagnostic.FullName -Destination (Join-Path $fixtureEvidence ('system-result-'+$diagnostic.Name))
    }
    # Supervisors persist only redacted exit/output diagnostics, never child environment or passwords.
    foreach($toolResult in Get-ChildItem -LiteralPath $fixtureRoot -File | Where-Object {$_.Name -match '^(tool-[0-9a-f]{32}|system-scheduler-[0-9a-f]{32}|system-actor-error)\.json$'}) {
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
    @{ FixtureId=$FixtureId;Root=$fixtureRoot;Port=$fixturePort;Database='fv_gate_261003';Role='fv_gate_service';TimeoutSeconds=5;Actors=$actors;RunCatalogueTests=[bool]$RunCatalogueTests;RunMetadataTests=[bool]$RunMetadataTests;RunSingleVaultTests=[bool]$RunSingleVaultTests } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/database-probe.json')
    @{Dotnet=$fixtureDotnet;Vstest=$fixtureVstest;Psql=(Join-Path $fixtureBin 'psql.exe');WorkingDirectory=(Join-Path $fixtureRoot 'runtime');SafePath=($fixtureBin+';'+(Join-Path $env:SystemRoot 'System32')+';'+$env:SystemRoot);IntegrityTimeoutSeconds=$IntegrityTimeoutSeconds} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/actor-runtime.json')
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
            if($RunCatalogueTests -and ($null -eq $probe.Result.Catalogue -or $probe.Result.Catalogue.Passed -lt 40 -or -not $probe.Result.Catalogue.PolicyActorsAreDoubles)){throw 'Required real catalogue contracts did not complete.'}
            # Multi-vault collision/coexistence cases are retired. Retain all single-repository binding/integrity contracts.
            if($RunMetadataTests -and ($null -eq $probe.Result.Catalogue.Metadata -or $probe.Result.Catalogue.Metadata.Passed -lt 18 -or $probe.Result.Catalogue.StoreHost -ne $hostAddress)){throw 'Required bound metadata contracts did not complete on their loopback.'}
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
        $native = Invoke-SystemActor '127.0.0.1' 'CallerFiles'
        $fixtureObservations.Add(@{NativeCallerFiles=$native})
        $proof=@($native.Results | Where-Object {$_.Kind -eq 'CallerFiles'})
        if($proof.Count -ne 1 -or $proof[0].Result.Passed -lt 8 -or -not $proof[0].Result.NativeCallerTokens){throw 'Native caller file contracts did not complete.'}
        if($RunSingleVaultTests -and (-not $proof[0].Result.SingleVault -or -not $proof[0].Result.ActualCatalogueAndExecutor -or -not $proof[0].Result.CreatorVerified -or -not $proof[0].Result.UngrantDenied)){throw 'Required native single-vault command flow did not complete.'}
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
