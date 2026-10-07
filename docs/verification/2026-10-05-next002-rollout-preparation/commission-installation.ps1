# One fixed staging candidate. Prepare is read-only. Install/Rollback require
# separate operational approval in the conversation; this switch is not approval.
#Requires -Version 7.2
param([ValidateSet('Prepare','Install','Rollback')][string]$Mode='Prepare',
    [switch]$OperationalApprovalRecorded)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$script:commissionSource=$PSScriptRoot
$script:commissionRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))

function Write-CommissionOperatorReceipt {
    param($Context,[string]$Name,$Value)
    $path=Join-Path $Context.WorkRoot ($Name+'.json')
    $stream=[IO.FileStream]::new($path,'CreateNew','Write','None')
    try{$bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 12));$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}

function New-CommissionContext {
    $baseline=Get-Content (Join-Path $script:commissionSource 'staging-preflight-completion.json') -Raw|ConvertFrom-Json -AsHashtable -Depth 12
    $candidate=Get-Content (Join-Path $script:commissionSource 'candidate.json') -Raw|ConvertFrom-Json -AsHashtable -Depth 12
    $auth=Get-Content (Join-Path $script:commissionSource 'authentication/prepared-hashes.json') -Raw|ConvertFrom-Json -AsHashtable
    $id='7871ff7f8d1b404db20771f2e742364f';$root='C:\ProgramData\FluxVault.Commission.'+$id
    $postmasterId=[int]([IO.File]::ReadAllLines('D:\Program Files\PostgreSQL\18\data\postmaster.pid')[0])
    $postmaster=Get-Process -Id $postmasterId
    try{$postmasterIdentity=Get-VaultFixtureProcessIdentity $postmaster}finally{$postmaster.Dispose()}
    if($postmasterIdentity.ProcessId -ne 10660 -or ([DateTimeOffset]$postmasterIdentity.StartedUtc).UtcDateTime -ne ([DateTimeOffset]'2026-10-06T09:31:44.0114872Z').UtcDateTime -or
        $postmasterIdentity.Executable -ine 'D:\Program Files\PostgreSQL\18\bin\postgres.exe'){throw 'Prepared normal postmaster identity changed.'}
    $postmasterIdentity.DataDirectory='D:\Program Files\PostgreSQL\18\data';$postmasterIdentity.Port=5432
    $logEntry=@(Get-Content 'D:\Program Files\PostgreSQL\18\data\current_logfiles'|Where-Object {$_ -like 'stderr *'})
    if($logEntry.Count -ne 1 -or $logEntry[0] -notmatch '^stderr (log/postgresql-[0-9_-]+\.log)$'){throw 'Expected bounded normal PostgreSQL log selection is unavailable.'}
    $prepared=@{}
    foreach($pair in @(@('FinalHba','final-pg_hba.conf'),@('FinalIdent','final-pg_ident.conf'),@('TemporaryHba','temporary-pg_hba.conf'),@('TemporaryIdent','temporary-pg_ident.conf'),@('ProbeIdent','probe-pg_ident.conf'))) {
        $hash=@($auth.Proposed|Where-Object Name -ceq $pair[1])[0].Sha256
        $prepared[$pair[0]]=@{Path=(Join-Path $root $pair[1]);Sha256=$hash}
    }
    $svc=@($candidate.Payload|Where-Object RelativePath -ieq 'service\FluxVault.Service.exe')[0]
    $context=@{NormalInstallation=$true;InstallationId=$id;OperatorSid=$baseline.NativeIntendedCreatorSid;Parent='C:\ProgramData';
        ActiveRoot='C:\ProgramData\FluxVault';RollbackRoot=('C:\ProgramData\FluxVault.Rollback.'+$id);FailedRoot=('C:\ProgramData\FluxVault.Failed.'+$id);
        LegacyConfigSha256='6C52FAFC7325B1C194DE6D4F510441DC6197CB6833A75E9A5570713CD982794F';WorkRoot=$root;LegacySetupPath=(Join-Path $root 'legacy-setup.exe');
        Candidate=$candidate;Baseline=$baseline;InstallerUncertain=$false;
        PsqlPath='D:\Program Files\PostgreSQL\18\bin\psql.exe';PgCtlPath='D:\Program Files\PostgreSQL\18\bin\pg_ctl.exe';
        SqlPath=(Join-Path $root 'create.sql');SqlSha256='DA258197F8308B476084A212BFAC7656BC8049C27383F0B5AB0505565BA743B7';
        ApplicationName=('FluxVault.Commission.'+$id);EmptyPasswordFile=(Join-Path $root 'empty.pgpass');
        Connection='host=127.0.0.1 port=5432 dbname=postgres user=postgres connect_timeout=3 require_auth=sspi';
        LegacyConnection='host=127.0.0.1 port=5432 dbname=fluxvault_metadata user=fluxvault connect_timeout=3 require_auth=none';
        ServiceConnection='host=127.0.0.1 port=5432 dbname=fluxvault_single user=fluxvault_service connect_timeout=3 require_auth=sspi';
        DataDirectory=$postmasterIdentity.DataDirectory;Postmaster=$postmasterIdentity;LogPath=(Join-Path $postmasterIdentity.DataDirectory $Matches[1]);
        Role='postgres';Database='postgres';Port=5432;LegacyRole='fluxvault';ServiceRole='fluxvault_service';ServiceDatabase='fluxvault_single';
        AdminMap=('fv_bootstrap_'+$id);OriginalHashes=$auth.Original;Prepared=$prepared;CleanupRequired=$true;
        WorkerJob=@{KernelName=('Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N'));OwnerSid=$baseline.NativeIntendedCreatorSid};
        SetupJob=@{KernelName=('Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N'));OwnerSid=$baseline.NativeIntendedCreatorSid};
        Setup=@{ServiceExe='C:\Program Files\FluxVault\service\FluxVault.Service.exe';ServiceExeSha256=$svc.Sha256;
            TicketPath=(Join-Path $root 'installation-ticket.template.json');TicketSha256='8DEAAA406C027C361E20C32C90F4C9AFD7ADDA76A72528E4D5E1E2936CA2DDE4'};
        Tasks=[Collections.Generic.List[object]]::new();Jobs=@{}}
    $self=Get-Process -Id $PID
    try{$context.OperatorIdentity=Get-VaultFixtureProcessIdentity $self}finally{$self.Dispose()}
    return $context
}

