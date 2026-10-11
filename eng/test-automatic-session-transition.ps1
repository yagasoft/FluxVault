#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Prepare','Rehearse','Start','Collect','Worker')][string]$Mode,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$RunId,
    [ValidateRange(60,600)][int]$TimeoutSeconds=600)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$parent='C:\ProgramData\FluxVault.Tests\NEXT005Session'
$root=Join-Path $parent $RunId
$taskName='FluxVault-NEXT005-Session-'+$RunId
$payload=Join-Path $root 'payload'
$requestPath=Join-Path $root 'request.json'
$fixtureRoot=Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $RunId
$isSystem=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18'
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole('Administrators')){throw 'An authorised elevated staging operator is required.'}
Import-Module (Join-Path $PSScriptRoot 'fixtures/vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$root=Resolve-VaultFixtureRoot $root $parent $RunId
function New-SystemDirectory([string]$Path){
    New-VaultFixtureProtectedDirectory $Path
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false);$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Get-TaskDigest {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Export-ScheduledTask -TaskName $taskName))))
}
function Get-ObserverBounds([int]$ObservationSeconds){
    $primaryTool=$ObservationSeconds+240
    $primaryWait=$primaryTool+20
    $recoveryTool=90;$recoveryWait=$recoveryTool+10
    $joinGrace=30 # Per invocation: bounded tree termination, native exit and job drain.
    $finalisationGrace=60
    return @{PrimaryTool=$primaryTool;PrimaryWait=$primaryWait;RecoveryTool=$recoveryTool;RecoveryWait=$recoveryWait;
        JoinGrace=$joinGrace;FinalisationGrace=$finalisationGrace;
        Scheduler=($primaryWait+$joinGrace+$recoveryWait+$joinGrace+$finalisationGrace)}
}
function Get-ObserverTaskIdentity {
    $executable='C:\Program Files\PowerShell\7\pwsh.exe'
    return @{Executable=$executable;Arguments=('-NoProfile -NonInteractive -File "'+(Join-Path $payload 'eng/test-automatic-session-transition.ps1')+'" -Mode Worker -RunId '+$RunId);
        Description=('Owned FluxVault native session observer '+$RunId);DefinitionSha256=$null}
}
function Remove-OwnedObserverTask($Identity,[switch]$RequireDefinitionPin){
    $registered=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if($null -eq $registered){return}
    if($RequireDefinitionPin -and $Identity.DefinitionSha256 -cnotmatch '^[0-9A-F]{64}$'){throw 'Observer definition pin unavailable; task removal refused.'}
    if($registered.Actions.Execute -cne $Identity.Executable -or $registered.Actions.Arguments -cne $Identity.Arguments -or
        $registered.Description -cne $Identity.Description -or $registered.Principal.UserId -notin @('SYSTEM','S-1-5-18') -or
        ($Identity.DefinitionSha256 -and (Get-TaskDigest) -cne $Identity.DefinitionSha256)){throw 'Observer task ownership changed; removal refused.'}
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Owned observer task remains.'}
}
function Set-ObserverSystemOwner([string]$Path){
    $acl=Get-Acl -LiteralPath $Path;$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'));Set-Acl -LiteralPath $Path -AclObject $acl
}
function Read-VerifiedObserverDescriptor([string]$Path,[string]$Expected){
    $actual=(Get-Acl -LiteralPath $Path -Audit).GetSecurityDescriptorSddlForm('All')
    $normalised=[Security.AccessControl.RawSecurityDescriptor]::new($Expected)
    if($actual -ceq $normalised.GetSddlForm('All')){return $actual}
    # Windows can mark even protected children as inheritance-processed during Set-Acl.
    # Permit only this added marker; preserve every other field, flag and ACE.
    $normalised.SetFlags($normalised.ControlFlags -bor [Security.AccessControl.ControlFlags]::DiscretionaryAclAutoInherited)
    if($actual -cne $normalised.GetSddlForm('All')){throw 'Observer descriptor differs beyond the Windows-added inheritance marker.'}
    return $actual
}
function Initialize-ObserverParents {
    $operatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if($isSystem -or $operatorSid -notin @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object {$_.SID.Value})){
        throw 'Observer preparation requires a direct local administrator.'
    }
    $base='C:\ProgramData\FluxVault.Tests';$nested=Join-Path $base 'NEXT002'
    $parents=@();$children=@();$expected=@{}
    foreach($path in @($base,$nested)){
        Assert-VaultFixtureTrustedPath $path
        $acl=Get-Acl -LiteralPath $path -Audit
        if(-not $acl.AreAccessRulesProtected){throw 'Observer fixture parent has an unexpected inheriting descriptor.'}
        $before=$acl.GetSecurityDescriptorSddlForm('All')
        foreach($rule in @($acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier]))){
            if($rule.IdentityReference.Value -eq $operatorSid -and $rule.AccessControlType -eq 'Allow'){
                if($rule.IsInherited -or $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
                    $rule.InheritanceFlags -ne ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit) -or
                    $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None){throw 'Unexpected personal observer parent grant; change refused.'}
                $acl.RemoveAccessRuleSpecific($rule)
            }
        }
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
        $expected[$path]=$acl
        $parents+=@{Path=$path;BeforeSddl=$before;ExpectedSddl=$acl.GetSecurityDescriptorSddlForm('All')}
    }
    foreach($child in Get-ChildItem -LiteralPath $base -Force){
        if(-not $child.PSIsContainer -or $child.Name -notin @('NEXT002','NEXT005','NEXT005Session') -or
            ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected observer parent child; change refused.'}
        $acl=Get-Acl -LiteralPath $child.FullName -Audit
        if(-not $acl.AreAccessRulesProtected){throw 'Inheritance-enabled observer parent child; change refused.'}
        $children+=@{Path=$child.FullName;BeforeSddl=$acl.GetSecurityDescriptorSddlForm('All')}
    }
    if(@(Get-ChildItem -LiteralPath $nested -Force).Count){throw 'Native fixture parent is not empty; change refused.'}
    $backup=Join-Path $root 'parent-acls-before.json'
    @{OperatorSid=$operatorSid;Parents=$parents;Children=$children} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $backup
    Set-ObserverSystemOwner $backup
    foreach($entry in $parents){Set-Acl -LiteralPath $entry.Path -AclObject $expected[$entry.Path]}
    foreach($entry in $parents){
        $entry.ActualSddl=Read-VerifiedObserverDescriptor $entry.Path $entry.ExpectedSddl
        Assert-VaultFixtureTrustedPath $entry.Path
    }
    foreach($entry in $children){
        $target=if($expected.ContainsKey($entry.Path)){$expected[$entry.Path].GetSecurityDescriptorSddlForm('All')}else{$entry.BeforeSddl}
        $entry.ExpectedSddl=$target
        $entry.ActualSddl=Read-VerifiedObserverDescriptor $entry.Path $target
    }
    $receipt=@{Parents=$parents;Children=$children;OtherChildSecurityUnchanged=$true;WindowsAutoInheritanceMarkerMayBeAdded=$true;CompletedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'parent-acls-after.json')
    Set-ObserverSystemOwner (Join-Path $root 'parent-acls-after.json')
    return $receipt
}
function Invoke-ObserverStart($Request){
    if($isSystem){throw 'The worker cannot recursively start an observer.'}
    if((Test-Path -LiteralPath (Join-Path $root 'worker.json')) -or (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)){throw 'Observer already started or task collision.'}
    foreach($entry in $Request.InstalledAssemblies){if((Get-FileHash -LiteralPath $entry.Path).Hash -cne $entry.Sha256){throw 'Installed product changed after observer preparation.'}}
    $identity=Get-ObserverTaskIdentity
    Assert-VaultFixtureTrustedPath $identity.Executable
    $bounds=Get-ObserverBounds $Request.TimeoutSeconds
    $action=New-ScheduledTaskAction -Execute $identity.Executable -Argument $identity.Arguments
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds $bounds.Scheduler) -MultipleInstances IgnoreNew
    $created=$false
    try{
        Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User SYSTEM -RunLevel Highest -Description $identity.Description | Out-Null
        $created=$true
        $identity.DefinitionSha256=Get-TaskDigest
        $identity.RequestSha256=(Get-FileHash -LiteralPath $requestPath).Hash
        $identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'task.json')
        Set-ObserverSystemOwner (Join-Path $root 'task.json')
        Start-ScheduledTask -TaskName $taskName
    }catch{
        $failure=$_
        if($created){
            $registered=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if($null -ne $registered -and $registered.State -in @('Running','Queued')){
                # A failed start acknowledgement may already have dispatched SYSTEM.
                # Let its independently owned finally block clean up; never kill its supervisor.
                'Abort requested after an uncertain start acknowledgement.' | Set-Content -LiteralPath (Join-Path $root 'abort')
            }else{Remove-OwnedObserverTask $identity}
        }
        $failure.Exception.Message | Set-Content -LiteralPath (Join-Path $root 'start-failure.txt')
        throw $failure
    }
}
function Read-Request {
    Assert-VaultFixtureTrustedPath $requestPath
    $request=Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
    if($request.RunId -cne $RunId -or $request.Root -cne $root -or $request.FixtureRoot -cne $fixtureRoot -or
        $request.CreatorSid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$' -or $request.Transition -notin @('AbortRehearsal','ObserveSignOut') -or
        $request.TimeoutSeconds -lt 60 -or $request.TimeoutSeconds -gt 600){throw 'Native observer request identity changed.'}
    foreach($entry in $request.Payload){
        $path=[IO.Path]::GetFullPath((Join-Path $payload $entry.RelativePath))
        if(-not $path.StartsWith($payload+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Observer payload escapes its owned root.'}
        Assert-VaultFixtureTrustedPath $path
        if((Get-FileHash -LiteralPath $path).Hash -cne $entry.Sha256){throw 'Native observer payload changed.'}
    }
    return $request
}
function Invoke-ObserverWorker {
    if(-not $isSystem){throw 'Native observer worker requires LocalSystem.'}
    $identity=Get-ObserverTaskIdentity
    $task=$null;$request=$null;$bounds=$null
    $failure=$null;$recovery=$null;$joined=$false;$workerIdentity=$null
    function Invoke-OwnedHarness([switch]$Cleanup){
        $job=[FluxVault.Fixtures.OwnedWindowsJob]::Create(('Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')),'S-1-5-18')
        $child=$null
        try{
            $arguments=@('-NoProfile','-NonInteractive','-File',(Join-Path $payload 'eng/test-windows-database-boundary.ps1'),'-FixtureId',$RunId,'-EvidenceDirectory',(Join-Path $root 'evidence'))
            if($Cleanup){$arguments+=@('-Mode','Cleanup')}else{$arguments+=@('-RunAutomaticTests','-AutomaticSessionTransition',$request.Transition,'-AutomaticCreatorSid',$request.CreatorSid,'-SessionTransitionTimeoutSeconds',[string]$request.TimeoutSeconds)}
            $toolRequest=Join-Path $root $(if($Cleanup){'cleanup-tool.json'}else{'harness-tool.json'})
            $result=Join-Path $root $(if($Cleanup){'cleanup-result.json'}else{'harness-result.json'})
            @{Executable=$task.Executable;Arguments=$arguments;Job=$job.Name;TimeoutSeconds=$(if($Cleanup){$bounds.RecoveryTool}else{$bounds.PrimaryTool});
                StartsPostmaster=$false;InputText=$null;OwnerPath=$toolRequest.Replace('.json','-owner.json');ResultPath=$result} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $toolRequest
            $start=[Diagnostics.ProcessStartInfo]::new($task.Executable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
            foreach($argument in @('-NoProfile','-NonInteractive','-File',(Join-Path $payload 'eng/fixtures/invoke-owned-tool.ps1'),'-Request',$toolRequest)){$start.ArgumentList.Add($argument)}
            $child=[Diagnostics.Process]::Start($start)
            $identity=Get-VaultFixtureLiveProcessIdentity $child
            $identity | ConvertTo-Json | Set-Content -LiteralPath $toolRequest.Replace('.json','-launcher.json')
            $waiting=[Diagnostics.Stopwatch]::StartNew();$limit=if($Cleanup){$bounds.RecoveryWait}else{$bounds.PrimaryWait}
            while(-not $child.WaitForExit(100)){
                if($waiting.Elapsed.TotalSeconds -gt $limit){throw 'SYSTEM fixture supervisor exceeded its finite deadline.'}
                if(-not $Cleanup -and (Test-Path -LiteralPath (Join-Path $root 'abort')) -and (Test-Path -LiteralPath (Join-Path $fixtureRoot 'runtime'))){
                    'Owned observer abort.' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'runtime/native-session-abort')
                }
            }
            if($child.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $result)){throw 'SYSTEM fixture launcher failed.'}
            $receipt=Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
            if($receipt.ExitCode -ne 0){throw ('Owned native fixture failed: '+$receipt.Error)}
        }finally{
            try{
                if($null -ne $child){if(-not $child.HasExited){Stop-VaultFixtureProcessTree (Get-VaultFixtureProcessIdentity $child) | Out-Null};if(-not $child.WaitForExit(5000)){throw 'Owned fixture launcher remains.'};$child.Dispose()}
            }finally{try{$job.StopAndJoin()}finally{$job.Dispose()}}
        }
    }
    try {
        $self=Get-Process -Id $PID
        try {$workerIdentity=Get-VaultFixtureProcessIdentity $self}finally{$self.Dispose()}
        Assert-VaultFixtureTrustedPath (Join-Path $root 'task.json')
        $task=Get-Content -LiteralPath (Join-Path $root 'task.json') -Raw | ConvertFrom-Json
        # Cleanup authority must survive validation and subsequent identity IO failures.
        $identity.DefinitionSha256=$task.DefinitionSha256
        $workerIdentity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'worker.json')
        if((Get-TaskDigest) -cne $task.DefinitionSha256 -or $task.Executable -cne $identity.Executable -or $task.Arguments -cne $identity.Arguments){throw 'Observer task pin changed.'}
        $request=Read-Request
        if((Get-FileHash -LiteralPath $requestPath).Hash -cne $task.RequestSha256){throw 'Observer request pin changed.'}
        $bounds=Get-ObserverBounds $request.TimeoutSeconds
        Invoke-OwnedHarness;$joined=$true
    }
    catch{
        $failure=$_.Exception.Message
        if($null -ne $request -and (Test-Path -LiteralPath $fixtureRoot)){try{Invoke-OwnedHarness -Cleanup;$recovery='Joined owned fixture recovery completed.';$joined=$true}catch{$recovery=$_.Exception.Message}}
    }
    finally{
        $cleanupFailure=$null
        try{Remove-OwnedObserverTask $identity -RequireDefinitionPin}catch{$cleanupFailure=$_.Exception.Message}
        try{
            @{RunId=$RunId;Transition=$(if($null -ne $request){$request.Transition}else{'Unvalidated'});Failure=$failure;Recovery=$recovery;OwnedHarnessJoined=$joined;
                WorkerProcess=$workerIdentity;CleanupFailure=$cleanupFailure;
                FixtureRootRemoved=(-not(Test-Path -LiteralPath $fixtureRoot));TaskRemoved=(-not(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue));
                CompletedUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'result.json')
        }catch{$_ | Out-String | Set-Content -LiteralPath (Join-Path $root 'worker-failure.txt');throw}
        if($cleanupFailure){throw $cleanupFailure}
    }
    if($failure){throw $failure}
    return
}
if($MyInvocation.InvocationName -eq '.'){return}
if($Mode -in @('Prepare','Rehearse')){
    if($isSystem){throw 'Preparation must record the actual desktop creator.'}
    if(Test-Path -LiteralPath $root){throw 'Observer root collision.'}
    if(-not(Test-Path -LiteralPath $parent)){New-SystemDirectory $parent}
    Assert-VaultFixtureTrustedPath $parent
    New-SystemDirectory $root
    Initialize-ObserverParents | Out-Null
    $repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $directories=@('eng/fixtures','tests/FluxVault.TestHost/bin/Release/net10.0-windows',
        'docs/verification/2026-10-03-next002-windows-fixture/live/4b7d6d4a3f6a46c3ad7fb15f3a2f3e66')
    foreach($directory in $directories){New-Item -ItemType Directory -Path (Join-Path $payload $directory) -Force | Out-Null}
    foreach($file in @(Get-ChildItem (Join-Path $repository 'eng/fixtures') -File)){
        Assert-VaultFixtureTrustedPath $file.FullName
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload 'eng/fixtures')
    }
    foreach($name in @('test-windows-database-boundary.ps1','test-automatic-session-transition.ps1')){
        Assert-VaultFixtureTrustedPath (Join-Path $PSScriptRoot $name)
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $payload 'eng')
    }
    $hostOutput='tests/FluxVault.TestHost/bin/Release/net10.0-windows'
    foreach($file in Get-ChildItem (Join-Path $repository $hostOutput) -Recurse -File){
        Assert-VaultFixtureTrustedPath $file.FullName
        $relative=[IO.Path]::GetRelativePath((Join-Path $repository $hostOutput),$file.FullName)
        $destination=Join-Path (Join-Path $payload $hostOutput) $relative
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    # TestHost changes only the observer. Exercise the exact installed product assemblies.
    $installedAssemblies=@(foreach($name in @('FluxVault.Abstractions.dll','FluxVault.Core.dll','FluxVault.Windows.dll','FluxVault.App.dll')){
        $source=Join-Path 'C:\Program Files\FluxVault\app' $name
        Assert-VaultFixtureTrustedPath $source
        Copy-Item -LiteralPath $source -Destination (Join-Path $payload ($hostOutput+'/'+$name)) -Force
        @{Path=$source;Sha256=(Get-FileHash -LiteralPath $source).Hash}
    })
    $reference='docs/verification/2026-10-03-next002-windows-fixture/live/4b7d6d4a3f6a46c3ad7fb15f3a2f3e66/postgresql-provenance.json'
    Copy-Item -LiteralPath (Join-Path $repository $reference) -Destination (Join-Path $payload $reference)
    $systemAcl=[Security.AccessControl.DirectorySecurity]::new()
    $systemAcl.SetAccessRuleProtection($true,$false);$systemAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$systemAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $root -AclObject $systemAcl
    foreach($item in Get-ChildItem -LiteralPath $root -Recurse -Force){
        $acl=Get-Acl -LiteralPath $item.FullName
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
    $inventory=@(foreach($file in Get-ChildItem -LiteralPath $payload -Recurse -File){
        @{RelativePath=[IO.Path]::GetRelativePath($payload,$file.FullName);Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
    })
    $request=@{RunId=$RunId;Root=$root;FixtureRoot=$fixtureRoot;CreatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
        Transition=$(if($Mode -eq 'Rehearse'){'AbortRehearsal'}else{'ObserveSignOut'});TimeoutSeconds=$TimeoutSeconds;
        Payload=$inventory;InstalledAssemblies=$installedAssemblies;PreparedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    $request | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $requestPath
    $acl=Get-Acl -LiteralPath $requestPath;$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'));Set-Acl -LiteralPath $requestPath -AclObject $acl
    if($Mode -eq 'Prepare'){
        @{RunId=$RunId;Root=$root;Prepared=$true;Started=$false;RequestSha256=(Get-FileHash -LiteralPath $requestPath).Hash} | ConvertTo-Json
        return
    }
}
if($Mode -in @('Start','Rehearse')){
    $request=Read-Request
    Invoke-ObserverStart $request
    @{RunId=$RunId;Root=$root;Transition=$request.Transition;Started=$true;Task=$taskName;WindowsLogoffInitiated=$false} | ConvertTo-Json
    return
}
if($Mode -eq 'Worker'){Invoke-ObserverWorker;return}
if($Mode -eq 'Collect'){
    $request=Read-Request
    $result=Get-Content -LiteralPath (Join-Path $root 'result.json') -Raw | ConvertFrom-Json
    $identity=$result.WorkerProcess
    if(Test-Path -LiteralPath (Join-Path $root 'worker.json')){
        Assert-VaultFixtureTrustedPath (Join-Path $root 'worker.json')
        $identity=Get-Content -LiteralPath (Join-Path $root 'worker.json') -Raw | ConvertFrom-Json
    }
    if($null -eq $identity){throw 'Observer worker identity was not persisted; preserve the failure record.'}
    $worker=Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
    if($null -ne $worker){
        try{
            if($worker.StartTime.ToUniversalTime().ToString('o') -cne ([DateTimeOffset]$identity.StartedUtc).UtcDateTime.ToString('o') -or
                [FluxVault.Fixtures.WindowsProcessIdentity]::ImagePath($worker) -cne $identity.Executable){throw 'Observer PID identity changed.'}
            if(-not $worker.WaitForExit(1000)){throw 'Observer is still running; collection does not interrupt it.'}
        }finally{$worker.Dispose()}
    }
    if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Observer task remains.'}
    @{WorkerJoined=$true;Result=$result;RetainedRoot=$root} | ConvertTo-Json -Depth 5
    if($result.Failure -or -not $result.OwnedHarnessJoined -or -not $result.FixtureRootRemoved -or -not $result.TaskRemoved){throw 'Native observer did not meet its owned cleanup gates.'}
}