function Assert-CommissionHash {
    param([string]$Path,[string]$Hash)
    # The two auth files have their accepted daemon writer and still-unapplied
    # ancestor correction. This read-only pin is followed by the scoped guard
    # before any authentication operation, not permission to execute from them.
    if($Path -ieq 'E:\Drive\Work (1)\Code\FluxVault\artifacts\release-integrity-v104\Yagasoft-FluxVault-v1.0.4-win-x64-Setup.exe') {
        # Read/copy this exact pinned input only. Native execution below always
        # applies the strict guard to its protected commissioning copy.
        & (Get-Module vault-windows-fixture) {param($path) Assert-FixtureNoReparse $path} $Path
        if($Hash -cne 'DD4C47DC6B20C0B348691289481965002466ACDF373CEA26E5D823AB9AF84F61'){throw 'Retained legacy installer pin changed.'}
    }elseif($Path -inotmatch '^D:\\Program Files\\PostgreSQL\\18\\data\\pg_(hba|ident)\.conf$'){Assert-VaultFixtureTrustedPath $Path -AdditionalTrustedOwnerSid 'S-1-5-21-136112424-624261118-1239521417-1001'}
    if((Get-FileHash -LiteralPath $Path).Hash -cne $Hash){throw ('Frozen input changed: '+$Path)}
}

function Assert-CommissionCandidate {
    param($Context,[switch]$Installed,[switch]$Legacy)
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{if($identity.User.Value -cne $Context.OperatorSid -or $identity.ImpersonationLevel -ne 'None'){throw 'The intended native creator/operator is required.'}}finally{$identity.Dispose()}
    $auth=Get-Module commission-authentication
    & $auth {param($Context) Assert-CommissionPostmaster $Context} $Context
    $root=if($Installed -or $Legacy){'C:\Program Files\FluxVault'}else{Join-Path $Context.Candidate.Root 'publish'}
    $payload=if($Legacy){$Context.Baseline.InstalledV104Payload}else{$Context.Candidate.Payload}
    foreach($entry in $payload) {
        $relative=if($Legacy){$entry.Path}else{$entry.RelativePath}
        $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
        if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload escapes its fixed root.'}
        Assert-CommissionHash $path $entry.Sha256
    }
    if($Installed) {& (Get-Module commission-setup) {param($Context) Assert-CommissionSetupNativeService $Context} $Context;return}
    if($Legacy) {
        Assert-CommissionHash 'C:\ProgramData\FluxVault\config.json' $Context.LegacyConfigSha256
        foreach($name in $Context.OriginalHashes.Keys){Assert-CommissionHash (Join-Path $Context.DataDirectory $name) $Context.OriginalHashes[$name]}
        Assert-CommissionServicePolicy $Context -Legacy
        return
    }
    if($Context.Candidate.CandidateId -cne '51b103b3c75645dfaa36600e2cbe07a5' -or $Context.Candidate.SourceCommit -cne 'e4736366cd6c47598523e62ffcbb6f71810f61a2' -or $payload.Count -ne 208){throw 'Candidate identity changed.'}
    foreach($entry in $Context.Candidate.Installer){Assert-CommissionHash $entry.Path $entry.Sha256}
    Assert-CommissionHash $Context.Baseline.RollbackSetup.Path 'DD4C47DC6B20C0B348691289481965002466ACDF373CEA26E5D823AB9AF84F61'
    Assert-CommissionHash (Join-Path $script:commissionSource 'installation-ticket.template.json') $Context.Setup.TicketSha256
    Assert-CommissionHash (Join-Path $script:commissionSource 'authentication/create-fluxvault.sql') $Context.SqlSha256
    foreach($value in $Context.Prepared.Values){Assert-CommissionHash (Join-Path $script:commissionSource ('authentication/'+[IO.Path]::GetFileName($value.Path))) $value.Sha256}
    foreach($path in @($Context.WorkRoot,$Context.RollbackRoot,$Context.FailedRoot)) {
        if($path -ieq $Context.WorkRoot -and $Context.ContainsKey('IdentitySha256')) {
            Assert-CommissionHash (Join-Path $Context.WorkRoot 'context.json') $Context.IdentitySha256
        }elseif(Test-Path -LiteralPath $path){throw 'Fresh commissioning/preservation target already exists; no adoption or retry.'}
    }
    foreach($entry in $Context.Baseline.InstalledV104Payload){Assert-CommissionHash (Join-Path 'C:\Program Files\FluxVault' $entry.Path) $entry.Sha256}
    foreach($entry in $Context.Baseline.ExistingFiles){Assert-CommissionHash $entry.Path $entry.Sha256}
    foreach($entry in $Context.Baseline.ExistingServices) {
        $svc=Get-CimInstance Win32_Service -Filter "Name='$($entry.Name)'"
        if($svc.State -ne 'Running' -or $svc.ProcessId -ne $entry.Identity.ProcessId -or $svc.StartName -ine $entry.StartName -or $svc.PathName -ine $entry.PathName){throw 'Normal service baseline changed.'}
    }
    Assert-CommissionServicePolicy $Context -Legacy
    if(@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ilike 'C:\Program Files\FluxVault\*' -and $_.ExecutablePath -ine $Context.Setup.ServiceExe}).Count){throw 'Close installed FluxVault clients before installation.'}
    $proposal=Get-Content (Join-Path $script:commissionSource 'postgresql-ancestor-acl-proposal.json') -Raw|ConvertFrom-Json
    foreach($entry in $proposal){if((Get-Acl $entry.Path).Sddl -cne $entry.OriginalSddl){throw 'Reviewed ancestor baseline changed.'}}
}

function Assert-CommissionServicePolicy {
    param($Context,[switch]$Legacy)
    $path='Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\FluxVaultService'
    if($Legacy) {
        $key=Get-Item $path
        try{foreach($entry in $Context.Baseline.ExistingServicePolicy) {
            $value=$key.GetValue($entry.Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if(($value|ConvertTo-Json -Compress) -cne ($entry.Value|ConvertTo-Json -Compress) -or $key.GetValueKind($entry.Name).ToString() -cne $entry.Kind){throw 'Legacy service policy differs from the checked rollback baseline.'}
        }}finally{$key.Dispose()}
        if((Get-Service FluxVaultService).Status -ne 'Running'){throw 'Restored legacy service is not running.'}
    }else {
        $service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
        if($service.State -ne 'Stopped' -or $service.StartName -cne 'LocalSystem' -or $service.StartMode -ne 'Manual' -or $service.PathName -ine '"C:\Program Files\FluxVault\service\FluxVault.Service.exe"'){throw 'Exact stopped/demand LocalSystem service is required.'}
    }
}

function Invoke-CommissionServiceAction {
    param($Context,[ValidateSet('Stop','Activate')][string]$Action)
    $service=Get-Service FluxVaultService -ErrorAction SilentlyContinue
    if($null -eq $service){if($Action -eq 'Stop'){return};throw 'Service registration is missing.'}
    if($Action -eq 'Stop') {
        # Disable recovery and automatic starts before any data/authentication move.
        $null=Invoke-CommissionNativeTool $Context 'C:\Windows\System32\sc.exe' @('config','FluxVaultService','start=','demand') 15
        $null=Invoke-CommissionNativeTool $Context 'C:\Windows\System32\sc.exe' @('failure','FluxVaultService','reset=','0','actions=','') 15
        Stop-Service FluxVaultService
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
        Assert-CommissionServicePolicy $Context
    }else {
        Assert-CommissionServicePolicy $Context
        $null=Invoke-CommissionNativeTool $Context 'C:\Windows\System32\sc.exe' @('failure','FluxVaultService','reset=','86400','actions=','restart/60000/restart/60000/none/0') 15
        $null=Invoke-CommissionNativeTool $Context 'C:\Windows\System32\sc.exe' @('config','FluxVaultService','start=','delayed-auto') 15
        Start-Service FluxVaultService
        $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    }
    $service.Dispose()
}

function Invoke-CommissionNativeTool {
    param($Context,[string]$Executable,[string[]]$Arguments,[int]$TimeoutSeconds=120)
    Assert-VaultFixtureTrustedPath $Executable -AdditionalTrustedOwnerSid $Context.OperatorSid
    $reply=& (Get-Module commission-authentication) {param($c,$e,$a,$t) Invoke-CommissionTool $c $e $a $null -TimeoutSeconds $t} $Context $Executable $Arguments $TimeoutSeconds
    if($reply.ExitCode -ne 0 -or -not $reply.Joined){throw 'Native tool failed; retain inputs and keep runtime stopped.'}
    return $reply
}

function Invoke-CommissionInstaller {
    param($Context,[ValidateSet('Install','Uninstall','LegacyInstall')][string]$Phase)
    Assert-CommissionInstallerSettled $Context
    $entry=if($Phase -eq 'LegacyInstall'){@{Path=$Context.LegacySetupPath;Sha256=$Context.Baseline.RollbackSetup.Sha256}}else{@($Context.Candidate.Installer|Where-Object Path -like '*.exe')[0]}
    Assert-CommissionHash $entry.Path $entry.Sha256
    Write-CommissionOperatorReceipt $Context ('installer-'+$Phase+'-intent') @{ContextSha256=$Context.IdentitySha256;Path=$entry.Path;Sha256=$entry.Sha256;Phase=$Phase}
    $arguments=@('/quiet','/norestart','/log',(Join-Path $Context.WorkRoot ('installer-'+$Phase+'.log')))
    if($Phase -eq 'Uninstall'){$arguments=@('/uninstall')+$arguments}
    try {$reply=Invoke-CommissionNativeTool $Context $entry.Path $arguments 120}
    catch {
        # Burn/MSI may have transferred work to the Windows Installer service.
        # A killed/joined launcher is not proof its external transaction ended.
        $Context.InstallerUncertain=$true
        Write-CommissionOperatorReceipt $Context 'installer-uncertain' @{Phase=$Phase;Error=$_.Exception.Message;AutomaticRetryAllowed=$false}
        throw
    }
    $reply.ContextSha256=$Context.IdentitySha256
    Write-CommissionOperatorReceipt $Context ('installer-'+$Phase+'-completed') $reply
}

function Assert-CommissionInstallerSettled {
    param($Context)
    if($Context.InstallerUncertain -or (Test-Path (Join-Path $Context.WorkRoot 'installer-uncertain.json'))){throw 'Installer transaction is unresolved; reconcile before further installation or rollback.'}
    foreach($phase in @('Install','Uninstall','LegacyInstall')) {
        $intent=Test-Path (Join-Path $Context.WorkRoot ('installer-'+$phase+'-intent.json'))
        $completed=Test-Path (Join-Path $Context.WorkRoot ('installer-'+$phase+'-completed.json'))
        if($intent -ne $completed){throw 'Installer intent has no confirmed completion; its external transaction must be reconciled.'}
        if($intent) {
            $null=Read-CommissionOperatorReceipt $Context ('installer-'+$phase+'-intent')
            $receipt=Read-CommissionOperatorReceipt $Context ('installer-'+$phase+'-completed')
            if(-not $receipt.Joined -or $receipt.ExitCode -ne 0){throw 'Installer completion is unconfirmed.'}
        }
    }
}

function Set-CommissionAncestorState {
    param($Context,[ValidateSet('Apply','Restore')][string]$Phase)
    $proposal=Get-Content (Join-Path $Context.WorkRoot 'postgresql-ancestor-acl-proposal.json') -Raw|ConvertFrom-Json
    [string[]]$paths=@($proposal|ForEach-Object Path)
    [string[]]$before=@($proposal|ForEach-Object {if($Phase -eq 'Apply'){$_.OriginalSddl}else{$_.ProposedSddl}})
    [string[]]$after=@($proposal|ForEach-Object {if($Phase -eq 'Apply'){$_.ProposedSddl}else{$_.OriginalSddl}})
    if($Phase -eq 'Restore' -and @($paths|Where-Object {(Get-Acl $_).Sddl -cne $after[[Array]::IndexOf($paths,$_)]}).Count -eq 0){return}
    [FluxVault.Commissioning.PostgreSqlAncestorAcl]::Replace($paths,$before,$after)
    foreach($path in $paths){if((Get-Acl $path).Sddl -cne $after[[Array]::IndexOf($paths,$path)]){throw 'Ancestor security transition is unconfirmed.'}}
}

function Start-CommissionTask {
    param($Context,[ValidateSet('cleanup','worker','setup','restore')][string]$Kind)
    $name='FV-NEXT002-'+$Context.InstallationId+'-'+$Kind
    if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Task name collision; no existing task is adopted.'}
    $scriptName=if($Kind -in @('setup','restore')){'commission-setup-worker.ps1'}else{'commission-authentication-'+$Kind+'.ps1'}
    $args='-NoProfile -NonInteractive -File "'+(Join-Path $Context.WorkRoot $scriptName)+'" -ContextPath "'+(Join-Path $Context.WorkRoot 'context.json')+'" -ContextSha256 '+$Context.IdentitySha256
    if($Kind -eq 'restore'){$args+=' -RestoreAuthentication'}
    $action=New-ScheduledTaskAction -Execute 'C:\Program Files\PowerShell\7\pwsh.exe' -Argument $args
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 180) -MultipleInstances IgnoreNew
    $task=@{Name=$name;ContextSha256=$Context.IdentitySha256;Execute=$action.Execute;Arguments=$args;Description=('FluxVault exact staging commissioning '+$Context.IdentitySha256)}
    $Context.Tasks.Add($task)
    Write-CommissionOperatorReceipt $Context ('task-'+$Kind+'-intent') $task
    Register-ScheduledTask -TaskName $name -Action $action -Settings $settings -User SYSTEM -RunLevel Highest -Description $task.Description|Out-Null
    $task.Hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $name))))
    Write-CommissionOperatorReceipt $Context ('task-'+$Kind+'-registered') $task
    # Retain the COM instance GUID so teardown checks the owned invocation.
    $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\');$registered=$folder.GetTask($name)
    try{$instance=$registered.Run($null);try{$task.InstanceId=$instance.InstanceGuid}finally{$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($instance)}}
    finally{foreach($ref in @($registered,$folder,$scheduler)){$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($ref)}}
    Write-CommissionOperatorReceipt $Context ('task-'+$Kind+'-started') $task
    return $task
}

function Wait-CommissionTask {
    param($Context,$Task,[int]$Seconds=140,[switch]$AllowWorkerTermination)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $scheduled=Get-ScheduledTask -TaskName $Task.Name
        if($scheduled.State -ne 'Running' -and $scheduled.State -ne 'Queued'){break}
        if($timer.Elapsed.TotalSeconds -ge $Seconds){throw 'SYSTEM task deadline exceeded; activation is blocked.'}
        Start-Sleep -Milliseconds 100
    }while($true)
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $Task.Name))))
    if($hash -cne $Task.Hash){throw 'Owned task definition changed.'}
    if(-not $AllowWorkerTermination -and (Get-ScheduledTaskInfo -TaskName $Task.Name).LastTaskResult -ne 0){throw 'SYSTEM task failed after receipt publication; activation is blocked.'}
}

function Read-CommissionOperatorReceipt {
    param($Context,[string]$Name)
    $path=Join-Path $Context.WorkRoot ($Name+'.json')
    Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid 'S-1-5-21-136112424-624261118-1239521417-1001'
    if((Get-Item $path).Length -gt 65536){throw 'Receipt exceeds its bound.'}
    $value=Get-Content $path -Raw|ConvertFrom-Json -AsHashtable -Depth 12
    if($value.ContextSha256 -cne $Context.IdentitySha256){throw 'Receipt context changed.'}
    return $value
}

function Wait-CommissionReadiness {
    param($Context,[string]$Name,[int]$Seconds=25)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path (Join-Path $Context.WorkRoot ($Name+'.json')))) {
        foreach($errorName in @('worker-error','cleanup-error','setup-error','cleanup-failed')){if(Test-Path (Join-Path $Context.WorkRoot ($errorName+'.json'))){throw 'Commissioning actor failed before readiness.'}}
        if($timer.Elapsed.TotalSeconds -ge $Seconds){throw 'Actor readiness deadline exceeded.'}
        Start-Sleep -Milliseconds 100
    }
    $ready=Read-CommissionOperatorReceipt $Context $Name
    $process=Get-CimInstance Win32_Process -Filter "ProcessId=$($ready.Process.ProcessId)"
    if($null -eq $process -or $process.ExecutablePath -ine 'C:\Program Files\PowerShell\7\pwsh.exe' -or
        $process.CreationDate.ToUniversalTime() -ne ([DateTimeOffset]$ready.Process.StartedUtc).UtcDateTime -or
        (Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid).Sid -ne 'S-1-5-18'){throw 'Readiness is not the bound native SYSTEM process.'}
    return $ready
}

function Invoke-CommissionAuthenticationTasks {
    param($Context)
    $cleanup=Start-CommissionTask $Context cleanup
    $null=Wait-CommissionReadiness $Context 'cleanup-ready'
    $worker=Start-CommissionTask $Context worker
    Wait-CommissionTask $Context $cleanup
    Wait-CommissionTask $Context $worker -AllowWorkerTermination
    if(Test-Path (Join-Path $Context.WorkRoot 'cleanup-failed.json')){throw 'Independent retirement failed.'}
    $receipt=Read-CommissionOperatorReceipt $Context 'cleanup-completed'
    if(-not $receipt.CanActivate -or -not $receipt.FinalRetained -or -not $receipt.AdmissionRetired -or -not $receipt.OwnedJobJoined -or $Context.Jobs.Auth.ProcessIds().Count){throw 'Authentication retirement and joining are unconfirmed.'}
    return $receipt
}

function Start-CommissionSetupTask {
    param($Context)
    Assert-CommissionCandidate $Context -Installed
    $task=Start-CommissionTask $Context setup
    $ready=Wait-CommissionReadiness $Context 'setup-ready'
    if($ready.TicketSha256 -cne $Context.Setup.TicketSha256){throw 'Ready setup ticket changed.'}
    return $task
}

function Invoke-CommissionCreatorConfirmation {
    param($Context)
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{if($identity.User.Value -cne $Context.OperatorSid -or $identity.ImpersonationLevel -ne 'None'){throw 'Native creator confirmation is required.'}}finally{$identity.Dispose()}
    $reply=Invoke-CommissionNativeTool $Context 'C:\Program Files\FluxVault\cli\FluxVault.Cli.exe' @('setup-confirm','--instance',$Context.InstallationId,'--timeout-seconds','120') 120
    $expected='Provisioning confirmed. Installation: 7871ff7f8d1b404db20771f2e742364f. Vault: 67539278-9292-4870-93cd-a011419228dc. Revision: 1.'
    if($reply.Output.Trim() -cne $expected -or $reply.Error.Trim()){throw 'Creator acknowledgement is uncertain; reconcile, never retry setup automatically.'}
    return @{Confirmed=$true;Process=$reply.Process;Joined=$reply.Joined;Output=$reply.Output;ContextSha256=$Context.IdentitySha256}
}

function Complete-CommissionSetupTask {
    param($Context,$Task)
    Wait-CommissionTask $Context $Task
    if($Context.Jobs.Setup.ProcessIds().Count){throw 'Setup job retains processes.'}
    $receipt=Read-CommissionOperatorReceipt $Context 'setup-completed'
    if(-not $receipt.BootstrapVerified -or -not $receipt.SetupHost.Joined -or $receipt.SetupHost.ExitCode -ne 0 -or
        $receipt.CanActivate -or $receipt.InstanceId -cne $Context.InstallationId -or $receipt.VaultId -cne '675392789292487093cda011419228dc' -or
        $receipt.CreatorSid -cne $Context.OperatorSid -or $receipt.TicketSha256 -cne $Context.Setup.TicketSha256){throw 'Published setup binding/host lifetime is unconfirmed.'}
    return $receipt
}

function Assert-CommissionActivation {
    param($Context,$Creator,$Setup)
    if(-not $Creator.Confirmed -or -not $Creator.Joined -or -not $Setup.BootstrapVerified){throw 'Creator/setup confirmation is incomplete.'}
    if(Test-Path (Join-Path $Context.WorkRoot 'cleanup-failed.json')){throw 'Watchdog failure prevents activation.'}
    $null=Read-CommissionOperatorReceipt $Context 'cleanup-completed'
    Assert-CommissionCandidate $Context -Installed
    Assert-CommissionHash 'C:\ProgramData\FluxVault\installation.json' $Setup.BootstrapSha256
    foreach($key in @('FinalHba','FinalIdent')) {
        $path=Join-Path $Context.DataDirectory $(if($key -eq 'FinalHba'){'pg_hba.conf'}else{'pg_ident.conf'})
        & (Get-Module commission-authentication) {param($Context,$Path) Assert-CommissionAuthenticationPath $Context $Path} $Context $path
        Assert-CommissionHash $path $Context.Prepared[$key].Sha256
    }
    Write-CommissionOperatorReceipt $Context 'creator-confirmed' $Creator
}

function Stop-CommissionActors {
    param($Context)
    foreach($job in $Context.Jobs.Values){$job.StopAndJoin()}
    # Let independent authentication retirement finish before stopping its task.
    foreach($task in $Context.Tasks) {
        if(-not(Get-ScheduledTask -TaskName $task.Name -ErrorAction SilentlyContinue)){continue}
        Assert-CommissionTaskIntent $Context $task
        Wait-CommissionTask $Context $task -AllowWorkerTermination
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $task.Name))))
        if($hash -cne $task.Hash){throw 'Changed task cannot be removed.'}
        Unregister-ScheduledTask -TaskName $task.Name -Confirm:$false
        if(Get-ScheduledTask -TaskName $task.Name -ErrorAction SilentlyContinue){throw 'Owned commissioning task remains.'}
    }
    foreach($job in $Context.Jobs.Values){if($job.ProcessIds().Count){throw 'Owned actor remains.'};$job.Dispose()}
    $Context.Jobs=@{};$Context.Tasks.Clear()
}

function Assert-CommissionTaskIntent {
    param($Context,$Intent)
    $task=Get-ScheduledTask -TaskName $Intent.Name -ErrorAction Stop
    if($Intent.ContextSha256 -cne $Context.IdentitySha256 -or $task.Description -cne $Intent.Description -or
        @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine $Intent.Execute -or $task.Actions[0].Arguments -cne $Intent.Arguments -or
        $task.Actions[0].WorkingDirectory -or @($task.Triggers|Where-Object {$null -ne $_}).Count -ne 0 -or
        $task.Principal.UserId -notin @('SYSTEM','S-1-5-18') -or $task.Principal.RunLevel -ne 'Highest' -or $task.Principal.LogonType -ne 'ServiceAccount' -or
        $task.Settings.ExecutionTimeLimit -ne 'PT3M' -or $task.Settings.MultipleInstances -ne 'IgnoreNew'){throw 'Task does not match its exact pre-registration intent; preserve it.'}
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask $Intent.Name))))
    if($Intent.ContainsKey('Hash') -and $Intent.Hash -cne $hash){throw 'Registered task changed; preserve it.'}
    $Intent.Hash=$hash
}

function Read-CommissionRecoveryTasks {
    param($Context)
    foreach($kind in @('cleanup','worker','setup','restore')) {
        $path=Join-Path $Context.WorkRoot ('task-'+$kind+'-intent.json')
        if(-not(Test-Path $path)){continue}
        $intent=Read-CommissionOperatorReceipt $Context ('task-'+$kind+'-intent')
        if($intent.Name -cne ('FV-NEXT002-'+$Context.InstallationId+'-'+$kind)){throw 'Task recovery name changed.'}
        if(-not(Get-ScheduledTask -TaskName $intent.Name -ErrorAction SilentlyContinue)){continue}
        $registered=Join-Path $Context.WorkRoot ('task-'+$kind+'-registered.json')
        if(Test-Path $registered){$record=Read-CommissionOperatorReceipt $Context ('task-'+$kind+'-registered');$intent.Hash=$record.Hash}
        Assert-CommissionTaskIntent $Context $intent
        $Context.Tasks.Add($intent)
    }
}

function Invoke-CommissionInstallation {
    param($Context)
    Assert-CommissionCandidate $Context
    try {
        Invoke-CommissionServiceAction $Context Stop
        Move-CommissionLegacyState $Context Preserve|Out-Null
        Invoke-CommissionInstaller $Context Install
        Assert-CommissionCandidate $Context -Installed
        Set-CommissionAncestorState $Context Apply
        $null=Invoke-CommissionAuthenticationTasks $Context
        $task=Start-CommissionSetupTask $Context
        $creator=Invoke-CommissionCreatorConfirmation $Context
        $setup=Complete-CommissionSetupTask $Context $task
        Assert-CommissionActivation $Context $creator $setup
        Invoke-CommissionServiceAction $Context Activate
        Write-CommissionOperatorReceipt $Context 'installation-completed' @{ContextSha256=$Context.IdentitySha256;Creator=$creator;Setup=$setup;LiveWorkflowAccepted=$false}
    }catch {
        Invoke-CommissionServiceAction $Context Stop
        throw
    }finally{Stop-CommissionActors $Context}
}

function Assert-CommissionRollbackInputs {
    param($Context)
    Assert-CommissionInstallerSettled $Context
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {if($identity.User.Value -cne 'S-1-5-21-136112424-624261118-1239521417-1001' -or $identity.ImpersonationLevel -ne 'None' -or
        -not $Context.NormalInstallation -or $Context.OperatorSid -cne $identity.User.Value -or $Context.InstallationId -cne '7871ff7f8d1b404db20771f2e742364f' -or
        $Context.WorkRoot -ine 'C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f'){throw 'Rollback requires the exact intended native operator and target.'}}
    finally{$identity.Dispose()}
    Assert-CommissionHash $Context.LegacySetupPath $Context.Baseline.RollbackSetup.Sha256
    & (Get-Module commission-authentication) {param($Context) Assert-CommissionPostmaster $Context} $Context
    if(-not(Test-Path $Context.RollbackRoot)){throw 'Preserved legacy root is missing; rollback is blocked.'}
    Assert-CommissionHash (Join-Path $Context.RollbackRoot 'config.json') $Context.LegacyConfigSha256
}

function Restore-CommissionOriginalAuthentication {
    param($Context)
    if(-not $Context.Jobs.ContainsKey('Setup')){$Context.Jobs.Setup=[FluxVault.Fixtures.OwnedWindowsJob]::Create($Context.SetupJob.KernelName,'S-1-5-18')}
    $task=Start-CommissionTask $Context restore
    try {
        Wait-CommissionTask $Context $task
        $receipt=Read-CommissionOperatorReceipt $Context 'rollback-authentication'
        if(-not $receipt.Restored -or -not $receipt.AdministratorRetired){throw 'Authentication rollback unconfirmed.'}
    }finally{Stop-CommissionActors $Context}
}

function Invoke-CommissionRollback {
    param($Context)
    Assert-CommissionRollbackInputs $Context
    try {
        Stop-CommissionActors $Context
        Invoke-CommissionServiceAction $Context Stop
        Invoke-CommissionInstaller $Context Uninstall
        Move-CommissionLegacyState $Context Restore|Out-Null
        Restore-CommissionOriginalAuthentication $Context
        Set-CommissionAncestorState $Context Restore
        Invoke-CommissionInstaller $Context LegacyInstall
        Assert-CommissionCandidate $Context -Legacy
        Write-CommissionOperatorReceipt $Context 'rollback-completed' @{ContextSha256=$Context.IdentitySha256;LegacyRestored=$true;DatabasesRetained=$true;FreshStateRetained=(Test-Path $Context.FailedRoot)}
    }catch {Invoke-CommissionServiceAction $Context Stop;throw}
}

function Initialize-CommissionWork {
    param($Context)
    New-VaultFixtureProtectedDirectory $Context.WorkRoot
    Copy-Item -LiteralPath $Context.Baseline.RollbackSetup.Path -Destination $Context.LegacySetupPath
    Assert-CommissionHash $Context.LegacySetupPath $Context.Baseline.RollbackSetup.Sha256
    foreach($name in @('commission-administrator.psm1','commission-authentication.psm1','commission-authentication-worker.ps1','commission-authentication-cleanup.ps1','commission-setup.psm1','commission-setup-worker.ps1','postgresql-ancestor-acl.cs','postgresql-ancestor-acl-proposal.json','installation-ticket.template.json')) {
        $path=Join-Path $script:commissionSource $name;Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid 'S-1-5-21-136112424-624261118-1239521417-1001'
        Copy-Item -LiteralPath $path -Destination (Join-Path $Context.WorkRoot $name)
    }
    foreach($name in @('vault-windows-fixture.psm1','owned-windows-job.cs')){Copy-Item -LiteralPath (Join-Path $script:commissionRepo ('eng/fixtures/'+$name)) -Destination (Join-Path $Context.WorkRoot $name)}
    foreach($value in $Context.Prepared.Values){Copy-Item -LiteralPath (Join-Path $script:commissionSource ('authentication/'+[IO.Path]::GetFileName($value.Path))) -Destination $value.Path}
    Copy-Item -LiteralPath (Join-Path $script:commissionSource 'authentication/create-fluxvault.sql') -Destination $Context.SqlPath
    [IO.File]::WriteAllText($Context.EmptyPasswordFile,'')
    $Context.Jobs.Auth=[FluxVault.Fixtures.OwnedWindowsJob]::Create($Context.WorkerJob.KernelName,'S-1-5-18')
    $Context.Jobs.Setup=[FluxVault.Fixtures.OwnedWindowsJob]::Create($Context.SetupJob.KernelName,'S-1-5-18')
    # Keep the worker context within its existing 64 KiB limit. Full payload/
    # legacy snapshots are parent recovery inputs, separately pinned in it.
    $Context.ParentInputHashes=@{}
    foreach($key in @('Candidate','Baseline')) {
        Write-CommissionOperatorReceipt $Context ('parent-'+$key) $Context[$key]
        $Context.ParentInputHashes[$key]=(Get-FileHash (Join-Path $Context.WorkRoot ('parent-'+$key+'.json'))).Hash
    }
    $frozen=@{};foreach($key in $Context.Keys){if($key -notin @('Jobs','Tasks','Candidate','Baseline')){$frozen[$key]=$Context[$key]}}
    $frozen|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $Context.WorkRoot 'context.json')
    $Context.IdentitySha256=(Get-FileHash (Join-Path $Context.WorkRoot 'context.json')).Hash
}

if($MyInvocation.InvocationName -ne '.') {
    Import-Module (Join-Path $script:commissionRepo 'eng/fixtures/vault-windows-fixture.psm1') -Force
    Import-VaultFixtureJobType
    Import-Module (Join-Path $PSScriptRoot 'commission-authentication.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'commission-setup.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'commission-rollback.psm1') -Force
    Add-Type -Path (Join-Path $PSScriptRoot 'postgresql-ancestor-acl.cs')
    if($Mode -eq 'Rollback') {
        if(-not $OperationalApprovalRecorded){throw 'Separate rollout/recovery approval must be recorded before effects.'}
        $path='C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f\context.json'
        Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid 'S-1-5-21-136112424-624261118-1239521417-1001'
        $context=Get-Content $path -Raw|ConvertFrom-Json -AsHashtable -Depth 12
        foreach($key in @('Candidate','Baseline')) {
            $inputPath=Join-Path $context.WorkRoot ('parent-'+$key+'.json')
            Assert-CommissionHash $inputPath $context.ParentInputHashes[$key]
            $context[$key]=Get-Content $inputPath -Raw|ConvertFrom-Json -AsHashtable -Depth 12
        }
        $context.IdentitySha256=(Get-FileHash $path).Hash;$context.Jobs=@{};$context.Tasks=[Collections.Generic.List[object]]::new()
        foreach($entry in @(@('Auth','WorkerJob'),@('Setup','SetupJob'))){$job=[FluxVault.Fixtures.OwnedWindowsJob]::Open($context[$entry[1]].KernelName,$context.OperatorSid);if($null -ne $job){$context.Jobs[$entry[0]]=$job}}
        Read-CommissionRecoveryTasks $context
        Invoke-CommissionRollback $context
    }else {
        $context=New-CommissionContext
        Assert-CommissionCandidate $context
        if($Mode -eq 'Prepare') {@{ReadOnly=$true;CandidateId=$context.Candidate.CandidateId;PayloadHashesMatched=208;LegacyHashesMatched=204;TicketSha256=$context.Setup.TicketSha256;Postmaster=$context.Postmaster;RolloutApproved=$false;WorkRoot=$context.WorkRoot}|ConvertTo-Json}
        else {
            if(-not $OperationalApprovalRecorded){throw 'Separate rollout approval must be recorded before effects.'}
            try{Initialize-CommissionWork $context;Invoke-CommissionInstallation $context}
            finally{Stop-CommissionActors $context}
        }
    }
}
