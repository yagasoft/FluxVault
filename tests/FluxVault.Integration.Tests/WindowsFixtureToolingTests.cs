using System.Diagnostics;
using System.Text.Json;

namespace FluxVault.Integration.Tests;

// Current-user tooling checks. These do not claim the real A/B/SYSTEM acceptance gates.
public sealed class WindowsFixtureToolingTests
{
    [Theory]
    [InlineData("cim-precision", true)]
    [InlineData("wrong-native-lifetime", false)]
    [InlineData("wrong-owner", false)]
    [InlineData("wrong-image", false)]
    [InlineData("exited-during-owner", false)]
    public async Task Commission_readiness_uses_the_held_native_lifetime_and_refuses_changed_identity(string scenario, bool accepted)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));. (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/commission-installation.ps1')
            $context=@{WorkRoot=$root;IdentitySha256=('A'*64)};$script:scenario='SCENARIO_VALUE'
            $child=Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 60') -WindowStyle Hidden -PassThru
            try {
                $script:child=$child;$native=Get-VaultFixtureProcessIdentity $child;$script:metadata=$native.Clone()
                $receipt=$native.Clone()
                if($script:scenario -eq 'wrong-native-lifetime'){$receipt.StartedUtc=([DateTimeOffset]$receipt.StartedUtc).AddTicks(1).ToString('o')}
                # CIM is only an owner/image boundary in this focused proof. Its timestamp
                # is deliberately different; the actual held child supplies native identity.
                $script:cimTime=([DateTimeOffset]$receipt.StartedUtc).UtcDateTime
                if($script:scenario -eq 'cim-precision'){$script:cimTime=$script:cimTime.AddTicks(-5)}
                function Get-CimInstance {param($ClassName,$Filter) [pscustomobject]@{ExecutablePath=$(if($script:scenario -eq 'wrong-image'){'wrong.exe'}else{$script:metadata.Executable});CreationDate=$script:cimTime}}
                function Invoke-CimMethod {param($InputObject,$MethodName)
                    if($script:scenario -eq 'exited-during-owner'){$script:child.Kill($true);if(-not $script:child.WaitForExit(5000)){throw 'Owned readiness child did not exit'}}
                    [pscustomobject]@{ReturnValue=0;Sid=$(if($script:scenario -eq 'wrong-owner'){'S-1-5-19'}else{'S-1-5-18'})}
                }
                Write-CommissionOperatorReceipt $context 'cleanup-ready' @{ContextSha256=$context.IdentitySha256;Process=$receipt}
                $failure=$null;try{$null=Wait-CommissionReadiness $context 'cleanup-ready'}catch{$failure=$_.Exception.Message}
            } finally {
                if(-not $child.HasExited){$child.Kill($true)}
                $joined=$child.WaitForExit(5000);$child.Dispose()
                if(-not $joined){throw 'Owned readiness child did not join'}
            }
            @{Failure=$failure;Joined=$joined}|ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal), simulateCommissionMembership: true);
        Assert.True(result.GetProperty("Joined").GetBoolean());
        if (accepted) Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
        else Assert.Contains("bound native SYSTEM process", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("intent-only", false)]
    [InlineData("after-context", false)]
    [InlineData("after-receipts", false)]
    [InlineData("before-completion", false)]
    [InlineData("changed-archive", false)]
    public async Task Pre_admission_reset_preserves_failed_evidence_and_blocks_interrupted_recovery(string scenario, bool accepted)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));. (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/commission-installation.ps1')
            $value=@{WorkRoot=$root;OperatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
                OperatorIdentity=@{ProcessId=123;StartedUtc='2026-01-01T00:00:00Z';Executable='original-operator'};
                InstallationId=[guid]::NewGuid().ToString('N');Targets=@('unchanged-repository','unchanged-database');InstallerUncertain=$false;WorkerJob=@{KernelName='unchanged-job'}}
            $value|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'context.json')
            $context=Get-Content (Join-Path $root 'context.json') -Raw|ConvertFrom-Json -AsHashtable
            $context.IdentitySha256=(Get-FileHash (Join-Path $root 'context.json')).Hash
            Write-CommissionOperatorReceipt $context 'installer-Install-intent' @{ContextSha256=$context.IdentitySha256;Phase='Install';Path='already-completed-installer';Sha256=('B'*64)}
            Write-CommissionOperatorReceipt $context 'installer-Install-completed' @{ContextSha256=$context.IdentitySha256;ExitCode=0;Joined=$true;Process=@{ProcessId=456;StartedUtc='2026-01-01T00:00:01Z';Executable='already-completed-installer'}}
            $names=@('context.json','installer-Install-intent.json','installer-Install-completed.json');$originalHashes=@{}
            foreach($name in $names){$originalHashes[$name]=(Get-FileHash (Join-Path $root $name)).Hash}
            Publish-CommissionAclHandoff $context
            $errorText='An unaccounted administrator session survives; preserve unrelated sessions and keep runtime blocked.'
            Write-CommissionOperatorReceipt $context 'cleanup-failed' @{ContextSha256=$context.IdentitySha256;CanActivate=$false;Error=$errorText}
            Write-CommissionOperatorReceipt $context 'cleanup-error' @{Error=$errorText}
            Write-CommissionOperatorReceipt $context 'cleanup-ready' @{ContextSha256=$context.IdentitySha256;WindowsSid='S-1-5-18';Job=$context.WorkerJob.KernelName;Process=@{ProcessId=789}}
            foreach($phase in @('intent','registered','started')){Write-CommissionOperatorReceipt $context ('task-cleanup-'+$phase) @{ContextSha256=$context.IdentitySha256;Name=('FV-NEXT002-'+$context.InstallationId+'-cleanup')}}
            [IO.File]::WriteAllText((Join-Path $root 'commission-processes.jsonl'),'original joined read-only probes')
            $failedHash=(Get-FileHash (Join-Path $root 'cleanup-failed.json')).Hash
            Publish-CommissionPreAdmissionReset $context
            $archive=Join-Path $root 'pre-admission-reset'
            $restoredExact=@($names|Where-Object {(Get-FileHash (Join-Path $root $_)).Hash -cne $originalHashes[$_]}).Count -eq 0
            $preserved=(Get-FileHash (Join-Path $archive 'cleanup-failed.json')).Hash -ceq $failedHash
            $replayRefused=$false;try{Publish-CommissionPreAdmissionReset $context}catch{$replayRefused=$_.Exception.Message -like '*cannot be replayed*'}
            $scenario='SCENARIO_VALUE'
            if($scenario -in @('intent-only','after-context','after-receipts','before-completion')){
                Remove-Item -LiteralPath (Join-Path $archive 'completed.json')
                if($scenario -eq 'intent-only'){foreach($name in $names){Copy-Item (Join-Path $archive $name) (Join-Path $root $name) -Force}}
                if($scenario -eq 'after-context'){foreach($name in $names[1..2]){Copy-Item (Join-Path $archive $name) (Join-Path $root $name) -Force}}
                if($scenario -eq 'after-receipts'){
                    foreach($name in @('acl-handoff-intent.json','acl-handoff-completed.json','acl-handoff-original-context.json',
                        'acl-handoff-original-installer-Install-intent.json','acl-handoff-original-installer-Install-completed.json',
                        'cleanup-ready.json','cleanup-failed.json','cleanup-error.json','task-cleanup-intent.json','task-cleanup-registered.json','task-cleanup-started.json')){
                        Copy-Item (Join-Path $archive $name) (Join-Path $root $name)
                    }
                }
            }
            if($scenario -eq 'changed-archive'){Add-Content (Join-Path $archive 'cleanup-failed.json') 'changed'}
            $reopened=Get-Content (Join-Path $root 'context.json') -Raw|ConvertFrom-Json -AsHashtable
            $reopened.IdentitySha256=(Get-FileHash (Join-Path $root 'context.json')).Hash
            $failure=$null;try{Assert-CommissionAclHandoff $reopened;Assert-CommissionInstallerSettled $reopened}catch{$failure=$_.Exception.Message}
            $script:effects=0
            function Stop-CommissionActors {param($Context) $script:effects++}
            function Invoke-CommissionServiceAction {param($Context,$Action) $script:effects++}
            if($failure){try{Invoke-CommissionRollback $reopened}catch{}}
            @{Failure=$failure;RestoredExact=$restoredExact;FailurePreserved=$preserved;ReplayRefused=$replayRefused;Effects=$script:effects;
                ActiveFailureAbsent=(-not(Test-Path (Join-Path $root 'cleanup-failed.json')));InstallerId=(Get-Content (Join-Path $root 'installer-Install-completed.json') -Raw|ConvertFrom-Json).Process.ProcessId}|ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal), simulateCommissionMembership: true);
        Assert.True(result.GetProperty("RestoredExact").GetBoolean());
        Assert.True(result.GetProperty("FailurePreserved").GetBoolean());
        Assert.True(result.GetProperty("ReplayRefused").GetBoolean());
        Assert.Equal(scenario != "after-receipts", result.GetProperty("ActiveFailureAbsent").GetBoolean());
        Assert.Equal(456, result.GetProperty("InstallerId").GetInt32());
        Assert.Equal(0, result.GetProperty("Effects").GetInt32());
        if (accepted) Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
        else Assert.Contains(scenario == "changed-archive" ? "Frozen input changed" : "Pre-admission reset is partial", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("before-context", false)]
    [InlineData("after-context", false)]
    [InlineData("after-intent", false)]
    [InlineData("before-completion", false)]
    [InlineData("changed-context", false)]
    [InlineData("changed-original", false)]
    public async Task Acl_checkpoint_handoff_preserves_originals_and_refuses_partial_recovery(string scenario, bool accepted)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));. (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/commission-installation.ps1')
            $value=@{WorkRoot=$root;OperatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
                OperatorIdentity=@{ProcessId=123;StartedUtc='2026-01-01T00:00:00Z';Executable='original-operator'};
                InstallationId=[guid]::NewGuid().ToString('N');Targets=@('unchanged-repository','unchanged-database');InstallerUncertain=$false}
            $value|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'context.json')
            $context=Get-Content (Join-Path $root 'context.json') -Raw|ConvertFrom-Json -AsHashtable
            $context.IdentitySha256=(Get-FileHash (Join-Path $root 'context.json')).Hash
            Write-CommissionOperatorReceipt $context 'installer-Install-intent' @{ContextSha256=$context.IdentitySha256;Phase='Install';Path='already-completed-installer';Sha256=('B'*64)}
            Write-CommissionOperatorReceipt $context 'installer-Install-completed' @{ContextSha256=$context.IdentitySha256;ExitCode=0;Joined=$true;Process=@{ProcessId=456;StartedUtc='2026-01-01T00:00:01Z';Executable='already-completed-installer'}}
            $names=@('context.json','installer-Install-intent.json','installer-Install-completed.json')
            $originalHashes=@{};foreach($name in $names){$originalHashes[$name]=(Get-FileHash (Join-Path $root $name)).Hash}
            Publish-CommissionAclHandoff $context
            $replayRefused=$false;try{Publish-CommissionAclHandoff $context}catch{$replayRefused=$_.Exception.Message -like '*cannot be replayed*'}
            $originalsExact=@($names|Where-Object {(Get-FileHash (Join-Path $root ('acl-handoff-original-'+$_))).Hash -cne $originalHashes[$_]}).Count -eq 0
            $successor=Get-Content (Join-Path $root 'context.json') -Raw|ConvertFrom-Json -AsHashtable
            $actualProcess=Get-Process -Id $PID
            try{$operatorMatches=$successor.OperatorIdentity.ProcessId -eq $PID -and ([DateTimeOffset]$successor.OperatorIdentity.StartedUtc).UtcDateTime -eq $actualProcess.StartTime.ToUniversalTime()}finally{$actualProcess.Dispose()}
            $scenario='SCENARIO_VALUE'
            if($scenario -like '*context' -or $scenario -in @('after-intent','before-completion')){
                if($scenario -ne 'changed-context'){Remove-Item -LiteralPath (Join-Path $root 'acl-handoff-completed.json')}
                if($scenario -eq 'before-context'){foreach($name in $names){Copy-Item (Join-Path $root ('acl-handoff-original-'+$name)) (Join-Path $root $name) -Force}}
                if($scenario -eq 'after-context'){foreach($name in $names[1..2]){Copy-Item (Join-Path $root ('acl-handoff-original-'+$name)) (Join-Path $root $name) -Force}}
                if($scenario -eq 'after-intent'){Copy-Item (Join-Path $root ('acl-handoff-original-'+$names[2])) (Join-Path $root $names[2]) -Force}
                if($scenario -eq 'changed-context'){$successor.Targets=@('different-repository');$successor|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'context.json')}
            }
            if($scenario -eq 'changed-original'){Add-Content (Join-Path $root 'acl-handoff-original-context.json') 'changed'}
            # Reopen from the real current context; do not reuse the in-memory successor.
            $reopened=Get-Content (Join-Path $root 'context.json') -Raw|ConvertFrom-Json -AsHashtable
            $reopened.IdentitySha256=(Get-FileHash (Join-Path $root 'context.json')).Hash
            $failure=$null;try{Assert-CommissionAclHandoff $reopened;Assert-CommissionInstallerSettled $reopened}catch{$failure=$_.Exception.Message}
            $script:effects=0
            function Stop-CommissionActors {param($Context) $script:effects++}
            function Invoke-CommissionServiceAction {param($Context,$Action) $script:effects++}
            if($failure){try{Invoke-CommissionRollback $reopened}catch{}}
            @{Failure=$failure;OriginalsExact=$originalsExact;OperatorMatches=$operatorMatches;TargetsUnchanged=($successor.Targets -join ',') -ceq 'unchanged-repository,unchanged-database';
                Effects=$script:effects;ReplayRefused=$replayRefused;OriginalInstallerRetained=(Get-Content (Join-Path $root 'acl-handoff-original-installer-Install-completed.json') -Raw|ConvertFrom-Json).Process.ProcessId -eq 456}|ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal), simulateCommissionMembership: true);
        Assert.True(result.GetProperty("OriginalsExact").GetBoolean());
        Assert.True(result.GetProperty("OperatorMatches").GetBoolean());
        Assert.True(result.GetProperty("OriginalInstallerRetained").GetBoolean());
        Assert.True(result.GetProperty("ReplayRefused").GetBoolean());
        Assert.Equal(0, result.GetProperty("Effects").GetInt32());
        if (accepted)
        {
            Assert.True(result.GetProperty("TargetsUnchanged").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
        }
        else Assert.Contains(scenario == "changed-original" ? "Frozen input changed" : scenario == "changed-context" ? "ACL handoff binding changed" : "ACL handoff is partial",
            result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reopened_installer_context_refuses_an_unmatched_intent_without_a_catch_marker(bool completed)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));. (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/commission-installation.ps1')
            $context=@{WorkRoot=$root;IdentitySha256=('A'*64);InstallerUncertain=$false}
            Write-CommissionOperatorReceipt $context 'installer-Install-intent' @{ContextSha256=$context.IdentitySha256;Phase='Install'}
            if(COMPLETED_VALUE){Write-CommissionOperatorReceipt $context 'installer-Install-completed' @{ContextSha256=$context.IdentitySha256;Joined=$true;ExitCode=0}}
            # This is a fresh object, with no process-local catch state.
            $reopened=@{WorkRoot=$root;IdentitySha256=('A'*64);InstallerUncertain=$false};$failure=$null
            try{Assert-CommissionInstallerSettled $reopened}catch{$failure=$_.Exception.Message}
            @{Failure=$failure;CatchMarkerExists=(Test-Path (Join-Path $root 'installer-uncertain.json'))}|ConvertTo-Json -Compress
            """.Replace("COMPLETED_VALUE", completed ? "$true" : "$false", StringComparison.Ordinal), simulateCommissionMembership: true);
        Assert.False(result.GetProperty("CatchMarkerExists").GetBoolean());
        if (completed) Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
        else Assert.Contains("intent has no confirmed completion", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reopened_task_context_recovers_from_intent_without_launch_acknowledgement_and_refuses_a_mismatch(bool mismatched)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));. (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/commission-installation.ps1')
            $id=[guid]::NewGuid().ToString('N');$context=@{WorkRoot=$root;InstallationId=$id;IdentitySha256=('A'*64);Tasks=[Collections.Generic.List[object]]::new()}
            $intent=@{Name=('FV-NEXT002-'+$id+'-cleanup');ContextSha256=$context.IdentitySha256;Execute='owned-pwsh';Arguments='exact-owned-script';Description='exact-owned-description'}
            Write-CommissionOperatorReceipt $context 'task-cleanup-intent' $intent
            $script:observed=[pscustomobject]@{State='Ready';Description=$intent.Description;Actions=@([pscustomobject]@{Execute=$intent.Execute;Arguments=$(if(MISMATCH_VALUE){'unrelated-script'}else{$intent.Arguments});WorkingDirectory=''});
                Triggers=@();Principal=[pscustomobject]@{UserId='SYSTEM';RunLevel='Highest';LogonType='ServiceAccount'};Settings=[pscustomobject]@{ExecutionTimeLimit='PT3M';MultipleInstances='IgnoreNew'}}
            $script:retired=$false;$script:resultChecked=$false;$script:observations=0
            function Get-ScheduledTask {param($TaskName) if($TaskName -like '*-cleanup' -and -not $script:retired){
                $script:observations++;$script:observed.State=$(if($script:observations -lt 4){'Running'}else{'Ready'})
                if($script:observed.State -eq 'Ready'){$script:resultChecked=$true};$script:observed}}
            function Unregister-ScheduledTask {param($TaskName,$Confirm) if(-not $script:resultChecked){throw 'Task removal preceded joining'};$script:retired=$true}
            function Export-ScheduledTask {param($TaskName) 'exact-native-definition'}
            $failure=$null;try{Read-CommissionRecoveryTasks $context}catch{$failure=$_.Exception.Message}
            $count=$context.Tasks.Count
            if(-not $failure){$context.Jobs=@{};Stop-CommissionActors $context}
            @{Failure=$failure;Recovered=$count;Retired=$script:retired;ResultChecked=$script:resultChecked;StartedExists=(Test-Path (Join-Path $root 'task-cleanup-started.json'));IntentRetained=(Test-Path (Join-Path $root 'task-cleanup-intent.json'))}|ConvertTo-Json -Compress
            """.Replace("MISMATCH_VALUE", mismatched ? "$true" : "$false", StringComparison.Ordinal), simulateCommissionMembership: true);
        Assert.False(result.GetProperty("StartedExists").GetBoolean());
        Assert.True(result.GetProperty("IntentRetained").GetBoolean());
        Assert.Equal(mismatched ? 0 : 1, result.GetProperty("Recovered").GetInt32());
        Assert.Equal(!mismatched, result.GetProperty("Retired").GetBoolean());
        Assert.Equal(!mismatched, result.GetProperty("ResultChecked").GetBoolean());
        if (mismatched) Assert.Contains("exact pre-registration intent", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
        else Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("authentication-failed", false)]
    [InlineData("creator-failed", false)]
    [InlineData("setup-failed", false)]
    [InlineData("installer-uncertain", false)]
    public async Task Installation_flow_keeps_activation_after_retirement_creator_and_joined_setup(string scenario, bool activated)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));. (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/commission-installation.ps1')
            $script:events=[Collections.Generic.List[string]]::new();$script:scenario='SCENARIO_VALUE'
            function Assert-CommissionCandidate {param($Context,[switch]$Installed,[switch]$Legacy) $script:events.Add($(if($Installed){'verify-installed'}elseif($Legacy){'verify-legacy'}else{'preflight'}))}
            function Invoke-CommissionServiceAction {param($Context,$Action) $script:events.Add($Action)}
            function Move-CommissionLegacyState {param($Context,$Phase) $script:events.Add($Phase)}
            function Invoke-CommissionInstaller {param($Context,$Phase) $script:events.Add($Phase);if($script:scenario -eq 'installer-uncertain'){ $Context.InstallerUncertain=$true;throw 'Installer transaction is unresolved'}}
            function Set-CommissionAncestorState {param($Context,$Phase) $script:events.Add('acl-'+$Phase)}
            function Invoke-CommissionAuthenticationTasks {param($Context) $script:events.Add('authentication');if($script:scenario -eq 'authentication-failed'){throw 'Retirement unconfirmed'}}
            function Start-CommissionSetupTask {param($Context) $script:events.Add('setup-dispatch');@{Name='owned-setup'}}
            function Invoke-CommissionCreatorConfirmation {param($Context) $script:events.Add('creator');if($script:scenario -eq 'creator-failed'){throw 'Creator acknowledgement uncertain'};@{Confirmed=$true}}
            function Complete-CommissionSetupTask {param($Context,$Task) $script:events.Add('setup-joined');if($script:scenario -eq 'setup-failed'){throw 'Setup publication unconfirmed'};@{BootstrapVerified=$true}}
            function Assert-CommissionActivation {param($Context,$Creator,$Setup) if(-not $Creator.Confirmed -or -not $Setup.BootstrapVerified){throw 'Invalid activation'};$script:events.Add('activation-check')}
            function Stop-CommissionActors {param($Context) $script:events.Add('join-actors')}
            function Write-CommissionOperatorReceipt {param($Context,$Name,$Value) $script:events.Add($Name)}
            $context=@{IdentitySha256=('A'*64);InstallerUncertain=$false};$failure=$null
            try{Invoke-CommissionInstallation $context|Out-Null}catch{$failure=$_.Exception.Message}
            @{Events=@($script:events);Failure=$failure;Uncertain=$context.InstallerUncertain}|ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        var events = result.GetProperty("Events").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(activated, events.Contains("Activate"));
        Assert.Equal("join-actors", events[^1]);
        if (activated)
        {
            Assert.Equal(["preflight", "Stop", "Preserve", "Install", "verify-installed", "acl-Apply", "authentication", "setup-dispatch", "creator", "setup-joined", "activation-check", "Activate", "installation-completed", "join-actors"], events);
        }
        else
        {
            Assert.NotNull(result.GetProperty("Failure").GetString());
            Assert.Contains("Stop", events);
            Assert.DoesNotContain("Restore", events); // No automatic recovery/retry from uncertainty.
        }
        Assert.Equal(scenario == "installer-uncertain", result.GetProperty("Uncertain").GetBoolean());
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("authentication-failed", false)]
    [InlineData("installer-uncertain", false)]
    public async Task Rollback_restores_verified_legacy_state_and_authentication_before_the_old_installer(string scenario, bool restored)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));. (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/commission-installation.ps1')
            $script:events=[Collections.Generic.List[string]]::new();$script:scenario='SCENARIO_VALUE'
            function Assert-CommissionRollbackInputs {param($Context) $script:events.Add('rollback-inputs');if($Context.InstallerUncertain){throw 'Installer transaction unresolved'}}
            function Stop-CommissionActors {param($Context) $script:events.Add('join-actors')}
            function Invoke-CommissionServiceAction {param($Context,$Action) $script:events.Add($Action)}
            function Invoke-CommissionInstaller {param($Context,$Phase) $script:events.Add($Phase)}
            function Move-CommissionLegacyState {param($Context,$Phase) $script:events.Add($Phase)}
            function Restore-CommissionOriginalAuthentication {param($Context) $script:events.Add('restore-auth');if($script:scenario -eq 'authentication-failed'){throw 'Auth restore unconfirmed'}}
            function Set-CommissionAncestorState {param($Context,$Phase) $script:events.Add('acl-'+$Phase)}
            function Assert-CommissionCandidate {param($Context,[switch]$Installed,[switch]$Legacy) $script:events.Add('verify-legacy')}
            function Write-CommissionOperatorReceipt {param($Context,$Name,$Value) $script:events.Add($Name)}
            $context=@{IdentitySha256=('A'*64);FailedRoot=(Join-Path $root 'retained-failed');InstallerUncertain=($script:scenario -eq 'installer-uncertain')};$failure=$null
            try{Invoke-CommissionRollback $context|Out-Null}catch{$failure=$_.Exception.Message}
            @{Events=@($script:events);Failure=$failure}|ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        var events = result.GetProperty("Events").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(restored, events.Contains("rollback-completed"));
        if (restored)
        {
            Assert.Equal(["rollback-inputs", "join-actors", "Stop", "Uninstall", "Restore", "restore-auth", "acl-Restore", "LegacyInstall", "verify-legacy", "rollback-completed"], events);
        }
        else
        {
            Assert.NotNull(result.GetProperty("Failure").GetString());
            Assert.DoesNotContain("LegacyInstall", events);
            Assert.DoesNotContain("Uninstall", scenario == "installer-uncertain" ? events : []);
        }
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("cleanup-unconfirmed", false)]
    [InlineData("watchdog-failed", false)]
    [InlineData("setup-failed", false)]
    [InlineData("binding-mismatch", false)]
    public async Task Setup_launcher_requires_retired_admission_and_joined_verified_publication(string scenario, bool accepted)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module));$prepared=Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation'
            $helper=Import-Module (Join-Path $prepared 'commission-setup.psm1') -Force -PassThru
            $scenario='SCENARIO_VALUE';$ticket=Join-Path $root 'ticket.json';Copy-Item (Join-Path $prepared 'installation-ticket.template.json') $ticket
            # Exercise real current assemblies from protected owned copies, without
            # depending on the developer's frozen candidate directory.
            $assemblyRoot=Join-Path $root 'setup-assemblies';New-VaultFixtureProtectedDirectory $assemblyRoot
            foreach($name in @('FluxVault.Abstractions.dll','FluxVault.Core.dll','FluxVault.Windows.dll')) {
                Copy-Item -LiteralPath (Join-Path $testAssemblies $name) -Destination (Join-Path $assemblyRoot $name)
            }
            $context=@{WorkRoot=$root;IdentitySha256='A'*64;OperatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
                Setup=@{TicketPath=$ticket;TicketSha256=(Get-FileHash $ticket).Hash;ServiceExe='owned-setup-host';ServiceExeSha256='B'*64};
                Postmaster=@{ProcessId=123;StartedUtc='2026-10-01T00:00:00Z';Executable='owned-postmaster'};Port=5432;ServiceDatabase='fluxvault_single';ServiceRole='fluxvault_service'}
            @{ContextSha256=$context.IdentitySha256;CanActivate=($scenario -ne 'cleanup-unconfirmed');AdmissionRetired=$true;OwnedJobJoined=$true;FinalRetained=$true;Postmaster=$context.Postmaster;
                State=@{database='fluxvault_single';role='fluxvault_service';port=5432}}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root 'cleanup-completed.json')
            if($scenario -eq 'watchdog-failed'){'failed'|Set-Content (Join-Path $root 'cleanup-failed.json')}
            $childScript=Join-Path $root 'setup-child.ps1';[IO.File]::WriteAllText($childScript,'param($Ticket,$ExitCode) if(-not(Test-Path -LiteralPath $Ticket)){exit 9};Start-Sleep -Milliseconds 500;exit ([int]$ExitCode)')
            $actual=& $helper {
                param($context,$prepared,$scenario,$childScript,$assemblyRoot)
                function Assert-CommissionSetupNativeState {}
                function Get-CommissionSetupAssemblyRoot {param($Context) $script:assemblyRoot}
                function Assert-CommissionSetupExecutable {}
                function Invoke-CommissionSetupHost {
                    param($Context,$TicketPath)
                    $script:launched=$true;$exit=if($script:scenario -eq 'setup-failed'){3}else{0}
                    $start=[Diagnostics.ProcessStartInfo]::new('pwsh');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
                    foreach($arg in @('-NoProfile','-NonInteractive','-File',$script:childScript,$TicketPath,[string]$exit)){$start.ArgumentList.Add($arg)}
                    $child=[Diagnostics.Process]::Start($start)
                    try{$identity=Get-VaultFixtureProcessIdentity $child;if(-not $child.WaitForExit(5000)){throw 'Owned setup test child exceeded its deadline'};@{ExitCode=$child.ExitCode;Joined=$true;Process=$identity}}
                    finally{if(-not $child.HasExited){$child.Kill($true);$child.WaitForExit(5000)|Out-Null};$child.Dispose()}
                }
                function Read-CommissionSetupBootstrap {
                    param($Ticket)
                    if($script:scenario -eq 'binding-mismatch'){return @{Matches=$false;Sha256='C'*64}}
                    @{Matches=$true;Sha256='C'*64}
                }
                $script:prepared=$prepared;$script:scenario=$scenario;$script:childScript=$childScript;$script:assemblyRoot=$assemblyRoot;$script:launched=$false
                $reply=$null;$failure=$null
                try{$reply=Invoke-CommissionSetup $context}catch{$failure=$_.Exception.Message}
                @{Reply=$reply;Failure=$failure;Launched=$script:launched;Completed=(Test-Path (Join-Path $context.WorkRoot 'setup-completed.json'));
                    InputRetained=(Test-Path $context.Setup.TicketPath);CopiedTicket=(Test-Path (Join-Path $context.WorkRoot 'setup-intents/installation-ticket.json'))}|ConvertTo-Json -Depth 8 -Compress
            } $context $prepared $scenario $childScript $assemblyRoot
            $actual
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal), simulateCommissionMembership: true);
        Assert.True(result.GetProperty("InputRetained").GetBoolean());
        Assert.Equal(scenario is not ("cleanup-unconfirmed" or "watchdog-failed"), result.GetProperty("Launched").GetBoolean());
        Assert.True(accepted == result.GetProperty("Completed").GetBoolean(), result.ToString());
        if (accepted)
        {
            var reply = result.GetProperty("Reply");
            Assert.True(reply.GetProperty("SetupHost").GetProperty("Joined").GetBoolean());
            Assert.True(reply.GetProperty("BootstrapVerified").GetBoolean());
            Assert.False(reply.GetProperty("CanActivate").GetBoolean());
            Assert.True(reply.GetProperty("CreatorConfirmationRequired").GetBoolean());
        }
        else
        {
            Assert.NotNull(result.GetProperty("Failure").GetString());
        }
    }

    [Theory]
    [InlineData("temporary", false)]
    [InlineData("final", true)]
    [InlineData("final-unconfirmed", false)]
    [InlineData("unknown-bytes", false)]
    [InlineData("administrator-survives", false)]
    [InlineData("torn-write", false)]
    [InlineData("before-truncate", false)]
    [InlineData("monitoring-error", false)]
    public async Task Independent_authentication_cleanup_joins_its_real_job_and_preserves_successful_final_state(string scenario, bool canActivate)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $helper=Join-Path (Split-Path (Split-Path (Split-Path $module))) 'docs/verification/2026-10-05-next002-rollout-preparation/commission-authentication.psm1'
            $auth=Import-Module $helper -Force -PassThru;Import-VaultFixtureJobType
            $scenario='SCENARIO_VALUE';$data=Join-Path $root 'data';New-VaultFixtureProtectedDirectory $data
            $context=@{NormalInstallation=$false;WorkRoot=$root;DataDirectory=$data;OperatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
                IdentitySha256='A'*64;Role='postgres';LegacyRole='legacy';Port=54321;ServiceDatabase='single';ServiceRole='service';
                ApplicationName='owned-cleanup';LegacyConnection='legacy';ServiceConnection='service';Connection='admin';
                PgCtlPath='owned-reload-tool';
                Postmaster=@{ProcessId=123;Executable='owned-postmaster';StartedUtc='2026-10-01T00:00:00Z'};OriginalHashes=@{};Prepared=@{}}
            foreach($name in @('pg_hba.conf','pg_ident.conf')) {
                [IO.File]::WriteAllText((Join-Path $root ('original-'+$name)),('original-'+$name))
                $context.OriginalHashes[$name]=(Get-FileHash -LiteralPath (Join-Path $root ('original-'+$name))).Hash
                [IO.File]::WriteAllText((Join-Path $data $name),$(if($scenario -like 'final*'){'final-'+$name}elseif($scenario -eq 'unknown-bytes'){'external-'+$name}else{'temporary-'+$name}))
            }
            foreach($entry in @(@('FinalHba','pg_hba.conf'),@('FinalIdent','pg_ident.conf'),@('TemporaryHba','pg_hba.conf'),@('TemporaryIdent','pg_ident.conf'),@('ProbeIdent','pg_ident.conf'))) {
                $path=Join-Path $root $entry[0];$prefix=if($entry[0] -like 'Final*'){'final-'}elseif($entry[0] -eq 'ProbeIdent'){'probe-'}else{'temporary-'}
                [IO.File]::WriteAllText($path,($prefix+$entry[1]));$context.Prepared[$entry[0]]=@{Path=$path;Sha256=(Get-FileHash -LiteralPath $path).Hash}
            }
            if($scenario -in @('torn-write','before-truncate')) {
                $old=[IO.File]::ReadAllBytes($context.Prepared.TemporaryIdent.Path);$new=[IO.File]::ReadAllBytes($context.Prepared.FinalIdent.Path)
                $prefix=if($scenario -eq 'torn-write'){8}else{$new.Length}
                $observed=[byte[]]$old.Clone();[Array]::Copy($new,$observed,$prefix)
                [IO.File]::WriteAllBytes((Join-Path $data 'pg_ident.conf'),$observed)
                [IO.File]::WriteAllBytes((Join-Path $root 'write-old.bin'),$old);[IO.File]::WriteAllBytes((Join-Path $root 'write-new.bin'),$new)
                @{ContextSha256=$context.IdentitySha256;File='pg_ident.conf';OldFile='write-old.bin';NewFile='write-new.bin';
                    OldSha256=$context.Prepared.TemporaryIdent.Sha256;NewSha256=$context.Prepared.FinalIdent.Sha256;OldLength=$old.Length;NewLength=$new.Length}|
                    ConvertTo-Json|Set-Content (Join-Path $root 'authentication-write-intent.json')
            }
            @{Role='postgres';BackendPids=@();Postmaster=$context.Postmaster}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'administrator-baseline.json')
            if($scenario -eq 'final'){@{Accepted=$true;Postmaster=$context.Postmaster;Administrator=@{Backend=@{backend_pid=42}}}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'authentication-completed.json')}
            [IO.File]::WriteAllText((Join-Path $root 'partial-database-state'),'retain SQL effects')
            $jobName='Global\FluxVault.NEXT002.'+[Guid]::NewGuid().ToString('N');$job=[FluxVault.Fixtures.OwnedWindowsJob]::Create($jobName,$context.OperatorSid)
            $context.WorkerJob=@{KernelName=$jobName;OwnerSid=$context.OperatorSid}
            $child=[Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{FileName='pwsh';Arguments='-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 30"';UseShellExecute=$false;CreateNoWindow=$true})
            $failure=$null;$reply=$null
            try {
                $job.Assign($child)
                $reply=& $auth {
                    param($context,$scenario)
                    function Assert-CommissionSystemWorker {}
                    function Assert-CommissionPostmaster {}
                    function Invoke-CommissionTool {param($Context,$Executable,$Arguments,$InputText) @{ExitCode=0;Joined=$true}}
                    function Invoke-CommissionQuery {
                        param($Context,$Connection,$Sql,[switch]$AllowRefusal)
                        if($Connection -eq 'admin'){return @{ExitCode=2;Output='';Error='denied'}}
                        $pids=if($script:cleanupScenario -eq 'administrator-survives'){@(777)}else{@()}
                        @{ExitCode=0;Output=(@{administrator_absent=$true;admin_pids=@($pids);legacy_pids=@();database='single';role='service';port=54321}|ConvertTo-Json -Compress)}
                    }
                    $script:cleanupScenario=$scenario
                    if($scenario -eq 'monitoring-error') {
                        $Context.OperatorIdentity=@{ProcessId=1};$script:observations=0
                        function Test-CommissionExactProcess {param($Identity) $script:observations++;if($script:observations -gt 1){throw 'Injected process observation failure'};return $true}
                        return Invoke-CommissionAuthenticationCleanupWatch $context -TimeoutSeconds 15
                    }
                    Complete-CommissionAuthenticationCleanup $context
                } $context $scenario
            } catch {$failure=$_.Exception.Message}
            finally {$joinedBeforeFallback=$child.HasExited;try{if(-not $child.HasExited){$child.Kill($true)};$child.WaitForExit(5000)|Out-Null}finally{$job.Dispose();$child.Dispose()}}
            $hba=[IO.File]::ReadAllText((Join-Path $data 'pg_hba.conf'));$ident=[IO.File]::ReadAllText((Join-Path $data 'pg_ident.conf'))
            @{Reply=$reply;Failure=$failure;Completed=(Test-Path -LiteralPath (Join-Path $root 'cleanup-completed.json'));
                Hba=$hba;Ident=$ident;JoinedBeforeFallback=$joinedBeforeFallback;
                PartialStateRetained=[IO.File]::ReadAllText((Join-Path $root 'partial-database-state')) -ceq 'retain SQL effects'}|ConvertTo-Json -Compress -Depth 7
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        Assert.True(result.GetProperty("PartialStateRetained").GetBoolean());
        Assert.True(result.GetProperty("JoinedBeforeFallback").GetBoolean(), result.ToString());
        var shouldComplete = scenario is "temporary" or "final" or "final-unconfirmed" or "torn-write" or "before-truncate" or "monitoring-error";
        Assert.True(shouldComplete == result.GetProperty("Completed").GetBoolean(), result.ToString());
        if (shouldComplete)
        {
            if (scenario == "monitoring-error")
            {
                Assert.Contains("Injected process observation failure", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
            }
            else
            {
                var reply = result.GetProperty("Reply");
                Assert.True(reply.GetProperty("OwnedJobJoined").GetBoolean());
                Assert.True(reply.GetProperty("AdmissionRetired").GetBoolean());
                Assert.Equal(canActivate, reply.GetProperty("CanActivate").GetBoolean());
            }
        }
        else
        {
            var failure = result.GetProperty("Failure").GetString();
            Assert.Contains(scenario == "unknown-bytes" ? "unaccounted bytes" : "unaccounted administrator", failure, StringComparison.OrdinalIgnoreCase);
        }
        var prefix = scenario.StartsWith("final", StringComparison.Ordinal) ? "final-" : scenario == "unknown-bytes" ? "external-" : "original-";
        Assert.Equal(prefix + "pg_hba.conf", result.GetProperty("Hba").GetString());
        Assert.Equal(prefix + "pg_ident.conf", result.GetProperty("Ident").GetString());
    }

    [Theory]
    [Trait("Category", "RequiresPreparedWindowsInstallation")]
    [InlineData("protected-template", true)]
    [InlineData("volume-delete", true)]
    [InlineData("creator-owner", true)]
    [InlineData("ancestor-delete", false)]
    [InlineData("ancestor-delete-child", false)]
    [InlineData("unprotected-template", false)]
    [InlineData("daemon-executable", false)]
    public async Task Normal_PostgreSql_guard_checks_real_descriptors_without_broadening_other_paths(string scenario, bool accepted)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $preparation=Join-Path (Split-Path (Split-Path (Split-Path $module))) 'docs/verification/2026-10-05-next002-rollout-preparation'
            Add-Type -Path (Join-Path $preparation 'postgresql-ancestor-acl.cs')
            $auth=Import-Module (Join-Path $preparation 'commission-authentication.psm1') -Force -PassThru
            $scenario='SCENARIO_VALUE';$owner=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $data=Join-Path $root 'data';$bin=Join-Path $root 'bin'
            foreach($path in @($data,$bin)){New-VaultFixtureProtectedDirectory $path}
            $hba=Join-Path $data 'pg_hba.conf';$exe=Join-Path $bin 'psql.exe'
            [IO.File]::WriteAllText($hba,'owned fixture');[IO.File]::WriteAllText($exe,'owned fixture')
            $map=@{}
            foreach($virtual in @('D:\','D:\Program Files','D:\Program Files\PostgreSQL','D:\Program Files\PostgreSQL\18')) {
                $path=Join-Path $root ([Guid]::NewGuid().ToString('N'));New-VaultFixtureProtectedDirectory $path;$map[$virtual]=$path
            }
            $map['D:\Program Files\PostgreSQL\18\data']=$data;$map['D:\Program Files\PostgreSQL\18\bin']=$bin
            $map['D:\Program Files\PostgreSQL\18\data\pg_hba.conf']=$hba;$map['D:\Program Files\PostgreSQL\18\bin\psql.exe']=$exe
            $higher=$map['D:\Program Files'];$acl=Get-Acl -LiteralPath $higher
            $rights=if($scenario -eq 'ancestor-delete'){'Modify'}elseif($scenario -eq 'ancestor-delete-child'){'DeleteSubdirectoriesAndFiles'}else{'Write'}
            $propagation=if($scenario -in @('ancestor-delete','ancestor-delete-child')){'None'}else{'InheritOnly'}
            $inherit=if($propagation -eq 'None'){'None'}else{'ContainerInherit,ObjectInherit'}
            $sid=if($scenario -eq 'creator-owner'){'S-1-3-0'}else{'S-1-5-11'}
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),$rights,$inherit,$propagation,'Allow'))
            Set-Acl -LiteralPath $higher -AclObject $acl
            if($scenario -in @('unprotected-template','creator-owner')) {
                foreach($path in $map.Values | Select-Object -Unique){$acl=Get-Acl -LiteralPath $path;$acl.SetAccessRuleProtection($false,$true);Set-Acl -LiteralPath $path -AclObject $acl}
            }
            if($scenario -eq 'volume-delete') {
                $acl=Get-Acl -LiteralPath $map['D:\'];$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-11'),'Modify','None','None','Allow'))
                Set-Acl -LiteralPath $map['D:\'] -AclObject $acl
            }
            if($scenario -eq 'daemon-executable') {
                $acl=Get-Acl -LiteralPath $exe;$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-20'),'Write','Allow'))
                Set-Acl -LiteralPath $exe -AclObject $acl
            } else {
                $acl=Get-Acl -LiteralPath $hba;$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-20'),'FullControl','Allow'))
                Set-Acl -LiteralPath $hba -AclObject $acl
            }
            $reply=& $auth {
                param($map,$owner,$scenario)
                $script:pgGuardMap=$map;$script:pgGuardOwner=$owner
                function Get-Item {param($LiteralPath,[switch]$Force) Microsoft.PowerShell.Management\Get-Item -LiteralPath $script:pgGuardMap[$LiteralPath] -Force}
                function Get-Acl {param($LiteralPath) Microsoft.PowerShell.Security\Get-Acl -LiteralPath $script:pgGuardMap[$LiteralPath]}
                function Test-Path {param($LiteralPath) $script:pgGuardMap.ContainsKey($LiteralPath)}
                function Get-CimInstance {param($ClassName,$Filter) @{State='Running';StartName='NT AUTHORITY\NetworkService'}}
                function Get-LocalGroupMember {param($SID) [pscustomobject]@{SID=[Security.Principal.SecurityIdentifier]::new($script:pgGuardOwner)}}
                $context=@{NormalInstallation=$true;DataDirectory='D:\Program Files\PostgreSQL\18\data';OperatorSid=$owner}
                $failure=$null
                try {
                    if($scenario -eq 'daemon-executable'){Assert-CommissionPostgresqlPath $context 'D:\Program Files\PostgreSQL\18\bin\psql.exe'}
                    else{Assert-CommissionPostgresqlPath $context 'D:\Program Files\PostgreSQL\18\data\pg_hba.conf' -Authentication}
                }catch{$failure=$_.Exception.Message}
                @{Accepted=$null -eq $failure;Failure=$failure}
            } $map $owner $scenario
            $reply | ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        Assert.True(accepted == result.GetProperty("Accepted").GetBoolean(), result.ToString());
        if (!accepted)
        {
            var failure = result.GetProperty("Failure").GetString();
            Assert.False(string.IsNullOrWhiteSpace(failure));
            Assert.DoesNotContain("not recognized", failure, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PostgreSql_ancestor_correction_changes_only_two_descriptors_and_rolls_back_exactly()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $source=Join-Path (Split-Path (Split-Path (Split-Path $module))) 'docs/verification/2026-10-05-next002-rollout-preparation/postgresql-ancestor-acl.cs'
            if(-not(Test-Path -LiteralPath $source)){throw 'The checked ancestor correction is missing.'}
            . (Join-Path (Split-Path $source) 'commission-installation.ps1')
            Add-Type -Path $source
            Add-Type @'
            using System;
            using System.Runtime.InteropServices;
            using System.Security.AccessControl;
            using System.Security.Principal;
            using System.Text;
            using Microsoft.Win32.SafeHandles;
            public static class AclFixtureSetup {
                [DllImport("advapi32",EntryPoint="SetFileSecurityW",CharSet=CharSet.Unicode,SetLastError=true)]
                private static extern bool Set(string path,uint info,byte[] descriptor);
                [DllImport("advapi32",EntryPoint="GetFileSecurityW",CharSet=CharSet.Unicode,SetLastError=true)]
                private static extern bool Get(string path,uint info,byte[] descriptor,uint length,out uint needed);
                public static string Read(string path) {
                    Get(path,7,null,0,out var needed);var bytes=new byte[needed];
                    if(!Get(path,7,bytes,needed,out needed))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    return new RawSecurityDescriptor(bytes,0).GetSddlForm(AccessControlSections.All);
                }
                public static void Write(string path,string sddl) {
                    var raw=new RawSecurityDescriptor(sddl);var bytes=new byte[raw.BinaryLength];raw.GetBinaryForm(bytes,0);
                    var protection=(raw.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=0?0x80000000u:0x20000000u;
                    if(!Set(path,7u|protection,bytes))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                [StructLayout(LayoutKind.Sequential)]private struct SidAndAttributes{public IntPtr Sid;public uint Attributes;}
                [DllImport("advapi32",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
                [DllImport("advapi32",SetLastError=true)]private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token,uint flags,uint disableCount,ref SidAndAttributes disable,uint privilegeCount,IntPtr privileges,uint restrictCount,IntPtr restrict,out SafeAccessTokenHandle restricted);
                [DllImport("advapi32",SetLastError=true)]private static extern bool SetThreadToken(IntPtr thread,SafeAccessTokenHandle token);
                [DllImport("advapi32",SetLastError=true)]private static extern bool DuplicateToken(SafeAccessTokenHandle token,int level,out SafeAccessTokenHandle impersonation);
                [DllImport("advapi32",SetLastError=true)]private static extern bool RevertToSelf();
                [DllImport("kernel32",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle Open(string path,uint access,uint sharing,IntPtr security,uint disposition,uint flags,IntPtr template);
                [DllImport("kernel32",EntryPoint="MoveFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool Move(string from,string to);
                [DllImport("kernel32",SetLastError=true)]private static extern bool DeviceIoControl(SafeFileHandle file,uint control,byte[] input,uint inputSize,IntPtr output,uint outputSize,out uint returned,IntPtr overlapped);
                public static SafeFileHandle HoldDelete(string path) {
                    var file=Open(path,0x10000,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
                    if(file.IsInvalid){file.Dispose();throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}return file;
                }
                private static int Junction(string path,string target) {
                    var substitute=Encoding.Unicode.GetBytes("\\??\\"+target);var print=Encoding.Unicode.GetBytes(target);
                    var data=new byte[16+substitute.Length+2+print.Length+2];
                    Array.Copy(BitConverter.GetBytes(0xA0000003u),0,data,0,4);
                    Array.Copy(BitConverter.GetBytes(checked((ushort)(data.Length-8))),0,data,4,2);
                    Array.Copy(BitConverter.GetBytes(checked((ushort)substitute.Length)),0,data,10,2);
                    Array.Copy(BitConverter.GetBytes(checked((ushort)(substitute.Length+2))),0,data,12,2);
                    Array.Copy(BitConverter.GetBytes(checked((ushort)print.Length)),0,data,14,2);
                    Array.Copy(substitute,0,data,16,substitute.Length);Array.Copy(print,0,data,18+substitute.Length,print.Length);
                    using var file=Open(path,0x40000000,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
                    if(file.IsInvalid)return Marshal.GetLastWin32Error();
                    return DeviceIoControl(file,0x900A4,data,(uint)data.Length,IntPtr.Zero,0,out _,IntPtr.Zero)?0:Marshal.GetLastWin32Error();
                }
                public static int[] Probe(string parent,string postgres,string child,string control,string target,bool before) {
                    var sid=new SecurityIdentifier("S-1-5-32-544");var bytes=new byte[sid.BinaryLength];sid.GetBinaryForm(bytes,0);var memory=Marshal.AllocHGlobal(bytes.Length);
                    try {
                        Marshal.Copy(bytes,0,memory,bytes.Length);var disable=new SidAndAttributes{Sid=memory};
                        if(!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,0xE,out var original))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"OpenProcessToken");
                        using(original) {
                            if(!CreateRestrictedToken(original,1,1,ref disable,0,IntPtr.Zero,0,IntPtr.Zero,out var restricted))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"CreateRestrictedToken");
                            using(restricted) {
                                if(!DuplicateToken(restricted,2,out var impersonation))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"DuplicateToken");
                                using var joinedToken=impersonation;
                                if(!SetThreadToken(IntPtr.Zero,impersonation))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"SetThreadToken");
                                try {
                                    if(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("Administrator group is still enabled.");
                                    using var p=Open(parent,0x10000,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);var pError=p.IsInvalid?Marshal.GetLastWin32Error():0;
                                    using var q=Open(postgres,0x10000,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);var qError=q.IsInvalid?Marshal.GetLastWin32Error():0;
                                    if(before)return new[]{pError,qError};
                                    var renamed=parent+".renamed";var renameError=Move(parent,renamed)?0:Marshal.GetLastWin32Error();
                                    if(renameError==0 && !Move(renamed,parent))throw new InvalidOperationException("Unexpectedly permitted rename could not be restored.");
                                    var childRenamed=child+".renamed";var childError=Move(child,childRenamed)?0:Marshal.GetLastWin32Error();
                                    if(childError==0 && !Move(childRenamed,child))throw new InvalidOperationException("Unexpectedly permitted child rename could not be restored.");
                                    return new[]{pError,qError,renameError,childError,Junction(parent,target),Junction(control,target)};
                                } finally{if(!RevertToSelf())throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
                            }
                        }
                    } finally{Marshal.FreeHGlobal(memory);}
                }
                public static void RemoveJunction(string path) {
                    using var file=Open(path,0x40000000,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);var data=new byte[8];Array.Copy(BitConverter.GetBytes(0xA0000003u),data,4);
                    if(!DeviceIoControl(file,0x900AC,data,8,IntPtr.Zero,0,out _,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            '@
            $shared=Join-Path $root 'shared';$postgres=Join-Path $shared 'PostgreSQL';$child=Join-Path $postgres '18'
            foreach($path in @($shared,$postgres,$child)){[IO.Directory]::CreateDirectory($path) | Out-Null}
            $file=Join-Path $child 'sentinel';[IO.File]::WriteAllText($file,'unrelated child remains unchanged')
            $group=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $protected='O:BAG:'+$group+'D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)'
            foreach($path in @($root,$child,$file)){[AclFixtureSetup]::Write($path,$protected)}
            $control=Join-Path $root 'empty-junction-control';$target=Join-Path $root 'junction-target'
            foreach($path in @($control,$target)){[IO.Directory]::CreateDirectory($path) | Out-Null}
            [AclFixtureSetup]::Write($control,('O:BAG:'+$group+'D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;0x1301bf;;;AU)'))
            foreach($path in @($shared,$postgres)) {
                $group=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
                [AclFixtureSetup]::Write($path,('O:BAG:'+ $group +'D:(A;ID;FA;;;BA)(A;OICIIOID;GA;;;BA)(A;ID;FA;;;SY)(A;OICIIOID;GA;;;SY)(A;ID;0x1301bf;;;AU)(A;OICIIOID;SDGXGWGR;;;AU)(A;ID;0x1200a9;;;BU)(A;OICIIOID;GXGR;;;BU)'))
            }
            $paths=[string[]]@($shared,$postgres)
            $before=[string[]]@($paths | ForEach-Object {[AclFixtureSetup]::Read($_)})
            $deleteBefore=[AclFixtureSetup]::Probe($shared,$postgres,$child,$control,$target,$true)
            $childrenBefore=@([AclFixtureSetup]::Read($child),[AclFixtureSetup]::Read($file))
            $hash=(Get-FileHash -LiteralPath $file).Hash
            $after=[string[]]@(foreach($sddl in $before){
                $raw=[Security.AccessControl.RawSecurityDescriptor]::new($sddl)
                foreach($ace in $raw.DiscretionaryAcl){if($ace -is [Security.AccessControl.CommonAce] -and $ace.AceQualifier -eq 'AccessAllowed' -and
                    $ace.SecurityIdentifier.Value -eq 'S-1-5-11' -and -not($ace.AceFlags -band [Security.AccessControl.AceFlags]::InheritOnly)){$ace.AccessMask=$ace.AccessMask -band (-bnot 0x10000)}
                    $ace.AceFlags=$ace.AceFlags -band (-bnot [Security.AccessControl.AceFlags]::Inherited)
                }
                $raw.SetFlags($raw.ControlFlags -bor [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected)
                $raw.GetSddlForm('All')
            })
            $deleteHandle=[AclFixtureSetup]::HoldDelete($postgres);$conflictRefused=$false
            try{try{[FluxVault.Commissioning.PostgreSqlAncestorAcl]::Replace($paths,$before,$after)}catch{$conflictRefused=$true}}
            finally{$deleteHandle.Dispose()}
            $conflictUnchanged=[string]::Join('|',@($paths | ForEach-Object {[AclFixtureSetup]::Read($_)})) -ceq [string]::Join('|',$before)
            @(for($index=0;$index -lt 2;$index++){@{Path=$paths[$index];OriginalSddl=$before[$index];ProposedSddl=$after[$index]}})|
                ConvertTo-Json -Depth 4|Set-Content (Join-Path $root 'postgresql-ancestor-acl-proposal.json')
            $context=@{WorkRoot=$root}
            Set-CommissionAncestorState $context Apply
            $nativeAccess=[AclFixtureSetup]::Probe($shared,$postgres,$child,$control,$target,$false)
            if($nativeAccess[5] -eq 0){[AclFixtureSetup]::RemoveJunction($control)}
            $applied=[string[]]@($paths | ForEach-Object {[AclFixtureSetup]::Read($_)})
            $managedReordered=(Get-Acl -LiteralPath $paths[0]).Sddl -cne $after[0]
            $descriptorRefusals=@(foreach($change in @('rights','owner','group','protection','inheritance','extra-entry')){
                $wrong=[Security.AccessControl.RawSecurityDescriptor]::new($after[0])
                switch($change){
                    rights {foreach($ace in $wrong.DiscretionaryAcl){if($ace.SecurityIdentifier.Value -eq 'S-1-5-11' -and -not($ace.AceFlags -band [Security.AccessControl.AceFlags]::InheritOnly)){$ace.AccessMask=$ace.AccessMask -bor 0x10000}}}
                    owner {$wrong.Owner=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')}
                    group {$wrong.Group=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')}
                    protection {$wrong.SetFlags($wrong.ControlFlags -band (-bnot [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected))}
                    inheritance {$wrong.DiscretionaryAcl[0].AceFlags=[Security.AccessControl.AceFlags]::ObjectInherit}
                    extra-entry {$wrong.DiscretionaryAcl.InsertAce(0,[Security.AccessControl.CommonAce]::new('None','AccessAllowed',0x1200a9,[Security.Principal.SecurityIdentifier]::new('S-1-1-0'),$false,$null))}
                }
                -not(Test-CommissionAncestorDescriptor $paths[0] $wrong.GetSddlForm('All'))
            })
            $noDelete=@(foreach($path in $paths){$acl=Get-Acl -LiteralPath $path;@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
                $_.IdentityReference.Value -eq 'S-1-5-11' -and $_.AccessControlType -eq 'Allow' -and $_.PropagationFlags -ne 'InheritOnly' -and
                ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Delete)}).Count -eq 0})
            $childrenAfter=@([AclFixtureSetup]::Read($child),[AclFixtureSetup]::Read($file))
            $changedRefused=$false
            try{[FluxVault.Commissioning.PostgreSqlAncestorAcl]::Replace($paths,$before,$after)}catch{$changedRefused=$true}
            Set-CommissionAncestorState $context Restore
            Set-CommissionAncestorState $context Restore # Already-restored path must not attempt another replacement.
            $restored=[string[]]@($paths | ForEach-Object {[AclFixtureSetup]::Read($_)})
            @{AppliedExact=[string]::Join('|',$applied) -ceq [string]::Join('|',$after);
                RestoredExact=[string]::Join('|',$restored) -ceq [string]::Join('|',$before);NoDelete=@($noDelete);
                ChangedRefused=$changedRefused;ChildDescriptorsUnchanged=[string]::Join('|',$childrenBefore) -ceq [string]::Join('|',$childrenAfter);
                DeleteBefore=@($deleteBefore);NativeAccess=@($nativeAccess);ConflictRefused=$conflictRefused;ConflictUnchanged=$conflictUnchanged;
                ManagedReordered=$managedReordered;DescriptorRefusals=$descriptorRefusals;
                DataUnchanged=(Get-FileHash -LiteralPath $file).Hash -ceq $hash} | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("AppliedExact").GetBoolean(), result.ToString());
        Assert.True(result.GetProperty("ManagedReordered").GetBoolean());
        Assert.Equal(6, result.GetProperty("DescriptorRefusals").GetArrayLength());
        Assert.All(result.GetProperty("DescriptorRefusals").EnumerateArray(), value => Assert.True(value.GetBoolean()));
        Assert.True(result.GetProperty("RestoredExact").GetBoolean());
        Assert.True(result.GetProperty("ChangedRefused").GetBoolean());
        Assert.True(result.GetProperty("ChildDescriptorsUnchanged").GetBoolean());
        Assert.True(result.GetProperty("DataUnchanged").GetBoolean());
        Assert.True(result.GetProperty("ConflictRefused").GetBoolean());
        Assert.True(result.GetProperty("ConflictUnchanged").GetBoolean());
        Assert.Equal(new[] { 0, 0 }, result.GetProperty("DeleteBefore").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(new[] { 5, 5, 5, 5, 145, 0 }, result.GetProperty("NativeAccess").EnumerateArray().Select(value => value.GetInt32()));
        Assert.All(result.GetProperty("NoDelete").EnumerateArray(), value => Assert.True(value.GetBoolean()));
    }

    [Theory]
    [InlineData("round-trip")]
    [InlineData("collision")]
    [InlineData("changed-legacy")]
    [InlineData("escaping-path")]
    public async Task Rollback_preserves_legacy_bytes_and_ACLs_and_retains_fresh_state_without_overwriting(string scenario)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $helper=Join-Path (Split-Path (Split-Path (Split-Path $module))) 'docs/verification/2026-10-05-next002-rollout-preparation/commission-rollback.psm1'
            if(-not(Test-Path -LiteralPath $helper)){throw 'Checked rollback procedure is missing.'}
            Import-Module $helper -Force
            $scenario='SCENARIO_VALUE'
            $context=@{Parent=$root;InstallationId=$fixtureId;NormalInstallation=$false;ActiveRoot=(Join-Path $root 'FluxVault');
                RollbackRoot=(Join-Path $root ('FluxVault.Rollback.'+$fixtureId));FailedRoot=(Join-Path $root ('FluxVault.Failed.'+$fixtureId))}
            [IO.Directory]::CreateDirectory($context.ActiveRoot) | Out-Null
            [IO.File]::WriteAllText((Join-Path $context.ActiveRoot 'config.json'),'all legacy settings retained')
            [IO.File]::WriteAllBytes((Join-Path $context.ActiveRoot 'repository.bin'),[byte[]](0,255,16,13,10))
            $context.LegacyConfigSha256=(Get-FileHash -LiteralPath (Join-Path $context.ActiveRoot 'config.json')).Hash
            $dataHash=(Get-FileHash -LiteralPath (Join-Path $context.ActiveRoot 'repository.bin')).Hash;$sddl=(Get-Acl -LiteralPath $context.ActiveRoot).Sddl
            if($scenario -eq 'collision'){[IO.Directory]::CreateDirectory($context.RollbackRoot) | Out-Null;[IO.File]::WriteAllText((Join-Path $context.RollbackRoot 'sentinel'),'unrelated destination')}
            if($scenario -eq 'changed-legacy'){[IO.File]::AppendAllText((Join-Path $context.ActiveRoot 'config.json'),'external change')}
            if($scenario -eq 'escaping-path'){$context.RollbackRoot=Join-Path $parent 'outside'}
            $failure=$null;$preserved=$null;$restored=$null
            try {
                $preserved=Move-CommissionLegacyState $context Preserve
                [IO.Directory]::CreateDirectory($context.ActiveRoot) | Out-Null
                [IO.File]::WriteAllText((Join-Path $context.ActiveRoot 'fresh-state'),'preserve interrupted new state')
                $restored=Move-CommissionLegacyState $context Restore
            } catch {$failure=$_.Exception.Message}
            @{Failure=$failure;Success=$null -ne $restored;OriginalDataUnchanged=(Get-FileHash -LiteralPath (Join-Path $context.ActiveRoot 'repository.bin')).Hash -ceq $dataHash;
                OriginalAclUnchanged=(Get-Acl -LiteralPath $context.ActiveRoot).Sddl -ceq $sddl;
                FreshRetained=(Test-Path -LiteralPath (Join-Path $context.FailedRoot 'fresh-state'));
                CollisionRetained=$(if($scenario -eq 'collision'){[IO.File]::ReadAllText((Join-Path $context.RollbackRoot 'sentinel')) -ceq 'unrelated destination'}else{$true});
                EscapedAbsent=(-not(Test-Path -LiteralPath (Join-Path $parent 'outside')))} | ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        Assert.True(result.GetProperty("OriginalDataUnchanged").GetBoolean());
        Assert.True(result.GetProperty("OriginalAclUnchanged").GetBoolean());
        Assert.True(result.GetProperty("CollisionRetained").GetBoolean());
        Assert.True(result.GetProperty("EscapedAbsent").GetBoolean());
        var success = scenario == "round-trip";
        Assert.True(success == result.GetProperty("Success").GetBoolean(), result.ToString());
        Assert.Equal(success, result.GetProperty("FreshRetained").GetBoolean());
        if (!success) Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("Failure").GetString()));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("original-changed")]
    [InlineData("wrong-principal")]
    [InlineData("administrator-failed")]
    [InlineData("legacy-session")]
    [InlineData("administrator-survives")]
    [InlineData("extra-administrator")]
    [InlineData("administrator-still-admitted")]
    [InlineData("service-failed")]
    [InlineData("completion-publication-failed")]
    public async Task Authentication_procedure_retires_admission_before_activation_and_restores_exact_originals_on_failure(string scenario)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $preparation=Join-Path (Split-Path (Split-Path (Split-Path $module))) 'docs/verification/2026-10-05-next002-rollout-preparation'
            $helper=Join-Path $preparation 'commission-authentication.psm1'
            if(-not(Test-Path -LiteralPath $helper)){throw 'The finite authentication procedure is missing.'}
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'Authentication procedure has parser errors.'}
            foreach($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){
                . ([scriptblock]::Create($function.Extent.Text))
            }
            $scenario='SCENARIO_VALUE';$calls=[Collections.Generic.List[string]]::new()
            # Native boundaries are intercepted; real pinned files, byte backups,
            # hashing, ACL preservation, flushes and the procedure flow execute.
            function Assert-CommissionSystemWorker {}
            function Assert-CommissionPostmaster {param($Context)}
            function Read-PinnedAuthentication {param($Path)
                $stream=[IO.FileStream]::new($Path,'Open','Read','ReadWrite')
                $reader=[IO.StreamReader]::new($stream)
                try{return $reader.ReadToEnd()}finally{$reader.Dispose()}}
            function Invoke-CommissionTool {param($Context,$Executable,$Arguments,$InputText)
                if($Arguments[-1] -ne 'reload'){throw 'Unexpected native tool.'}
                $calls.Add('reload');return @{ExitCode=0;Joined=$true}}
            $hba=Join-Path $root 'pg_hba.conf';$ident=Join-Path $root 'pg_ident.conf'
            [IO.File]::WriteAllBytes($hba,[byte[]](239,187,191,35,111,108,100,13,10))
            [IO.File]::WriteAllText($ident,"# original map`r`n")
            $originalHba=[Convert]::ToBase64String([IO.File]::ReadAllBytes($hba));$originalIdent=[Convert]::ToBase64String([IO.File]::ReadAllBytes($ident))
            $hbaAcl=(Get-Acl -LiteralPath $hba).Sddl;$identAcl=(Get-Acl -LiteralPath $ident).Sddl
            $context=@{WorkRoot=$root;DataDirectory=$root;OperatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
                NormalInstallation=$false;Postmaster=@{};Port=5433;PsqlPath=(Get-Command pwsh).Source;PgCtlPath=(Get-Command pwsh).Source;
                SqlPath=(Join-Path $root 'create.sql');EmptyPasswordFile=(Join-Path $root 'empty.pgpass');ApplicationName='owned-commission';
                Connection='admin';LegacyConnection='legacy';ServiceConnection='service';Role='postgres';AdminMap='owned_map';
                LegacyRole='old_role';ServiceDatabase='owned_db';ServiceRole='owned_role';LogPath=(Join-Path $root 'postgres.log');
                OriginalHashes=@{'pg_hba.conf'=(Get-FileHash -LiteralPath $hba).Hash;'pg_ident.conf'=(Get-FileHash -LiteralPath $ident).Hash};Prepared=@{}}
            [IO.File]::WriteAllText($context.SqlPath,'inert');[IO.File]::WriteAllText($context.EmptyPasswordFile,'');[IO.File]::WriteAllText($context.LogPath,'')
            $context.SqlSha256=(Get-FileHash -LiteralPath $context.SqlPath).Hash
            foreach($name in @('FinalHba','FinalIdent','TemporaryHba','TemporaryIdent','ProbeIdent')) {
                $path=Join-Path $root ($name+'.txt');[IO.File]::WriteAllText($path,$name)
                $context.Prepared[$name]=@{Path=$path;Sha256=(Get-FileHash -LiteralPath $path).Hash}
            }
            if($scenario -eq 'original-changed'){[IO.File]::AppendAllText($hba,'concurrent change');$originalHba=[Convert]::ToBase64String([IO.File]::ReadAllBytes($hba))}
            function Invoke-CommissionQuery {param($Context,$Connection,$Sql,[switch]$AllowRefusal)
                $calls.Add($Connection)
                if($Connection -eq 'legacy' -and -not $Context.ContainsKey('AdministratorBaseline')){
                    return @{ExitCode=0;Joined=$true;Output='{"admin_pids":[777]}'}}
                $currentHba=Read-PinnedAuthentication $hba;$currentIdent=Read-PinnedAuthentication $ident
                if($Connection -eq 'admin') {
                    if($currentIdent -eq 'ProbeIdent') {
                        if($currentHba -ne 'TemporaryHba'){throw 'Principal probe did not use temporary HBA.'}
                        $principal=if($scenario -eq 'wrong-principal'){'another@NT AUTHORITY'}else{'SYSTEM@NT AUTHORITY'}
                        [IO.File]::AppendAllText($Context.LogPath,('no match in usermap "owned_map" for user "postgres" authenticated as "'+$principal+'"'))
                        return @{ExitCode=2;Output='';Joined=$true}
                    }
                    if($currentIdent -ne 'FinalIdent' -or $currentHba -ne 'FinalHba'){throw 'Administrator retirement probe preceded final authentication.'}
                    return @{ExitCode=$(if($scenario -eq 'administrator-still-admitted'){0}else{2});Output='';Joined=$true}
                }
                if($Connection -eq 'legacy' -and ($currentIdent -ne 'TemporaryIdent' -or $currentHba -ne 'TemporaryHba')){throw 'Legacy inspection did not precede trust retirement.'}
                if($Connection -eq 'service' -and ($currentIdent -ne 'FinalIdent' -or $currentHba -ne 'FinalHba')){throw 'Fresh service inspection preceded authentication retirement.'}
                if($Connection -eq 'service' -and $scenario -eq 'service-failed'){throw 'Service authentication refused.'}
                return @{ExitCode=0;Joined=$true;Output=(@{administrator_absent=$scenario -ne 'administrator-survives';
                    admin_pids=@(777;if($scenario -eq 'extra-administrator'){888});
                    legacy_pids=@(if($scenario -eq 'legacy-session'){42});database='owned_db';role='owned_role';port=5433} | ConvertTo-Json -Compress)}
            }
            function Invoke-CommissionAdministrator {param($Context)
                if((Read-PinnedAuthentication $ident) -ne 'TemporaryIdent'){throw 'Administrator executed outside its exact map.'}
                foreach($name in @('original-pg_hba.conf','original-pg_ident.conf','authentication-recovery.txt')){
                    if(-not(Test-Path -LiteralPath (Join-Path $root $name))){throw 'Administrator preceded retained restoration inputs.'}}
                $calls.Add('ddl');[IO.File]::WriteAllText((Join-Path $root 'partial-state'),'preserve this SQL effect')
                if($scenario -eq 'administrator-failed'){throw 'SQL failed after partial state.'}
                return @{WorkerJoined=$true;Backend=@{backend_pid=123}}
            }
            function Write-CommissionAuthenticationReceipt {param($Path,$Receipt)
                if($scenario -eq 'completion-publication-failed' -and (Split-Path $Path -Leaf) -eq 'authentication-completed.json'){throw 'Completion flush failed.'}
                [IO.File]::WriteAllText($Path,($Receipt | ConvertTo-Json -Depth 6))}
            $failure=$null;$reply=$null
            try{$reply=Invoke-CommissionAuthentication $context}catch{$failure=$_.Exception.Message}
            $finalHba=[Convert]::ToBase64String([IO.File]::ReadAllBytes($hba));$finalIdent=[Convert]::ToBase64String([IO.File]::ReadAllBytes($ident))
            @{Failure=$failure;Accepted=$null -ne $reply;Calls=@($calls);OriginalsRestored=$finalHba -ceq $originalHba -and $finalIdent -ceq $originalIdent;
                FinalActive=[IO.File]::ReadAllText($hba) -ceq 'FinalHba' -and [IO.File]::ReadAllText($ident) -ceq 'FinalIdent';
                AclsPreserved=(Get-Acl -LiteralPath $hba).Sddl -ceq $hbaAcl -and (Get-Acl -LiteralPath $ident).Sddl -ceq $identAcl;
                PartialStateRetained=(Test-Path -LiteralPath (Join-Path $root 'partial-state'));
                Activated=(Test-Path -LiteralPath (Join-Path $root 'authentication-completed.json'))} | ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        var success = scenario == "success";
        Assert.True(success == result.GetProperty("Accepted").GetBoolean(), result.ToString());
        Assert.Equal(success, result.GetProperty("Activated").GetBoolean());
        Assert.Equal(success, result.GetProperty("FinalActive").GetBoolean());
        Assert.Equal(!success, result.GetProperty("OriginalsRestored").GetBoolean());
        Assert.True(result.GetProperty("AclsPreserved").GetBoolean());
        var calls = result.GetProperty("Calls").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var effected = scenario is not ("original-changed" or "wrong-principal");
        Assert.True(effected == result.GetProperty("PartialStateRetained").GetBoolean(), result.ToString());
        Assert.Equal(effected ? 1 : 0, calls.Count(item => item == "ddl"));
        if (success) Assert.Equal(["legacy", "reload", "admin", "reload", "ddl", "legacy", "reload", "admin", "service"], calls);
        else Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("Failure").GetString()));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("wrong-binding")]
    [InlineData("publication-failure")]
    [InlineData("missing-backend")]
    [InlineData("sql-failure")]
    [InlineData("cancelled")]
    [InlineData("cancelled-after-open")]
    [InlineData("cancelled-after-effect")]
    public async Task Administrator_withholds_DDL_until_durable_backend_confirmation_and_always_joins(string scenario)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $helper=Join-Path (Split-Path (Split-Path (Split-Path $module))) 'docs/verification/2026-10-05-next002-rollout-preparation/commission-administrator.psm1'
            if(-not(Test-Path -LiteralPath $helper)){throw 'The bounded administrator helper is missing.'}
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'Administrator helper has parser errors.'}
            foreach($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){
                . ([scriptblock]::Create($function.Extent.Text))
            }
            $scenario='SCENARIO_VALUE'
            $fake=Join-Path $root 'fake-psql.ps1'
            @'
            $ErrorActionPreference='Stop'
            $scenario=$env:FV_SCENARIO;$root=$env:FV_ROOT;$publication=$null
            [IO.File]::WriteAllText((Join-Path $root 'child.pid'),$PID.ToString())
            while($null -ne ($line=[Console]::In.ReadLine())) {
                if($line -match "^\\o '([^']+)'$"){$publication=$Matches[1]}
                elseif($line -eq '\o') {
                    if($scenario -notin @('missing-backend','cancelled-after-open')) {
                        @{backend_pid=$PID;role=$(if($scenario -eq 'wrong-binding'){'another_role'}else{'postgres'});
                            database='postgres';data_directory=$root;port='5433'} |
                            ConvertTo-Json -Compress | Set-Content -LiteralPath $publication
                    }
                } elseif($line -eq 'CREATE ROLE owned_test;') {
                    $receipt=Join-Path $root 'administrator-session.json'
                    if(-not(Test-Path -LiteralPath $receipt)){throw 'DDL arrived before a durable receipt.'}
                    $confirmed=Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
                    if($confirmed.Backend.backend_pid -ne $PID){throw 'DDL receipt identifies another session.'}
                    [IO.File]::WriteAllText((Join-Path $root 'ddl.txt'),'effect after receipt')
                    if($scenario -eq 'cancelled-after-effect'){Start-Sleep -Seconds 30}
                    if($scenario -eq 'sql-failure'){exit 3}
                }
            }
            '@ | Set-Content -LiteralPath $fake
            $sql=Join-Path $root 'create.sql';[IO.File]::WriteAllText($sql,'CREATE ROLE owned_test;')
            $empty=Join-Path $root 'empty.pgpass';[IO.File]::WriteAllText($empty,'')
            $context=@{WorkRoot=$root;PsqlPath=(Get-Command pwsh).Source;SqlPath=$sql;SqlSha256=(Get-FileHash -LiteralPath $sql).Hash;
                ApplicationName='FluxVault.OwnedTest';EmptyPasswordFile=$empty;Connection='inert';DataDirectory=$root;
                Role='postgres';Database='postgres';Port=5433;PrefixArguments=@('-NoProfile','-NonInteractive','-File',$fake);
                Environment=@{FV_SCENARIO=$scenario;FV_ROOT=$root}}
            if($scenario -eq 'publication-failure') {
                function Write-CommissionAdministratorReceipt {throw 'Simulated durable publication failure.'}
            }
            $cancel=[Threading.CancellationTokenSource]::new()
            if($scenario -eq 'cancelled'){$cancel.Cancel()}
            $observer=$null
            if($scenario -in @('cancelled-after-open','cancelled-after-effect')) {
                Add-Type @'
            public static class CancelAtOwnedMarker {
                public static async System.Threading.Tasks.Task Run(string path, System.Threading.CancellationTokenSource source) {
                    var deadline=System.Diagnostics.Stopwatch.StartNew();
                    while(!System.IO.File.Exists(path)) {
                        if(deadline.Elapsed.TotalSeconds>5) throw new System.TimeoutException("Owned cancellation marker did not appear.");
                        await System.Threading.Tasks.Task.Delay(10);
                    }
                    source.Cancel();
                }
            }
            '@
                $marker=if($scenario -eq 'cancelled-after-open'){'child.pid'}else{'ddl.txt'}
                $observer=[CancelAtOwnedMarker]::Run((Join-Path $root $marker),$cancel)
            }
            $failure=$null;$reply=$null
            try {$reply=Invoke-CommissionAdministrator $context -PublicationTimeoutSeconds 2 -ExecutionTimeoutSeconds 10 -CancellationToken $cancel.Token}
            catch {$failure=$_.Exception.Message}
            finally {try{if($null -ne $observer){$null=$observer.GetAwaiter().GetResult()}}finally{$cancel.Dispose()}}
            $childPath=Join-Path $root 'child.pid';$launched=Test-Path -LiteralPath $childPath;$absent=$true
            if($launched){$childPid=[int][IO.File]::ReadAllText($childPath);$child=Get-Process -Id $childPid -ErrorAction SilentlyContinue;
                if($null -ne $child){try{$absent=$child.HasExited}finally{$child.Dispose()}}}
            @{Failure=$failure;Ddl=(Test-Path -LiteralPath (Join-Path $root 'ddl.txt'));
                Receipt=(Test-Path -LiteralPath (Join-Path $root 'administrator-session.json'));
                ChildLaunched=$launched;ChildAbsent=$absent;Joined=$(if($reply){$reply.WorkerJoined}else{$false})} | ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        Assert.True(result.GetProperty("ChildAbsent").GetBoolean());
        var succeeded = scenario == "success";
        var admitted = scenario is "success" or "sql-failure" or "cancelled-after-effect";
        Assert.Equal(admitted, result.GetProperty("Ddl").GetBoolean());
        Assert.Equal(admitted, result.GetProperty("Receipt").GetBoolean());
        Assert.Equal(scenario != "cancelled", result.GetProperty("ChildLaunched").GetBoolean());
        Assert.Equal(succeeded, result.GetProperty("Joined").GetBoolean());
        if (succeeded) Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
        else Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("Failure").GetString()));
        if (scenario.StartsWith("cancelled", StringComparison.Ordinal))
            Assert.Contains("cancel", result.GetProperty("Failure").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interrupted_effect_runner_refuses_invalid_batches_before_creating_any_fixture_resources()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $refusals=[Collections.Generic.List[bool]]::new()
            foreach($flags in @(@{RunInterruptedEffectTests=$true},
                @{RunInterruptedEffectTests=$true;RunSingleVaultTests=$true;RunRestartTests=$true},
                @{RunInterruptedEffectTests=$true;RunSingleVaultTests=$true;RunNativeAccessTests=$true},
                @{RunInterruptedEffectTests=$true;RunSingleVaultTests=$true;RunPackagedIdentityTests=$true})) {
                $refused=$false
                try {& $runner @flags -FixtureId $fixtureId -EvidenceDirectory (Join-Path $root 'unused')}
                catch {$refused=$_.Exception.Message -eq 'Interrupted-effect proof requires an exclusive single-vault extension within the existing resource bound.'}
                $refusals.Add($refused)
            }
            @{Refusals=@($refusals);RootEmpty=@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0;
                NoProgramDataRoot=(-not(Test-Path -LiteralPath (Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $fixtureId)))} | ConvertTo-Json -Compress
            """);
        Assert.All(result.GetProperty("Refusals").EnumerateArray(), item => Assert.True(item.GetBoolean()));
        Assert.True(result.GetProperty("RootEmpty").GetBoolean());
        Assert.True(result.GetProperty("NoProgramDataRoot").GetBoolean());
    }

    [Fact]
    public async Task A_retained_native_handle_reports_the_owned_process_termination_exit_code()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $start=[Diagnostics.ProcessStartInfo]::new('pwsh')
            $start.UseShellExecute=$false;$start.CreateNoWindow=$true
            foreach($argument in @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30')){$start.ArgumentList.Add($argument)}
            $child=[Diagnostics.Process]::Start($start)
            $observer=$null
            try {
                $identity=Get-VaultFixtureProcessIdentity $child
                $observer=Get-Process -Id $child.Id
                $null=$observer.Handle
                Stop-VaultFixtureProcess $identity
                if(-not $observer.WaitForExit(1000) -or -not $child.WaitForExit(1000)){throw 'Owned child did not exit.'}
                $nativeExit=$observer.ExitCode
                @{NativeHandleExit=$nativeExit;LaunchedHandleExit=$child.ExitCode;BothExited=$observer.HasExited -and $child.HasExited}|ConvertTo-Json -Compress
            } finally {
                if(-not $child.HasExited){Stop-VaultFixtureProcess (Get-VaultFixtureProcessIdentity $child)}
                if(-not $child.WaitForExit(5000)){throw 'Owned child remains.'}
                if($null -ne $observer){$observer.Dispose()};$child.Dispose()
            }
            """);
        Assert.Equal(-1, result.GetProperty("NativeHandleExit").GetInt32());
        Assert.Equal(-1, result.GetProperty("LaunchedHandleExit").GetInt32());
        Assert.True(result.GetProperty("BothExited").GetBoolean());
    }

    [Fact]
    public async Task Interrupted_effect_stop_gate_refuses_an_expired_or_wrong_operation_without_stopping_a_process()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$null)
            $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Stop-InterruptedServer'},$true)
            Invoke-Expression $definition.Extent.Text
            $fixtureRoot=$root;$FixtureId=$fixtureId;$actors=@{A='S-1-5-21-1-2-3-1001'}
            New-VaultFixtureProtectedDirectory (Join-Path $root 'runtime')
            New-VaultFixtureProtectedDirectory (Join-Path $root 'output-A')
            $op=[guid]::ParseExact($fixtureId,'N')
            $held=@{FixtureId=$fixtureId;Operation=$op;ActorSid=$actors.A;Revision=1;BackendPid=1;
                ObservedUtc=[DateTime]::UtcNow.ToString('o');LatestKillUtc=[DateTime]::UtcNow.AddSeconds(-1).ToString('o')}
            $checkpoint=@{FixtureId=$fixtureId;Request=@{OperationId=$op;ExpectedVaultRevision=1};Hash=('A'*64);SourceBytes=1}
            $checkpoint|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $root 'output-A/interrupted-checkpoint.json')
            $refusals=[Collections.Generic.List[bool]]::new()
            foreach($variant in @('Expired','WrongOperation')) {
                if($variant -eq 'WrongOperation'){$held.LatestKillUtc=[DateTime]::UtcNow.AddSeconds(5).ToString('o');$held.Operation=[guid]::NewGuid()}
                $held|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'runtime/effect-held.json')
                $refused=$false
                try{Stop-InterruptedServer -RunId $fixtureId -ReadyPath (Join-Path $root 'unused') -JobName 'unused'}
                catch{$refused=$_.Exception.Message -eq 'Interrupted evidence identity changed or its termination window was missed.'}
                $refusals.Add($refused)
            }
            $self=Get-Process -Id $PID
            try{@{Refusals=@($refusals);NativeSelfPreserved=(-not $self.HasExited)}|ConvertTo-Json -Compress}finally{$self.Dispose()}
            """);
        Assert.All(result.GetProperty("Refusals").EnumerateArray(), item => Assert.True(item.GetBoolean()));
        Assert.True(result.GetProperty("NativeSelfPreserved").GetBoolean());
    }

    [Fact]
    public async Task Restart_runner_refuses_invalid_batches_before_creating_any_fixture_resources()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $refusals=[Collections.Generic.List[bool]]::new()
            foreach($flags in @(@{RunRestartTests=$true},
                @{RunRestartTests=$true;RunSingleVaultTests=$true;RunNativeAccessTests=$true},
                @{RunRestartTests=$true;RunSingleVaultTests=$true;RunPackagedIdentityTests=$true})) {
                $refused=$false
                try {& $runner @flags -FixtureId $fixtureId -EvidenceDirectory (Join-Path $root 'unused')}
                catch {$refused=$_.Exception.Message -eq 'Restart proof requires an exclusive single-vault extension within the existing resource bound.'}
                $refusals.Add($refused)
            }
            @{Refusals=@($refusals);RootEmpty=@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0;
                NoProgramDataRoot=(-not(Test-Path -LiteralPath (Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $fixtureId)))} | ConvertTo-Json -Compress
            """);
        Assert.All(result.GetProperty("Refusals").EnumerateArray(), item => Assert.True(item.GetBoolean()));
        Assert.True(result.GetProperty("RootEmpty").GetBoolean());
        Assert.True(result.GetProperty("NoProgramDataRoot").GetBoolean());
    }

    [Fact]
    public async Task Restart_process_absence_gate_refuses_a_live_native_identity_without_stopping_it()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$null)
            $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-ProcessAbsent'},$true)
            Invoke-Expression $definition.Extent.Text
            $self=Get-Process -Id $PID
            try {
                $identity=Get-VaultFixtureProcessIdentity $self
                $refused=$false
                try {Assert-ProcessAbsent $identity} catch {$refused=$_.Exception.Message -eq 'Previous native server process remains.'}
                $wrong=$identity.Clone();$wrong.StartedUtc=([DateTimeOffset]$identity.StartedUtc).AddMinutes(-1).UtcDateTime.ToString('o')
                Assert-ProcessAbsent $wrong
                $self.Refresh()
                @{Refused=$refused;LiveIdentityPreserved=(-not $self.HasExited);OldStartDoesNotMatch=$true}|ConvertTo-Json -Compress
            } finally {$self.Dispose()}
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.True(result.GetProperty("LiveIdentityPreserved").GetBoolean());
        Assert.True(result.GetProperty("OldStartDoesNotMatch").GetBoolean());
    }

    [Fact]
    [Trait("Category", "RequiresPreparedWindowsInstallation")]
    public async Task Interrupted_native_key_import_is_recovered_without_deleting_an_unrelated_key()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path $module) 'packaged-identity.psm1') -Force
            Import-Module (Join-Path (Split-Path $module) 'fixture-cng-keys.psm1') -Force
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            New-VaultFixtureProtectedDirectory (Join-Path $root 'catalogue')
            New-VaultFixtureProtectedDirectory (Join-Path $root 'catalogue/package-signing')
            $provider=[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
            $unrelatedName='FluxVault.Unrelated.Test.'+$fixtureId
            $unrelated=$null
            $rsa=[Security.Cryptography.RSA]::Create(2048);$cert=$null;$imported=$null;$importedRsa=$null;$importedName=$null
            try {
                $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new(('CN=FluxVault Fixture '+$fixtureId),$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
                $cert=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
                $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo()))
                $intent=New-VaultFixtureKeyImportIntent $journal $fingerprint
                # A concurrent unrelated import after the baseline must also survive.
                $unrelated=[Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::Rsa,$unrelatedName)
                # Native Windows PFX import persists the key, just as SignTool does.
                # No result/Created state is published before reopening the intent.
                $imported=[Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12($cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,''),'',[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet)
                $importedRsa=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($imported)
                $importedName=$importedRsa.Key.KeyName
                $importedRsa.Dispose();$importedRsa=$null;$imported.Dispose();$imported=$null
                $before=Get-Content -LiteralPath $intent -Raw|ConvertFrom-Json
                Remove-VaultFixtureImportedKeys (Read-VaultFixtureJournal $root $parent $fixtureId)
                $after=Get-Content -LiteralPath $intent -Raw|ConvertFrom-Json
                @{Before=$before.State;After=$after.State;OwnedCount=$after.OwnedKeys.Count;
                    OwnedAbsent=(-not[Security.Cryptography.CngKey]::Exists($importedName,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent));
                    UnrelatedPreserved=[Security.Cryptography.CngKey]::Exists($unrelatedName,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent)}|ConvertTo-Json -Compress
            } finally {
                if($importedRsa){$importedRsa.Dispose()};if($imported){$imported.Dispose()}
                if($importedName -and [Security.Cryptography.CngKey]::Exists($importedName,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent)){
                    $left=[Security.Cryptography.CngKey]::Open($importedName,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent);try{$left.Delete()}finally{$left.Dispose()}
                }
                if($unrelated){$unrelated.Delete();$unrelated.Dispose()};if($cert){$cert.Dispose()};$rsa.Dispose()
            }
            """);
        Assert.Equal("Intent", result.GetProperty("Before").GetString());
        Assert.Equal("Removed", result.GetProperty("After").GetString());
        Assert.Equal(1, result.GetProperty("OwnedCount").GetInt32());
        Assert.True(result.GetProperty("OwnedAbsent").GetBoolean());
        Assert.True(result.GetProperty("UnrelatedPreserved").GetBoolean());
    }

    [Theory]
    [InlineData("DeveloperMode")]
    [InlineData("Sideload")]
    [InlineData("RunnerTrustedPeople")]
    [InlineData("MachineTrustedPeople")]
    public async Task Installation_snapshot_detects_package_trust_or_policy_changes(string changedField)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $snapshot=@{Services=@();Files=@();PackageBoundary=@{DeveloperModePresent=$true;DeveloperMode=0;SideloadPresent=$true;Sideload=0;
                RunnerTrustedPeople=@('A');MachineTrustedPeople=@('B')}}
            $path=Join-Path $root 'snapshot.json';$snapshot|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $path
            $expected=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
            $same=Test-VaultFixtureInstallationUnchanged $expected $snapshot
            $field='FIELD'
            $snapshot.PackageBoundary[$field]=$(if($field.EndsWith('TrustedPeople')){@('unrelated changed thumbprint')}else{1})
            @{Same=$same;Changed=(Test-VaultFixtureInstallationUnchanged $expected $snapshot)}|ConvertTo-Json -Compress
            """.Replace("FIELD", changedField, StringComparison.Ordinal));
        Assert.True(result.GetProperty("Same").GetBoolean());
        Assert.False(result.GetProperty("Changed").GetBoolean());
    }

    [Fact]
    public async Task SDK_snapshot_refuses_unsigned_copied_dependencies_before_tool_execution()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path $module) 'packaged-identity.psm1') -Force
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            $source=Join-Path $root 'untrusted-sdk';$null=New-Item -ItemType Directory $source
            [IO.File]::WriteAllText((Join-Path $source 'appxpackaging.dll'),'unsigned payload')
            $refused=$false
            try {New-VaultFixtureSdkSnapshot $journal $source | Out-Null} catch {$refused=$_.Exception.Message -eq 'Copied SDK signature or tool identity is not the reviewed Microsoft payload.'}
            $copied=Join-Path $root 'package-sdk/appxpackaging.dll'
            @{Refused=$refused;CopyExists=(Test-Path -LiteralPath $copied);CopiedBytes=[IO.File]::ReadAllText($copied);
                NoKey=(-not(Test-Path -LiteralPath (Join-Path $root 'catalogue/package-signing/ephemeral-key.pfx')))}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.True(result.GetProperty("CopyExists").GetBoolean());
        Assert.Equal("unsigned payload", result.GetProperty("CopiedBytes").GetString());
        Assert.True(result.GetProperty("NoKey").GetBoolean());
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Fingerprint")]
    [InlineData("Provider")]
    public async Task Machine_trust_refuses_private_key_ownership_mismatch_without_publication(string fault)
    {
        var result = await RunMachineTrustCaseAsync("""
            switch('FAULT_VALUE') {
                Owner {$keys.OwnerSid='S-1-5-21-1-2-3-9199'}
                Fingerprint {$keys.PublicFingerprint='0'*64}
                Provider {$keys.Provider='A different provider'}
            }
            $keys|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $keysPath
            $failure=$null;try{Invoke-VaultFixtureMachineTrust $journal Add -Approved}catch{$failure=$_.Exception.Message}
            @{Failure=$failure;Adds=$fakeStore.Adds;OwnerExists=(Test-Path -LiteralPath $ownerPath)}|ConvertTo-Json -Compress
            """.Replace("FAULT_VALUE", fault, StringComparison.Ordinal));
        Assert.Equal("Signing private keys have not been retired.", result.GetProperty("Failure").GetString());
        Assert.Equal(0, result.GetProperty("Adds").GetInt32());
        Assert.False(result.GetProperty("OwnerExists").GetBoolean());
    }

    [Fact]
    public async Task Machine_trust_refuses_a_signing_key_that_still_exists_despite_a_retired_record()
    {
        var result = await RunMachineTrustCaseAsync("""
            $parameters=[Security.Cryptography.CngKeyCreationParameters]::new()
            $parameters.Provider=[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
            $keyName='{'+[guid]::NewGuid().ToString()+'}'
            $nativeKey=[Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::Rsa,$keyName,$parameters)
            try {
                $keys.OwnedKeys=@(@{KeyName=$nativeKey.KeyName;UniqueName=$nativeKey.UniqueName;PublicFingerprint=$keys.PublicFingerprint;State='Removed'})
                $keys|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $keysPath
                $failure=$null;try{Invoke-VaultFixtureMachineTrust $journal Add -Approved}catch{$failure=$_.Exception.Message}
                @{Failure=$failure;Adds=$fakeStore.Adds;OwnerExists=(Test-Path -LiteralPath $ownerPath)}|ConvertTo-Json -Compress
            } finally {
                $nativeKey.Delete();$nativeKey.Dispose()
                if([Security.Cryptography.CngKey]::Exists($keyName,$parameters.Provider)){throw 'Test-owned signing key remains.'}
            }
            """);
        Assert.Equal("Signing private keys have not been retired.", result.GetProperty("Failure").GetString());
        Assert.Equal(0, result.GetProperty("Adds").GetInt32());
        Assert.False(result.GetProperty("OwnerExists").GetBoolean());
    }

    [Fact]
    public async Task Machine_trust_recovery_refuses_reappearance_after_retirement()
    {
        var result = await RunMachineTrustCaseAsync("""
            Invoke-VaultFixtureMachineTrust $journal Add -Approved
            Invoke-VaultFixtureMachineTrust $journal Remove
            $fakeStore.Certificates.Add($public)|Out-Null
            $failure=$null;try{Invoke-VaultFixtureMachineTrust $journal Remove}catch{$failure=$_.Exception.Message}
            @{Failure=$failure;Removes=$fakeStore.Removes;Certificates=$fakeStore.Certificates.Count}|ConvertTo-Json -Compress
            """);
        Assert.Equal("A retired machine certificate reappeared; preserve it for review.", result.GetProperty("Failure").GetString());
        Assert.Equal(1, result.GetProperty("Removes").GetInt32());
        Assert.Equal(2, result.GetProperty("Certificates").GetInt32());
    }

    [Fact]
    public async Task Machine_trust_recovers_a_lost_import_acknowledgement_from_its_real_ownership_record()
    {
        var result = await RunMachineTrustCaseAsync("""
            $fakeStore.FailAfterAdd=$true
            $failure=$null;try{Invoke-VaultFixtureMachineTrust $journal Add -Approved}catch{$failure=$_.Exception.Message}
            $before=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json
            Invoke-VaultFixtureMachineTrust $journal Remove
            $after=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json
            @{Failure=$failure;Before=$before.State;After=$after.State;Adds=$fakeStore.Adds;Removes=$fakeStore.Removes;
                Remaining=$fakeStore.Certificates.Count;UnrelatedPreserved=($fakeStore.Certificates[0].Thumbprint -eq $unrelated.Thumbprint)}|ConvertTo-Json -Compress
            """);
        Assert.Contains("Lost import acknowledgement.", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
        Assert.Equal("Intent", result.GetProperty("Before").GetString());
        Assert.Equal("Removed", result.GetProperty("After").GetString());
        Assert.Equal(1, result.GetProperty("Adds").GetInt32());
        Assert.Equal(1, result.GetProperty("Removes").GetInt32());
        Assert.Equal(1, result.GetProperty("Remaining").GetInt32());
        Assert.True(result.GetProperty("UnrelatedPreserved").GetBoolean());
    }

    [Fact]
    public async Task Installation_snapshot_preserves_current_bootstrap_and_detects_creation_of_legacy_configuration()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$null)
            $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-InstallationSnapshot'},$true)
            Invoke-Expression $definition.Extent.Text
            function Get-CimInstance {param($ClassName,$Filter) @{State='Running';ProcessId=$PID;StartName='Preserved';PathName='Preserved'}}
            $script:legacyExists=$false;$script:bootstrapHash='B'*64
            function Test-Path {
                param($LiteralPath)
                if($LiteralPath -eq 'C:\ProgramData\FluxVault\config.json'){return $script:legacyExists}
                if($LiteralPath -eq 'C:\ProgramData\FluxVault\installation.json'){return $true}
                throw 'Unexpected snapshot existence check.'
            }
            function Get-FileHash {
                param($LiteralPath,$Algorithm)
                if($LiteralPath -eq 'C:\ProgramData\FluxVault\config.json' -and -not $script:legacyExists){throw 'Legacy configuration is absent.'}
                @{Hash=$(if($LiteralPath.EndsWith('installation.json')){$script:bootstrapHash}else{'A'*64})}
            }
            $RunPackagedIdentityTests=$false;$fixtureJournal=$null;$fixtureBefore=$null
            $before=Get-InstallationSnapshot
            $same=Test-VaultFixtureInstallationUnchanged $before (Get-InstallationSnapshot)
            $script:bootstrapHash='C'*64
            $changedBootstrap=Test-VaultFixtureInstallationUnchanged $before (Get-InstallationSnapshot)
            $script:bootstrapHash='B'*64;$script:legacyExists=$true
            $createdLegacy=Test-VaultFixtureInstallationUnchanged $before (Get-InstallationSnapshot)
            @{Same=$same;ChangedBootstrap=$changedBootstrap;CreatedLegacy=$createdLegacy}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Same").GetBoolean());
        Assert.False(result.GetProperty("ChangedBootstrap").GetBoolean());
        Assert.False(result.GetProperty("CreatedLegacy").GetBoolean());
    }

    [Fact]
    public async Task Machine_trust_interrupted_before_profile_intents_retains_reopened_boundary_verification()
    {
        var result = await RunMachineTrustCaseAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$null)
            $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-InstallationSnapshot'},$true)
            Invoke-Expression $definition.Extent.Text
            # Native machine/store checks are substituted. Run the actual snapshot
            # function over the real reopened baseline and ownership journal.
            function Get-CimInstance {param($ClassName,$Filter) @{State='Running';ProcessId=$PID;StartName='Preserved';PathName='Preserved'}}
            function Get-FileHash {
                param($LiteralPath,$Algorithm='SHA256')
                if($LiteralPath -in @('D:\Program Files\PostgreSQL\18\data\pg_hba.conf','D:\Program Files\PostgreSQL\18\data\pg_ident.conf','C:\ProgramData\FluxVault\config.json','C:\ProgramData\FluxVault\installation.json')){@{Hash='A'*64}}
                else {Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm}
            }
            function Get-ChildItem {
                param($Path)
                if($Path -eq 'Cert:\LocalMachine\TrustedPeople'){$fakeStore.Certificates}
                elseif($Path -eq 'Cert:\CurrentUser\TrustedPeople'){[pscustomobject]@{Thumbprint='Retained runner trust'}}
                else{throw 'Unexpected snapshot enumeration.'}
            }
            $RunPackagedIdentityTests=$true;$fixtureJournal=$journal;$fixtureBefore=$null
            $before=Get-InstallationSnapshot
            $beforePath=Join-Path $root 'before.json';$before|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $beforePath
            $fakeStore.FailAfterAdd=$true
            $failure=$null;try{Invoke-VaultFixtureMachineTrust $journal Add -Approved}catch{$failure=$_.Exception.Message}
            $intent=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json
            $RunPackagedIdentityTests=$false
            $fixtureJournal=Read-VaultFixtureJournal $root $parent $fixtureId
            $fixtureBefore=Get-Content -LiteralPath $beforePath -Raw|ConvertFrom-Json
            Invoke-VaultFixtureMachineTrust $fixtureJournal Remove
            $after=Get-InstallationSnapshot
            $included=$after.ContainsKey('PackageBoundary')
            $same=$false;$changed=$false
            if($included){
                $same=Test-VaultFixtureInstallationUnchanged $fixtureBefore $after
                $fakeStore.Certificates.Add($public)|Out-Null
                $changed=Test-VaultFixtureInstallationUnchanged $fixtureBefore (Get-InstallationSnapshot)
            }
            @{Failure=$failure;Intent=$intent.State;ProfileIntents=@($fixtureJournal.Resources|Where-Object Kind -eq 'PackageUser').Count;
                BoundaryIncluded=$included;Same=$same;Changed=$changed;Removes=$fakeStore.Removes}|ConvertTo-Json -Compress
            """);
        Assert.Contains("Lost import acknowledgement.", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
        Assert.Equal("Intent", result.GetProperty("Intent").GetString());
        Assert.Equal(0, result.GetProperty("ProfileIntents").GetInt32());
        Assert.True(result.GetProperty("BoundaryIncluded").GetBoolean());
        Assert.True(result.GetProperty("Same").GetBoolean());
        Assert.False(result.GetProperty("Changed").GetBoolean());
        Assert.Equal(1, result.GetProperty("Removes").GetInt32());
    }

    [Theory]
    [InlineData("Approval", "Explicit machine-certificate scope approval is required.")]
    [InlineData("Collision", "Existing machine certificate is not owned; no trust was changed.")]
    [InlineData("Bytes", "Machine trust public certificate bytes changed.")]
    [InlineData("RecoveryHash", "Machine certificate identity changed; preserve it for review.")]
    public async Task Machine_trust_refusals_preserve_unowned_certificates(string fault, string expected)
    {
        var result = await RunMachineTrustCaseAsync("""
            $fault='FAULT_VALUE';$failure=$null
            if($fault -eq 'Collision'){$fakeStore.Certificates.Add($public)|Out-Null}
            if($fault -eq 'Bytes'){[IO.File]::WriteAllText($certificatePath,'changed public bytes')}
            if($fault -eq 'RecoveryHash') {
                Invoke-VaultFixtureMachineTrust $journal Add -Approved
                $record=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json -AsHashtable
                $record.CertificateSha256='0'*64;Write-VaultFixturePackageRecord $ownerPath $record
            }
            try {
                if($fault -eq 'RecoveryHash'){Invoke-VaultFixtureMachineTrust $journal Remove}
                else{Invoke-VaultFixtureMachineTrust $journal Add -Approved:($fault -ne 'Approval')}
            }catch{$failure=$_.Exception.Message}
            @{Failure=$failure;Adds=$fakeStore.Adds;Removes=$fakeStore.Removes;
                UnrelatedPreserved=($fakeStore.Certificates[0].Thumbprint -eq $unrelated.Thumbprint)}|ConvertTo-Json -Compress
            """.Replace("FAULT_VALUE", fault, StringComparison.Ordinal));
        Assert.Equal(expected, result.GetProperty("Failure").GetString());
        Assert.Equal(fault == "RecoveryHash" ? 1 : 0, result.GetProperty("Adds").GetInt32());
        Assert.Equal(0, result.GetProperty("Removes").GetInt32());
        Assert.True(result.GetProperty("UnrelatedPreserved").GetBoolean());
    }

    private static async Task<JsonElement> RunMachineTrustCaseAsync(string body)
    {
        using var fixture = new ScriptFixture();
        return await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path $module) 'packaged-identity.psm1') -Force
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            New-VaultFixtureProtectedDirectory (Join-Path $root 'runtime')
            New-VaultFixtureProtectedDirectory (Join-Path $root 'catalogue')
            New-VaultFixtureProtectedDirectory (Join-Path $root 'catalogue/package-signing')
            $rsa=[Security.Cryptography.RSA]::Create(2048)
            $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new(('CN=FluxVault Fixture '+$fixtureId),$rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
            $usages=[Security.Cryptography.OidCollection]::new();$null=$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages,$false))
            $signed=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
            $certificatePath=Join-Path $root 'runtime/identity.cer';[IO.File]::WriteAllBytes($certificatePath,$signed.RawData)
            $public=[Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificate($signed.RawData)
            $unrelatedRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Preserve unrelated test leaf',$rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $unrelated=$unrelatedRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
            $metadata=@{Version=1;FixtureId=$fixtureId;Root=$root;PackageName=('FVGate.Package.'+$fixtureId);Publisher=$public.Subject;
                Thumbprint=$public.Thumbprint;CertificateSha256=(Get-FileHash -LiteralPath $certificatePath).Hash}
            $metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'runtime/package-identity.json')
            $keys=@{FixtureId=$fixtureId;OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
                Provider='Microsoft Software Key Storage Provider';PublicFingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo()));State='Removed';OwnedKeys=@()}
            $keysPath=Join-Path $root 'catalogue/package-signing/native-key-cleanup.json';$keys|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $keysPath
            $ownerPath=Join-Path $root 'catalogue/package-signing/machine-trust-owner.json'
            # Substitute only the native store boundary. Files, ownership receipts,
            # certificates and hash checks are real; this is not native trust proof.
            $fakeStore=[pscustomobject]@{Certificates=[Security.Cryptography.X509Certificates.X509Certificate2Collection]::new();Adds=0;Removes=0;FailAfterAdd=$false}
            $fakeStore.Certificates.Add($unrelated)|Out-Null
            $fakeStore|Add-Member ScriptMethod Open {param($flags)}
            $fakeStore|Add-Member ScriptMethod Dispose {}
            $fakeStore|Add-Member ScriptMethod Add {param($certificate) $this.Adds++;$this.Certificates.Add([Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificate($certificate.RawData))|Out-Null;if($this.FailAfterAdd){throw 'Lost import acknowledgement.'}}
            $fakeStore|Add-Member ScriptMethod Remove {param($certificate) $this.Removes++;$this.Certificates.Remove($certificate)}
            $factory={return $fakeStore}.GetNewClosure()
            & (Get-Module packaged-identity) {param($factory) Set-Item function:script:New-VaultFixtureMachineTrustStore $factory} $factory
            try {
            """ + body + """
            } finally {foreach($item in $fakeStore.Certificates){$item.Dispose()};$public.Dispose();$signed.Dispose();$unrelated.Dispose();$rsa.Dispose()}
            """);
    }

    [Fact]
    public async Task Packaged_launcher_refuses_a_root_outside_its_fixed_fixture_parent()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $launcher=Join-Path (Split-Path $module) 'invoke-packaged-identity.ps1'
            $failure=$null
            try {& $launcher -Root $root -Actor A -Action Cleanup | Out-Null} catch {$failure=$_.Exception.Message}
            @{Failure=$failure}|ConvertTo-Json -Compress
            """);
        Assert.Equal("Packaged actor fixture root mismatch.", result.GetProperty("Failure").GetString());
    }

    [Theory]
    [InlineData("Sid")]
    [InlineData("Hash")]
    public async Task Packaged_launcher_refuses_wrong_native_SID_or_payload_hash_before_profile_effects(string fault)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $launcher=Join-Path (Split-Path $module) 'invoke-packaged-identity.ps1'
            $canonical=Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $fixtureId
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $actors=@{A=$sid;B='S-1-5-21-1-2-3-9199'}
            if('FAULT' -eq 'Sid'){$actors.A=$actors.B}
            @{FixtureId=$fixtureId;Root=$canonical;Actors=$actors}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $root 'database-probe.json')
            $metadata=@{Version=1;FixtureId=$fixtureId;Root=$canonical;PackageName=('FVGate.Package.'+$fixtureId);
                Publisher=('CN=FluxVault Fixture '+$fixtureId);Thumbprint=('A'*40)}
            foreach($pair in @(@{Name='identity.msix';Field='PackageSha256'},@{Name='identity.cer';Field='CertificateSha256'},@{Name='FluxVault.TestHost.exe';Field='ApphostSha256'})) {
                $path=Join-Path $root $pair.Name;[IO.File]::WriteAllText($path,'real fixture payload '+$pair.Name)
                $metadata[$pair.Field]=(Get-FileHash -LiteralPath $path).Hash
            }
            if('FAULT' -eq 'Hash'){$metadata.PackageSha256='0'*64}
            $metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'package-identity.json')
            # Map only read-only payload paths to the real current-user test files.
            # Native traversal-only A/B admission is proved separately by the owned Windows run.
            $fixtureReadRoot=$root
            function Get-Content {param($LiteralPath,[switch]$Raw) Microsoft.PowerShell.Management\Get-Content -LiteralPath (Join-Path $fixtureReadRoot ([IO.Path]::GetFileName($LiteralPath))) -Raw:$Raw}
            function Get-FileHash {param($LiteralPath) Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath (Join-Path $fixtureReadRoot ([IO.Path]::GetFileName($LiteralPath)))}
            $failure=$null
            try {& $launcher -Root $canonical -Actor A -Action Cleanup | Out-Null} catch {$failure=$_.Exception.Message}
            @{Failure=$failure;NoProfileOwner=(-not(Test-Path -LiteralPath (Join-Path $root 'package-owner.json')))}|ConvertTo-Json -Compress
            """.Replace("FAULT", fault, StringComparison.Ordinal));
        Assert.Equal(fault == "Sid" ? "Packaged fixture identity mismatch." : "Packaged fixture payload changed.",
            result.GetProperty("Failure").GetString());
        Assert.True(result.GetProperty("NoProfileOwner").GetBoolean());
    }

    [Theory]
    [InlineData("MachineParent", "Client", "None", "Reached registration boundary.", 0, 0)]
    [InlineData("MachineParent", "Client", "Missing", "Machine-parent certificate publication was not verified.", 0, 0)]
    [InlineData("MachineParent", "Client", "Changed", "Machine-parent certificate publication was not verified.", 0, 0)]
    [InlineData("MachineParent", "Client", "Package", "Owned package collision; no existing resource was changed.", 0, 0)]
    [InlineData("MachineParent", "Client", "Mode", "Packaged fixture trust mode mismatch.", 0, 0)]
    [InlineData("MachineParent", "Cleanup", "Missing", "", 0, 0)]
    [InlineData("MachineParent", "Cleanup", "Registered", "", 0, 0)]
    [InlineData("MachineParent", "Cleanup", "OwnerMode", "Package cleanup ownership mismatch.", 0, 0)]
    [InlineData("MachineParent", "Cleanup", "None", "Parent-owned effective certificate trust remains.", 0, 0)]
    [InlineData("PerUser", "Client", "Missing", "Reached registration boundary.", 1, 1)]
    [InlineData("PerUser", "Client", "None", "Owned per-user certificate collision; no existing resource was changed.", 0, 0)]
    public async Task Packaged_launcher_observes_trust_ownership_through_its_actual_flow(
        string mode, string action, string fault, string expectedFailure, int adds, int removes)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path $module) 'packaged-identity.psm1') -Force
            $launcher=Join-Path (Split-Path $module) 'invoke-packaged-identity.ps1'
            $canonical=Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $fixtureId
            $caseRoot=$root
            $null=New-Item -ItemType Directory -Path (Join-Path $root 'runtime'),(Join-Path $root 'output-A')
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            @{FixtureId=$fixtureId;Root=$canonical;Actors=@{A=$sid};PackageTrustMode='MODE'} |
                ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $root 'runtime/database-probe.json')
            $rsa=[Security.Cryptography.RSA]::Create(2048)
            $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new(('CN=FluxVault Fixture '+$fixtureId),$rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $signed=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
            $cer=Join-Path $root 'runtime/identity.cer'
            [IO.File]::WriteAllBytes($cer,$signed.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            $public=[Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($cer)
            [IO.File]::WriteAllText((Join-Path $root 'runtime/identity.msix'),'not installed')
            [IO.File]::WriteAllText((Join-Path $root 'runtime/FluxVault.TestHost.exe'),'not executed')
            $metadata=@{Version=1;FixtureId=$fixtureId;Root=$canonical;PackageName=('FVGate.Package.'+$fixtureId);
                Publisher=$public.Subject;Thumbprint=$public.Thumbprint;TrustMode='MODE'}
            if('FAULT' -eq 'Mode'){$metadata.TrustMode='Unknown'}
            foreach($pair in @(@{Name='identity.msix';Field='PackageSha256'},@{Name='identity.cer';Field='CertificateSha256'},@{Name='FluxVault.TestHost.exe';Field='ApphostSha256'})) {
                $metadata[$pair.Field]=(Get-FileHash -LiteralPath (Join-Path $root ('runtime/'+$pair.Name))).Hash
            }
            $metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'runtime/package-identity.json')
            if('ACTION' -eq 'Cleanup') {
                $owner=@{Version=1;FixtureId=$fixtureId;UserSid=$sid;PackageName=$metadata.PackageName;Publisher=$metadata.Publisher;
                    Thumbprint=$metadata.Thumbprint;TrustMode='MODE';State='PackageCreated'}
                if('FAULT' -eq 'OwnerMode'){$owner.TrustMode='PerUser'}
                Write-VaultFixturePackageRecord (Join-Path $root 'output-A/package-owner.json') $owner
            }
            # Substitute only native certificate/package APIs and map the fixed
            # read/write paths into this real isolated fixture. Run the full launcher.
            $fakeStore=[pscustomobject]@{Items=[Security.Cryptography.X509Certificates.X509Certificate2Collection]::new();Adds=0;Removes=0;Opened=[Collections.Generic.List[string]]::new()}
            $fakeStore|Add-Member ScriptProperty Certificates {
                $snapshot=[Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
                foreach($item in $this.Items){$null=$snapshot.Add([Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificate($item.RawData))}
                return ,$snapshot
            }
            $changed=$null
            if('FAULT' -eq 'Changed') {
                $otherRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Different leaf',$rsa,
                    [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
                $changed=$otherRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
                $null=$fakeStore.Items.Add($changed)
            } elseif('FAULT' -notin @('Missing','Registered')){$null=$fakeStore.Items.Add($public)}
            $fakeStore|Add-Member ScriptMethod Open {param($flags) $this.Opened.Add($flags.ToString())}
            $fakeStore|Add-Member ScriptMethod Dispose {}
            $fakeStore|Add-Member ScriptMethod Add {param($certificate) $this.Adds++;$null=$this.Items.Add([Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificate($certificate.RawData))}
            $fakeStore|Add-Member ScriptMethod Remove {param($certificate) $this.Removes++;foreach($item in @($this.Items|Where-Object Thumbprint -eq $certificate.Thumbprint)){$this.Items.Remove($item);$item.Dispose()}}
            $factory={return $fakeStore}.GetNewClosure()
            & (Get-Module packaged-identity) {param($factory) Set-Item function:script:New-VaultFixtureUserTrustStore $factory} $factory
            # Imports were performed above so the isolated native boundary survives.
            function Import-Module {param($Name,[switch]$Force)}
            function Join-Path {
                param($Path,$ChildPath)
                if($Path -eq $canonical){$Path=$caseRoot}
                Microsoft.PowerShell.Management\Join-Path $Path $ChildPath
            }
            $nativeCalls=[Collections.Generic.List[string]]::new()
            $packageState=@{Present=('FAULT' -in @('Package','Registered'))}
            function Get-AppxPackage {
                param($Name)
                if($packageState.Present){[pscustomobject]@{Publisher=$metadata.Publisher;PackageFullName=($metadata.PackageName+'_1.0.0.0_x64__collision')}}
            }
            function Add-AppxPackage {param($Path,$ExternalLocation,$ErrorAction) $nativeCalls.Add('Register');throw 'Reached registration boundary.'}
            function Remove-AppxPackage {param($Package,$ErrorAction) $nativeCalls.Add('Remove');$packageState.Present=$false}
            $failure='';$reply=$null
            try { $reply=& $launcher -Root $canonical -Actor A -Action 'ACTION' | ConvertFrom-Json }
            catch { $failure=$_.Exception.Message.Split(' Certificate publication:')[0] }
            $ownerPath=Join-Path $caseRoot 'output-A/package-owner.json'
            $owner=if(Test-Path -LiteralPath $ownerPath){Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json}else{$null}
            @{Failure=$failure;Adds=$fakeStore.Adds;Removes=$fakeStore.Removes;OpenFlags=@($fakeStore.Opened);
                NativeCalls=@($nativeCalls);Owner=$owner;Reply=$reply}|ConvertTo-Json -Depth 8 -Compress
            if($changed){$changed.Dispose()};$public.Dispose();$signed.Dispose();$rsa.Dispose()
            """.Replace("MODE", mode, StringComparison.Ordinal).Replace("FAULT", fault, StringComparison.Ordinal)
                .Replace("ACTION", action, StringComparison.Ordinal));
        Assert.Equal(expectedFailure, result.GetProperty("Failure").GetString());
        Assert.Equal(adds, result.GetProperty("Adds").GetInt32());
        Assert.Equal(removes, result.GetProperty("Removes").GetInt32());
        var registrations = result.GetProperty("NativeCalls").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(expectedFailure == "Reached registration boundary." ? new[] { "Register" }
            : fault == "Registered" ? ["Remove"] : [], registrations);
        if (mode == "MachineParent")
            Assert.DoesNotContain("ReadWrite", result.GetProperty("OpenFlags").EnumerateArray().Select(x => x.GetString()));
        if (expectedFailure == "Reached registration boundary.")
        {
            var owner = result.GetProperty("Owner");
            Assert.Equal(mode, owner.GetProperty("TrustMode").GetString());
            Assert.True(owner.GetProperty("TrustProof").GetProperty("ReopenedVerified").GetBoolean());
            Assert.Equal(mode == "PerUser", owner.GetProperty("CertificateRemoved").GetBoolean());
            Assert.Equal(mode == "PerUser", owner.GetProperty("EffectiveTrustAbsent").GetBoolean());
        }
        if (action == "Cleanup" && fault is "Missing" or "Registered")
        {
            Assert.True(result.GetProperty("Reply").GetProperty("EffectiveTrustAbsent").GetBoolean());
            Assert.False(result.GetProperty("Reply").GetProperty("CertificateRemoved").GetBoolean());
        }
    }

    [Fact]
    public async Task Identity_package_owns_its_logo_and_preserves_cloned_runtime_assets()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            # The hosted checkout may have an owner outside native admission.
            # Use unchanged module/asset bytes in an owned protected source tree.
            $inputs=Join-Path $root 'package-inputs';New-VaultFixtureProtectedDirectory $inputs
            foreach($directory in @('eng','eng/fixtures','src','src/FluxVault.App','src/FluxVault.App/Assets')) {
                New-VaultFixtureProtectedDirectory (Join-Path $inputs $directory)
            }
            foreach($name in @('packaged-identity.psm1','fixture-cng-keys.psm1')) {
                Copy-Item -LiteralPath (Join-Path (Split-Path $module) $name) -Destination (Join-Path $inputs ('eng/fixtures/'+$name))
            }
            Copy-Item -LiteralPath (Join-Path (Split-Path $module) '../../src/FluxVault.App/Assets/YagasoftLogo.png') -Destination (Join-Path $inputs 'src/FluxVault.App/Assets/YagasoftLogo.png')
            Import-Module (Join-Path $inputs 'eng/fixtures/packaged-identity.psm1') -Force
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            $runtime=Join-Path $root 'runtime';New-VaultFixtureProtectedDirectory $runtime
            New-VaultFixtureProtectedDirectory (Join-Path $root 'catalogue')
            $assets=Join-Path $runtime 'Assets';New-VaultFixtureProtectedDirectory $assets
            $sentinel=Join-Path $assets 'existing-logo.png';[IO.File]::WriteAllText($sentinel,'preserve cloned assets')
            $acl=(Get-Acl -LiteralPath $assets).Sddl
            [IO.File]::WriteAllText((Join-Path $runtime 'FluxVault.TestHost.exe'),'not executed')
            # This test covers asset staging and preservation, not signed SDK admission.
            # The separate snapshot tests retain the actual signature boundary.
            $sdk=Join-Path $root 'sdk-stand-in';New-VaultFixtureProtectedDirectory $sdk
            & (Get-Module packaged-identity) {
                function script:New-VaultFixtureSdkSnapshot {param($Journal,$Source) return $Source}
            }
            $failure=$null
            try {
                New-VaultFixtureIdentityPackage $journal $sdk {
                    param($Executable,$Arguments)
                    if([IO.Path]::GetFileName($Executable) -ne 'mt.exe'){throw 'Unexpected tool invocation.'}
                    throw 'Reached bounded packaging boundary.'
                } | Out-Null
            } catch {$failure=$_.Exception.Message}
            $package=Join-Path $root 'catalogue/package-signing/identity'
            $logo=Join-Path $package 'Assets/logo.png'
            $expectedLogo=Join-Path (Split-Path $module) '../../src/FluxVault.App/Assets/YagasoftLogo.png'
            @{Failure=$failure;LogoExists=(Test-Path -LiteralPath $logo);
                LogoMatches=((Test-Path -LiteralPath $logo) -and (Get-FileHash -LiteralPath $logo).Hash -eq (Get-FileHash -LiteralPath $expectedLogo).Hash);
                OriginalBytes=[IO.File]::ReadAllText($sentinel);OriginalAclPreserved=((Get-Acl -LiteralPath $assets).Sddl -eq $acl);
                NoRuntimeLogo=(-not(Test-Path -LiteralPath (Join-Path $assets 'logo.png')));
                NoKey=(-not(Test-Path -LiteralPath (Join-Path $root 'catalogue/package-signing/ephemeral-key.pfx')))}|ConvertTo-Json -Compress
            """);
        Assert.Equal("Reached bounded packaging boundary.", result.GetProperty("Failure").GetString());
        Assert.True(result.GetProperty("LogoExists").GetBoolean());
        Assert.True(result.GetProperty("LogoMatches").GetBoolean());
        Assert.Equal("preserve cloned assets", result.GetProperty("OriginalBytes").GetString());
        Assert.True(result.GetProperty("OriginalAclPreserved").GetBoolean());
        Assert.True(result.GetProperty("NoRuntimeLogo").GetBoolean());
        Assert.True(result.GetProperty("NoKey").GetBoolean());
    }

    [Theory]
    [InlineData("PerUser")]
    [InlineData("MachineParent")]
    public async Task Interrupted_package_cleanup_reaches_B_after_A_is_already_unregistered(string trustMode)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$null)
            $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Remove-OwnedPackageUsers'},$true)
            Invoke-Expression $definition.Extent.Text
            $fixtureJournal=New-VaultFixtureJournal $root $parent $fixtureId
            $fixtureRoot=$root
            $null=New-Item -ItemType Directory -Path (Join-Path $root 'runtime')
            @{PackageTrustMode='TRUST_MODE'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'runtime/database-probe.json')
            $fixtureDescription='FluxVault N2 '+$fixtureId
            $fixtureCredentials=@{A='not used';B='not used'}
            $users=@{}
            foreach($actor in @('A','B')) {
                $sid='S-1-5-21-1-2-3-'+$(if($actor -eq 'A'){9198}else{9199})
                $name="FVGate${actor}_261003"
                $users[$name]=[pscustomobject]@{Name=$name;SID=[pscustomobject]@{Value=$sid};Description=$fixtureDescription}
                Add-VaultFixtureIntent $fixtureJournal Account $name
                Set-VaultFixtureResourceState $fixtureJournal Account $name Created @{Sid=$sid}
                $identity=@{Sid=$sid;PackageName=('FVGate.Package.'+$fixtureId);Publisher=('CN=FluxVault Fixture '+$fixtureId);Thumbprint=('A'*40);TrustMode='TRUST_MODE'}
                Add-VaultFixtureIntent $fixtureJournal PackageUser "package-$actor" $identity
                Set-VaultFixtureResourceState $fixtureJournal PackageUser "package-$actor" Created $identity
            }
            $remaining=[Collections.Generic.HashSet[string]]::new();$null=$remaining.Add('B')
            $visited=[Collections.Generic.List[string]]::new()
            function Get-LocalUser {param($Name) $users[$Name]}
            function Invoke-UserActor {
                param($Actor,$HostAddress,$ClientKind)
                $visited.Add($Actor);$null=$remaining.Remove($Actor)
                [pscustomobject]@{Results=@([pscustomobject]@{Kind='Packaged';Result=[pscustomobject]@{PackageRemoved=$true;CertificateRemoved=('TRUST_MODE' -eq 'PerUser');TrustMode='TRUST_MODE';TrustOwner=$(if('TRUST_MODE' -eq 'MachineParent'){'MachineParent'}else{'Actor'});EffectiveTrustAbsent=$true}})}
            }
            function Get-AppxPackage {
                param([switch]$AllUsers,$Name,$User)
                foreach($actor in $remaining) {
                    if($AllUsers -or $User -eq $users["FVGate${actor}_261003"].SID.Value) {[pscustomobject]@{Name=$Name}}
                }
            }
            Remove-OwnedPackageUsers
            $reopened=Read-VaultFixtureJournal $root $parent $fixtureId
            @{Visited=@($visited);Remaining=$remaining.Count;Removed=@($reopened.Resources|Where-Object {$_.Kind -eq 'PackageUser' -and $_.State -eq 'Removed'}).Count}|ConvertTo-Json -Compress
            """.Replace("TRUST_MODE", trustMode, StringComparison.Ordinal));
        Assert.Equal(new[] { "A", "B" }, result.GetProperty("Visited").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(0, result.GetProperty("Remaining").GetInt32());
        Assert.Equal(2, result.GetProperty("Removed").GetInt32());
    }

    [Fact]
    public async Task Profile_cleanup_preserves_a_leftover_directory_after_CIM_record_removal()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$null)
            $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Remove-OwnedPackageProfiles'},$true)
            Invoke-Expression $definition.Extent.Text
            $fixtureJournal=New-VaultFixtureJournal $root $parent $fixtureId
            $sid='S-1-5-21-1-2-3-9199'
            Add-VaultFixtureIntent $fixtureJournal Account 'FVGateA_261003'
            Set-VaultFixtureResourceState $fixtureJournal Account 'FVGateA_261003' Created @{Sid=$sid}
            $profileParent=[Environment]::ExpandEnvironmentVariables((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList').ProfilesDirectory)
            $profilePath=Join-Path $profileParent 'FVGateA_261003'
            $identity=@{Sid=$sid;Path=$profilePath}
            Add-VaultFixtureIntent $fixtureJournal Profile 'profile-A' $identity
            Set-VaultFixtureResourceState $fixtureJournal Profile 'profile-A' Created $identity
            $script:recordPresent=$true
            function Get-CimInstance {param($ClassName,$Filter) if($script:recordPresent){[pscustomobject]@{Loaded=$false;LocalPath=$profilePath}}}
            function Remove-CimInstance {param([Parameter(ValueFromPipeline)]$InputObject) process {$script:recordPresent=$false}}
            function Test-Path {param($LiteralPath) $LiteralPath -eq $profilePath}
            $refused=$false
            try {Remove-OwnedPackageProfiles} catch {$refused=$_.Exception.Message -eq 'Owned package profile directory remains.'}
            Remove-Item function:Test-Path
            $reopened=Read-VaultFixtureJournal $root $parent $fixtureId
            @{Refused=$refused;CimRemoved=(-not $script:recordPresent);State=@($reopened.Resources|Where-Object Kind -eq 'Profile')[0].State}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.True(result.GetProperty("CimRemoved").GetBoolean());
        Assert.Equal("Created", result.GetProperty("State").GetString());
    }

    [Fact]
    public async Task Package_and_profile_intents_remain_bound_to_the_owned_fixture_principals()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            $sid='S-1-5-21-1-2-3-9199'
            Add-VaultFixtureIntent $journal Account 'FVGateA_261003'
            Set-VaultFixtureResourceState $journal Account 'FVGateA_261003' Created @{Sid=$sid}
            $wrongPackage=$false
            try {Add-VaultFixtureIntent $journal PackageUser 'package-A' @{Sid=$sid;PackageName=('FVGate.Package.'+[guid]::NewGuid().ToString('N'));Publisher=('CN=FluxVault Fixture '+$fixtureId);Thumbprint=('A'*40)}} catch {$wrongPackage=$true}
            $wrongProfile=$false
            try {Add-VaultFixtureIntent $journal Profile 'profile-A' @{Sid=$sid;Path=(Join-Path $root 'unrelated')}} catch {$wrongProfile=$true}
            $wrongPrincipal=$false
            try {Add-VaultFixtureIntent $journal PackageUser 'package-B' @{Sid=$sid;PackageName=('FVGate.Package.'+$fixtureId);Publisher=('CN=FluxVault Fixture '+$fixtureId);Thumbprint=('A'*40)}} catch {$wrongPrincipal=$true}
            $profileParent=[Environment]::ExpandEnvironmentVariables((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList').ProfilesDirectory)
            Add-VaultFixtureIntent $journal Profile 'profile-A' @{Sid=$sid;Path=(Join-Path $profileParent 'FVGateA_261003')}
            Add-VaultFixtureIntent $journal PackageUser 'package-A' @{Sid=$sid;PackageName=('FVGate.Package.'+$fixtureId);Publisher=('CN=FluxVault Fixture '+$fixtureId);Thumbprint=('A'*40)}
            $reopened=Read-VaultFixtureJournal $root $parent $fixtureId
            $intents=@($reopened.Resources|Where-Object {$_.Kind -in @('Profile','PackageUser')})
            @{WrongPackage=$wrongPackage;WrongProfile=$wrongProfile;WrongPrincipal=$wrongPrincipal;ValidIntents=$intents.Count;
                PrincipalsMatch=(@($intents|Where-Object {$_.Identity.Sid -ne $sid}).Count -eq 0)}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("WrongPackage").GetBoolean());
        Assert.True(result.GetProperty("WrongProfile").GetBoolean());
        Assert.True(result.GetProperty("WrongPrincipal").GetBoolean());
        Assert.Equal(2,result.GetProperty("ValidIntents").GetInt32());
        Assert.True(result.GetProperty("PrincipalsMatch").GetBoolean());
    }

    [Fact]
    public async Task Additional_trusted_fixture_owner_requires_direct_administrator_membership()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $adminSid=@(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object {$_.SID.Value})[0]
            Assert-VaultFixtureTrustedPath $root -AdditionalTrustedOwnerSid $adminSid
            $denied=$false
            try {Assert-VaultFixtureTrustedPath $root -AdditionalTrustedOwnerSid 'S-1-5-21-1-2-3-9199'}
            catch {$denied=$_.Exception.Message -eq 'Additional fixture owner is not a direct local administrator.'}
            @{AdministratorAccepted=$true;UnrelatedSidDenied=$denied} | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("AdministratorAccepted").GetBoolean());
        Assert.True(result.GetProperty("UnrelatedSidDenied").GetBoolean());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supervised_actor_creates_children_only_after_kernel_job_admission(bool assign)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-VaultFixtureJobType
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $job=[FluxVault.Fixtures.OwnedWindowsJob]::CreateSupervisedActor(('Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')),$sid)
            $script=Join-Path $root 'actor.ps1';$ready=Join-Path $root 'ready';$childMarker=Join-Path $root 'child'
            @'
            param($Module,$Job,$Ready,$ChildMarker)
            $ErrorActionPreference='Stop';Import-Module $Module -Force;Import-VaultFixtureJobType
            [IO.File]::WriteAllText($Ready,'waiting')
            [FluxVault.Fixtures.OwnedWindowsJob]::WaitForCurrentAdmission($Job)
            $child=Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile','-NonInteractive','-Command','"Start-Sleep -Seconds 60"') -WindowStyle Hidden -PassThru
            try{[IO.File]::WriteAllText($ChildMarker,$child.Id.ToString())}finally{$child.Dispose()}
            '@|Set-Content -LiteralPath $script
            $launcher=$null;$child=$null
            try {
                $module=(Get-Module vault-windows-fixture).Path
                $arguments=@('-NoProfile','-NonInteractive','-File',('"'+$script+'"'),('"'+$module+'"'),$job.Name,('"'+$ready+'"'),('"'+$childMarker+'"'))
                $launcher=Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList $arguments -WindowStyle Hidden -PassThru
                $deadline=[DateTime]::UtcNow.AddSeconds(5)
                while(-not(Test-Path -LiteralPath $ready)){if($launcher.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Actor did not reach admission wait.'};Start-Sleep -Milliseconds 50}
                if(__ASSIGN__){$job.Assign($launcher)}
                if(-not $launcher.WaitForExit(8000)){throw 'Admission did not finish within its deadline.'}
                $created=Test-Path -LiteralPath $childMarker;$contained=$false
                if($created){$childId=[uint](Get-Content -LiteralPath $childMarker);$contained=$childId -in $job.ProcessIds();$child=Get-Process -Id $childId}
                $exitCode=$launcher.ExitCode
                $job.StopAndJoin()
                if($null -ne $child -and -not $child.WaitForExit(5000)){throw 'Contained child remains.'}
                @{Created=$created;Contained=$contained;ExitCode=$exitCode;Empty=($job.ProcessIds().Length -eq 0)}|ConvertTo-Json -Compress
            }finally{
                $job.StopAndJoin();$job.Dispose()
                if($null -ne $launcher){if(-not $launcher.HasExited){$launcher.Kill($true)};if(-not $launcher.WaitForExit(5000)){throw 'Launcher remains.'};$launcher.Dispose()}
                if($null -ne $child){$child.Dispose()}
            }
            """.Replace("__ASSIGN__", assign ? "$true" : "$false", StringComparison.Ordinal));
        Assert.Equal(assign, result.GetProperty("Created").GetBoolean());
        Assert.Equal(assign, result.GetProperty("Contained").GetBoolean());
        Assert.Equal(assign, result.GetProperty("ExitCode").GetInt32() == 0);
        Assert.True(result.GetProperty("Empty").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actor_job_has_medium_integrity_and_only_its_declared_permission(bool supervised)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-VaultFixtureJobType
            Add-Type @'
            using System;
            using System.Runtime.InteropServices;
            public static class JobDescriptorProbe {
                [DllImport("advapi32")] static extern uint GetSecurityInfo(IntPtr handle,int type,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
                [DllImport("advapi32",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr descriptor,uint revision,uint information,out IntPtr text,out uint length);
                [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr pointer);
                public static string Read(IntPtr handle) {
                    IntPtr owner,group,dacl,sacl,descriptor;
                    uint error=GetSecurityInfo(handle,6,0x15,out owner,out group,out dacl,out sacl,out descriptor);
                    if(error!=0)throw new System.ComponentModel.Win32Exception((int)error);
                    try {
                        IntPtr text;uint length;
                        if(!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor,1,0x15,out text,out length))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        try{return Marshal.PtrToStringUni(text);}finally{LocalFree(text);}
                    }finally{LocalFree(descriptor);}
                }
            }
            '@
            $actorSid='S-1-5-21-1-2-3-9000'
            $job=[FluxVault.Fixtures.OwnedWindowsJob]::__FACTORY__(('Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')),$actorSid)
            try {
                $handle=$job.GetType().GetField('handle',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($job)
                $actual=[JobDescriptorProbe]::Read($handle)
                $descriptor=[Security.AccessControl.RawSecurityDescriptor]::new($actual)
                $actorGrants=@($descriptor.DiscretionaryAcl|Where-Object{$_.SecurityIdentifier.Value -eq $actorSid})
                @{MediumNoWriteUp=$actual.Contains('(ML;;NW;;;ME)');ActorGrantCount=$actorGrants.Count;ActorMask=$actorGrants[0].AccessMask;
                    OwnerMatches=($descriptor.Owner.Value -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value);Processes=$job.ProcessIds().Length}|ConvertTo-Json -Compress
            }finally{$job.StopAndJoin();$job.Dispose()}
            """.Replace("__FACTORY__", supervised ? "CreateSupervisedActor" : "Create", StringComparison.Ordinal));
        Assert.True(result.GetProperty("MediumNoWriteUp").GetBoolean());
        Assert.Equal(1, result.GetProperty("ActorGrantCount").GetInt32());
        Assert.Equal(supervised ? 4 : 1, result.GetProperty("ActorMask").GetInt32());
        Assert.True(result.GetProperty("OwnerMatches").GetBoolean());
        Assert.Equal(0, result.GetProperty("Processes").GetInt32());
    }

    [Fact]
    public async Task Fresh_child_job_accepts_a_new_parent_after_a_previous_hierarchy_terminates()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-VaultFixtureJobType
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $jobs=@(1..4|ForEach-Object{[FluxVault.Fixtures.OwnedWindowsJob]::Create(('Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')),$sid)})
            $worker=Join-Path $root 'worker.ps1';$ready=Join-Path $root 'ready';$resultPath=Join-Path $root 'result.json'
            @'
            param($Module,$Parent,$Common,$Fresh,$Output)
            $ErrorActionPreference='Stop';Import-Module $Module -Force;Import-VaultFixtureJobType
            [FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($Parent)
            if($Fresh -eq 'first'){
                [FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($Common)
                [IO.File]::WriteAllText($Output,'ready');Start-Sleep -Seconds 60
            }else{
                $refused=$false
                try{[FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($Common)}catch [ComponentModel.Win32Exception]{$refused=$_.Exception.NativeErrorCode -eq 5}
                [FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($Fresh)
                @{OldHierarchyRefused=$refused;FreshJoined=$true}|ConvertTo-Json -Compress|Set-Content -LiteralPath $Output
            }
            '@|Set-Content -LiteralPath $worker
            $module=(Get-Module vault-windows-fixture).Path;$first=$null;$second=$null
            try{
                $arguments=@('-NoProfile','-File',('"'+$worker+'"'),('"'+$module+'"'),$jobs[0].Name,$jobs[2].Name,'first',('"'+$ready+'"'))
                $first=Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList $arguments -WindowStyle Hidden -PassThru
                $deadline=[DateTime]::UtcNow.AddSeconds(5);while(-not(Test-Path -LiteralPath $ready)){if([DateTime]::UtcNow -ge $deadline){throw 'First hierarchy did not admit its child.'};Start-Sleep -Milliseconds 50}
                $jobs[0].StopAndJoin();if(-not $first.WaitForExit(5000)){throw 'First hierarchy remains.'}
                $arguments=@('-NoProfile','-File',('"'+$worker+'"'),('"'+$module+'"'),$jobs[1].Name,$jobs[2].Name,$jobs[3].Name,('"'+$resultPath+'"'))
                $second=Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList $arguments -WindowStyle Hidden -PassThru
                if(-not $second.WaitForExit(8000) -or $second.ExitCode -ne 0){throw 'Second hierarchy did not finish.'}
                Get-Content -LiteralPath $resultPath -Raw
            }finally{
                $jobs[0].StopAndJoin();$jobs[1].StopAndJoin()
                foreach($process in @($first,$second)){if($null -ne $process){if(-not $process.HasExited){$process.Kill($true)};if(-not $process.WaitForExit(5000)){throw 'Hierarchy process remains.'};$process.Dispose()}}
                foreach($job in $jobs){if($job.ProcessIds().Length){throw 'Hierarchy job still contains a process.'};$job.Dispose()}
            }
            """);
        Assert.True(result.GetProperty("OldHierarchyRefused").GetBoolean());
        Assert.True(result.GetProperty("FreshJoined").GetBoolean());
    }

    [Fact]
    public async Task Task_instance_updates_preserve_the_owned_definition_and_survive_journal_reopen()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            $hash='A'*64;Add-VaultFixtureIntent $journal Task 'owned-task'
            Set-VaultFixtureResourceState $journal Task 'owned-task' Created @{DefinitionSha256=$hash;InstanceId=''}
            $first=[guid]::NewGuid().ToString();Set-VaultFixtureTaskInstance $journal 'owned-task' $hash $first
            $journal=Read-VaultFixtureJournal $root $parent $fixtureId
            $second=[guid]::NewGuid().ToString();Set-VaultFixtureTaskInstance $journal 'owned-task' $hash $second
            $wrongHash=$false;try{Set-VaultFixtureTaskInstance $journal 'owned-task' ('B'*64) ([guid]::NewGuid().ToString())}catch{$wrongHash=$true}
            $invalid=$false;try{Set-VaultFixtureTaskInstance $journal 'owned-task' $hash ''}catch{$invalid=$true}
            $journal=Read-VaultFixtureJournal $root $parent $fixtureId;$task=@($journal.Resources|Where-Object{$_.Kind -eq 'Task'})[0]
            @{Instance=$task.Identity.InstanceId;Expected=$second;Definition=$task.Identity.DefinitionSha256;WrongDefinitionRefused=$wrongHash;EmptyInstanceRefused=$invalid;State=$task.State}|ConvertTo-Json -Compress
            """);
        Assert.Equal(result.GetProperty("Expected").GetString(), result.GetProperty("Instance").GetString());
        Assert.Equal(new string('A', 64), result.GetProperty("Definition").GetString());
        Assert.Equal("Created", result.GetProperty("State").GetString());
        Assert.True(result.GetProperty("WrongDefinitionRefused").GetBoolean());
        Assert.True(result.GetProperty("EmptyInstanceRefused").GetBoolean());
    }

    [Fact]
    public async Task Active_PostgreSQL_log_can_be_read_while_its_writer_is_open()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $path=Join-Path $root 'postgres.log'
            $writer=[IO.FileStream]::new($path,'CreateNew','Write','ReadWrite')
            try{
                $payload=[Text.Encoding]::UTF8.GetBytes('owned authentication refusal')
                $writer.Write($payload);$writer.Flush()
                $actual=Read-VaultFixtureLog -Path $path
                @{Text=$actual}|ConvertTo-Json -Compress
            }finally{$writer.Dispose()}
            """);
        Assert.Equal("owned authentication refusal", result.GetProperty("Text").GetString());
    }

    [Fact]
    public async Task Log_windows_refuse_truncation_and_oversize_and_exclude_previous_probes()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $path=Join-Path $root 'postgres.log';[IO.File]::WriteAllText($path,'prior/current/next')
            $current=Read-VaultFixtureLog $path -StartOffset 6 -EndOffset 13
            $truncated=$false;try{Read-VaultFixtureLog $path -StartOffset 0 -EndOffset 999|Out-Null}catch{$truncated=$true}
            $writer=[IO.File]::OpenWrite($path);try{$writer.SetLength(4MB+1)}finally{$writer.Dispose()}
            $oversize=$false;try{Read-VaultFixtureLog $path|Out-Null}catch{$oversize=$true}
            @{Current=$current;TruncationRefused=$truncated;OversizeRefused=$oversize}|ConvertTo-Json -Compress
            """);
        Assert.Equal("current", result.GetProperty("Current").GetString());
        Assert.True(result.GetProperty("TruncationRefused").GetBoolean());
        Assert.True(result.GetProperty("OversizeRefused").GetBoolean());
    }

    [Fact]
    public async Task Sspi_refusal_requires_same_backend_host_role_map_and_actual_Windows_SID()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $one=@'
            [101] [unknown] LOG:  connection received: host=127.0.0.1 port=62001
            [101] [unknown] LOG:  connection authenticated: identity="NT AUTHORITY\SYSTEM" method=sspi (fixture)
            [101] [unknown] LOG:  no match in usermap "fv_gate_system" for user "fv_gate_service" authenticated as "SYSTEM@NT AUTHORITY"
            [101] [unknown] FATAL:  SSPI authentication failed for user "fv_gate_service"
            '@
            # Match the observed PostgreSQL LF log format regardless of checkout endings.
            $one=$one.Replace("`r`n","`n")
            $two=$one+"`n"+$one.Replace('[101]','[102]')
            $principal=Assert-VaultFixtureSspiRefusal $two -ExpectedSid 'S-1-5-18' -HostAddress '127.0.0.1'
            $refused=0
            foreach($bad in @($one.Replace('[101] [unknown] LOG:  no match','[999] [unknown] LOG:  no match'),$one.Replace('method=sspi','method=trust'),$one.Replace('fv_gate_system','other_map'),$one.Replace('fv_gate_service','other_role'),$one.Replace('127.0.0.1','::1'),$one+"`n[101] [unknown] LOG:  connection authorized: user=fv_gate_service database=fv_gate_261003",'')){
                try{Assert-VaultFixtureSspiRefusal $bad -ExpectedSid 'S-1-5-18' -HostAddress '127.0.0.1'|Out-Null}catch{$refused++}
            }
            $otherSid=$false;try{Assert-VaultFixtureSspiRefusal $one -ExpectedSid 'S-1-5-19' -HostAddress '127.0.0.1'|Out-Null}catch{$otherSid=$true}
            @{Principal=$principal;Refused=$refused;WrongSidRefused=$otherSid}|ConvertTo-Json -Compress
            """);
        Assert.Equal("SYSTEM@NT AUTHORITY", result.GetProperty("Principal").GetString());
        Assert.Equal(7, result.GetProperty("Refused").GetInt32());
        Assert.True(result.GetProperty("WrongSidRefused").GetBoolean());
    }

    [Fact]
    public async Task Postmaster_guard_accepts_omitted_input_and_refuses_payload_or_another_data_root()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $proposed=Join-Path 'C:\ProgramData\FluxVault.Tests\NEXT002' $fixtureId
            $executable=Join-Path $proposed 'postgresql/bin/pg_ctl.exe'
            $arguments=@('-D',(Join-Path $proposed 'data'),'-l',(Join-Path $proposed 'postgres.log'),'-w','-t','30','start')
            Assert-VaultFixturePostmasterLaunch -Root $proposed -Executable $executable -Arguments $arguments
            $payloadRefused=$false;try{Assert-VaultFixturePostmasterLaunch -Root $proposed -Executable $executable -Arguments $arguments -InputText 'unexpected stdin'}catch{$payloadRefused=$true}
            $arguments[1]='D:\Program Files\PostgreSQL\18\data'
            $otherDataRefused=$false;try{Assert-VaultFixturePostmasterLaunch -Root $proposed -Executable $executable -Arguments $arguments}catch{$otherDataRefused=$true}
            @{OmittedInputAccepted=$true;PayloadRefused=$payloadRefused;OtherDataRefused=$otherDataRefused;NoRootCreated=(-not(Test-Path -LiteralPath $proposed))}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("OmittedInputAccepted").GetBoolean());
        Assert.True(result.GetProperty("PayloadRefused").GetBoolean());
        Assert.True(result.GetProperty("OtherDataRefused").GetBoolean());
        Assert.True(result.GetProperty("NoRootCreated").GetBoolean());
    }

    [Fact]
    public async Task Manifest_snapshot_verifies_pinned_provenance_before_publishing_installed_bytes()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path (Get-Module vault-windows-fixture).Path) 'verified-postgresql-snapshot.psm1') -Force
            $installed=Join-Path $root 'installed';[IO.Directory]::CreateDirectory($installed)|Out-Null
            $rows=@(foreach($relative in @('bin/postgres.exe','bin/pg_ctl.exe','bin/initdb.exe','bin/psql.exe','bin/libpq.dll','share/postgres.bki','lib/plpgsql.dll')){
                $source=Join-Path $installed $relative;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($source))|Out-Null
                [IO.File]::WriteAllText($source,'generated fixture bytes for '+$relative)
                @{Path=$relative;Sha256=(Get-FileHash -LiteralPath $source).Hash;Bytes=(Get-Item -LiteralPath $source).Length}
            })
            $manifest=Join-Path $root 'reference.json'
            @{Distribution='18.6-4';ReferenceUri='https://get.enterprisedb.com/postgresql/postgresql-18.6-4-windows-x64-binaries.zip';ReferenceAuthentication='HTTPS with default certificate validation; redirects forbidden';ArchiveSha256=('1'*64);Files=$rows}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $manifest
            $expected=(Get-FileHash -LiteralPath $manifest).Hash
            $wrongPinRefused=$false;try{New-VaultFixtureSnapshotFromManifest $root $installed $manifest ('0'*64)|Out-Null}catch{$wrongPinRefused=$true}
            $notPublished=-not(Test-Path -LiteralPath (Join-Path $root 'postgresql'))
            $bin=New-VaultFixtureSnapshotFromManifest $root $installed $manifest $expected
            $valid=(@($rows|Where-Object{(Get-FileHash -LiteralPath (Join-Path $root ('postgresql/'+$_.Path))).Hash -ne $_.Sha256}).Count -eq 0)
            $provenance=Get-Content -LiteralPath (Join-Path $root 'postgresql-provenance.json') -Raw|ConvertFrom-Json
            @{WrongPinRefused=$wrongPinRefused;NotPublished=$notPublished;AllCopiedBytesVerified=$valid;Bin=$bin;ExpectedPin=$expected;ReusedPin=$provenance.ReusedManifestSha256}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("WrongPinRefused").GetBoolean());
        Assert.True(result.GetProperty("NotPublished").GetBoolean());
        Assert.True(result.GetProperty("AllCopiedBytesVerified").GetBoolean());
        Assert.Equal(result.GetProperty("ExpectedPin").GetString(), result.GetProperty("ReusedPin").GetString());
    }

    [Fact]
    public async Task Process_identity_uses_the_kernel_handle_and_never_publishes_a_gone_launcher()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-VaultFixtureJobType
            $executable=(Get-Command pwsh).Source
            $child=Start-Process -FilePath $executable -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 2') -WindowStyle Hidden -PassThru
            try {
                $null=$child.Handle;$started=$child.StartTime.ToUniversalTime().ToString('o');$processId=$child.Id
                $identity=Get-VaultFixtureLiveProcessIdentity $child
                if(-not $child.WaitForExit(5000)){throw 'Fast launcher did not exit.'}
                $gone=Get-VaultFixtureLiveProcessIdentity $child
                @{Identity=$identity;GoneRefused=($null -eq $gone);ExpectedExecutable=$executable;ExpectedStart=$started;ExpectedPid=$processId;Exited=$child.HasExited}|ConvertTo-Json -Compress
            }finally{if(-not $child.HasExited){$child.Kill($true)};if(-not $child.WaitForExit(5000)){throw 'Launcher cleanup failed.'};$child.Dispose()}
            """);
        Assert.True(result.GetProperty("Exited").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("Identity").ValueKind);
        Assert.True(result.GetProperty("GoneRefused").GetBoolean());
        Assert.Equal(result.GetProperty("ExpectedExecutable").GetString(), result.GetProperty("Identity").GetProperty("Executable").GetString());
        Assert.Equal(result.GetProperty("ExpectedStart").GetString(), result.GetProperty("Identity").GetProperty("StartedUtc").GetString());
        Assert.Equal(result.GetProperty("ExpectedPid").GetInt32(), result.GetProperty("Identity").GetProperty("ProcessId").GetInt32());
    }

    [Fact]
    public async Task Reference_archive_bounds_metadata_and_selects_only_safe_server_payload()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path (Get-Module vault-windows-fixture).Path) 'verified-postgresql-snapshot.psm1') -Force
            $zipPath=Join-Path $root 'reference.zip'
            $zip=[IO.Compression.ZipFile]::Open($zipPath,'Create')
            try {
                for($i=0;$i -lt 22025;$i++){ $null=$zip.CreateEntry("pgsql/pgAdmin/extras/$i") }
                $null=$zip.CreateEntry('pgsql/bin/postgres.exe')
            }finally{$zip.Dispose()}
            $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
            $selected=@();$accepted=$false
            try { $selected=@(Get-VaultFixturePostgreSqlEntries $zip);$accepted=$true }catch{}finally{$zip.Dispose()}
            $unsafePath=Join-Path $root 'unsafe.zip';$zip=[IO.Compression.ZipFile]::Open($unsafePath,'Create')
            try{$null=$zip.CreateEntry('pgsql/bin/../../outside')}finally{$zip.Dispose()}
            $zip=[IO.Compression.ZipFile]::OpenRead($unsafePath);$unsafeRefused=$false
            try{$null=Get-VaultFixturePostgreSqlEntries $zip}catch{$unsafeRefused=$true}finally{$zip.Dispose()}
            $excessivePath=Join-Path $root 'excessive.zip';$zip=[IO.Compression.ZipFile]::Open($excessivePath,'Create')
            try{for($i=0;$i -lt 32769;$i++){$null=$zip.CreateEntry("extras/$i")}}finally{$zip.Dispose()}
            $zip=[IO.Compression.ZipFile]::OpenRead($excessivePath);$metadataRefused=$false
            try{$null=Get-VaultFixturePostgreSqlEntries $zip}catch{$metadataRefused=$true}finally{$zip.Dispose()}
            @{Accepted=$accepted;SelectedNames=@($selected.FullName);UnsafeRefused=$unsafeRefused;MetadataRefused=$metadataRefused}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Accepted").GetBoolean());
        Assert.Equal("pgsql/bin/postgres.exe", Assert.Single(result.GetProperty("SelectedNames").EnumerateArray()).GetString());
        Assert.True(result.GetProperty("UnsafeRefused").GetBoolean());
        Assert.True(result.GetProperty("MetadataRefused").GetBoolean());
    }

    [Fact]
    public async Task Snapshot_refuses_oversized_source_before_creating_the_destination()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path (Get-Module vault-windows-fixture).Path) 'verified-postgresql-snapshot.psm1') -Force
            $source=Join-Path $root 'oversized.bin';$stream=[IO.File]::Open($source,'CreateNew','Write','None');try{$stream.SetLength(1048576)}finally{$stream.Dispose()}
            $destination=Join-Path $root 'unpublished.bin';$refused=$false
            try{Copy-VaultFixtureVerifiedFile $source $destination ('0'*64) 16}catch{$refused=$true}
            @{Refused=$refused;DestinationPublished=(Test-Path -LiteralPath $destination)}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.False(result.GetProperty("DestinationPublished").GetBoolean());
    }

    [Fact]
    public async Task Snapshot_releases_source_when_destination_creation_fails()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path (Get-Module vault-windows-fixture).Path) 'verified-postgresql-snapshot.psm1') -Force
            $source=Join-Path $root 'source.bin';[IO.File]::WriteAllText($source,'fixture bytes')
            $destination=Join-Path $root 'existing.bin';[IO.File]::WriteAllText($destination,'existing sentinel')
            try{Copy-VaultFixtureVerifiedFile $source $destination (Get-FileHash -LiteralPath $source).Hash (Get-Item -LiteralPath $source).Length}catch{}
            $released=$false;try{$stream=[IO.File]::Open($source,'Open','Write','None');$stream.Dispose();$released=$true}catch{}
            @{SourceReleased=$released;ExistingDestination=[IO.File]::ReadAllText($destination)}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("SourceReleased").GetBoolean());
        Assert.Equal("existing sentinel", result.GetProperty("ExistingDestination").GetString());
    }

    [Fact]
    public async Task Snapshot_verification_checks_final_bytes_and_refuses_a_different_installed_payload()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-Module (Join-Path (Split-Path (Get-Module vault-windows-fixture).Path) 'verified-postgresql-snapshot.psm1') -Force
            $reference=Join-Path $root 'reference.bin';[IO.File]::WriteAllText($reference,'authenticated reference fixture bytes')
            $source=Join-Path $root 'installed.bin';Copy-Item -LiteralPath $reference -Destination $source
            $hash=(Get-FileHash -LiteralPath $reference).Hash;$length=(Get-Item -LiteralPath $reference).Length
            $valid=Join-Path $root 'valid.bin';Copy-VaultFixtureVerifiedFile $source $valid $hash $length
            [IO.File]::WriteAllText($source,('x' * $length))
            $refused=$false;try{Copy-VaultFixtureVerifiedFile $source (Join-Path $root 'invalid.bin') $hash $length}catch{$refused=$true}
            @{ControlMatches=((Get-FileHash -LiteralPath $valid).Hash -eq $hash);DifferentPayloadRefused=$refused}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("ControlMatches").GetBoolean());
        Assert.True(result.GetProperty("DifferentPayloadRefused").GetBoolean());
    }

    [Fact]
    public async Task Saved_installation_baseline_round_trip_preserves_process_time_and_detects_changed_state()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $process=Get-Process -Id $PID
            try{$identity=Get-VaultFixtureProcessIdentity $process}finally{$process.Dispose()}
            $sentinel=Join-Path $root 'installation.bin';[IO.File]::WriteAllText($sentinel,'original fixture bytes')
            $snapshot=@{Services=@(@{Name='fixture-service';StartName='fixture-account';PathName=$identity.Executable;Identity=$identity});Files=@(@{Path=$sentinel;Sha256=(Get-FileHash -LiteralPath $sentinel).Hash})}
            $baselinePath=Join-Path $root 'before.json';$snapshot|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $baselinePath
            $reopened=Get-Content -LiteralPath $baselinePath -Raw|ConvertFrom-Json
            $same=Test-VaultFixtureInstallationUnchanged $reopened $snapshot
            $snapshot.Services[0].Identity.StartedUtc=([DateTimeOffset]$identity.StartedUtc).AddSeconds(1).UtcDateTime.ToString('o')
            $changedTime=Test-VaultFixtureInstallationUnchanged $reopened $snapshot
            $snapshot.Services[0].Identity.StartedUtc=([DateTimeOffset]$reopened.Services[0].Identity.StartedUtc).UtcDateTime.ToString('o')
            [IO.File]::WriteAllText($sentinel,'changed fixture bytes');$snapshot.Files[0].Sha256=(Get-FileHash -LiteralPath $sentinel).Hash
            @{Same=$same;ChangedTime=$changedTime;ChangedBytes=(Test-VaultFixtureInstallationUnchanged $reopened $snapshot)}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Same").GetBoolean());
        Assert.False(result.GetProperty("ChangedTime").GetBoolean());
        Assert.False(result.GetProperty("ChangedBytes").GetBoolean());
    }

    [Fact]
    public async Task Reopened_journal_allocates_a_new_tool_intent_without_reusing_previous_names()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            Add-VaultFixtureIntent $journal Process 'tool-1' @{StartedUtc=[DateTime]::UtcNow.ToString('o');Executable=(Get-Command pwsh).Source}
            $reopened=Read-VaultFixtureJournal $root $parent $fixtureId
            $newName=Add-VaultFixtureToolIntent $reopened (Get-Command pwsh).Source
            $persisted=Read-VaultFixtureJournal $root $parent $fixtureId
            @{NewName=$newName;ResourceCount=$persisted.Resources.Count;DistinctNames=@($persisted.Resources.Name | Select-Object -Unique).Count}|ConvertTo-Json -Compress
            """);
        Assert.NotEqual("tool-1", result.GetProperty("NewName").GetString());
        Assert.Equal(2, result.GetProperty("ResourceCount").GetInt32());
        Assert.Equal(2, result.GetProperty("DistinctNames").GetInt32());
    }

    [Fact]
    public async Task Journal_refuses_secrets_hidden_in_runner_metadata()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal=New-VaultFixtureJournal $root $parent $fixtureId
            $journal.RunnerIdentity.Password='fixture-only-value'
            $refused=$false
            try{Add-VaultFixtureIntent $journal Group 'uncreated'}catch{$refused=$true}
            @{Refused=$refused;ContainsPassword=[IO.File]::ReadAllText($journal.Path).Contains('fixture-only-value')}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.False(result.GetProperty("ContainsPassword").GetBoolean());
    }

    [Fact]
    public async Task Job_containment_joins_an_unpublished_child_after_its_launcher_exits()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            Import-VaultFixtureJobType
            $jobName='Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N')
            $job=[FluxVault.Fixtures.OwnedWindowsJob]::Create($jobName,[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
            $spawner=Join-Path $root 'unpublished.ps1'
            $ready=Join-Path $root 'ready'
            @'
            param($Module,$JobName,$Ready)
            Import-Module $Module
            Import-VaultFixtureJobType
            [FluxVault.Fixtures.OwnedWindowsJob]::JoinCurrent($JobName)
            Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 30') -WindowStyle Hidden | Out-Null
            [IO.File]::WriteAllText($Ready,'child launched; no child identity published')
            Start-Sleep -Seconds 30
            '@ | Set-Content -LiteralPath $spawner
            $module=(Get-Module vault-windows-fixture).Path
            $launcher=Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile','-File',('"'+$spawner+'"'),('"'+$module+'"'),$jobName,('"'+$ready+'"')) -WindowStyle Hidden -PassThru
            $held=@()
            try {
                $deadline=[DateTime]::UtcNow.AddSeconds(10)
                while(-not(Test-Path -LiteralPath $ready)){if([DateTime]::UtcNow -gt $deadline){throw 'Contained child did not start.'};Start-Sleep -Milliseconds 50}
                foreach($processId in $job.ProcessIds()){ $p=Get-Process -Id $processId; $null=$p.Handle; $held+=@($p) }
                $launcher.Kill(); if(-not $launcher.WaitForExit(5000)){throw 'Launcher remained.'}
                $survived=$job.ProcessIds().Length -gt 0
                $job.StopAndJoin()
                foreach($p in $held){if(-not $p.WaitForExit(5000)){throw 'Unpublished child remained.'}}
                @{ ChildSurvivedLauncher=$survived; JobEmpty=($job.ProcessIds().Length -eq 0); AllCapturedExited=(@($held|Where-Object{-not $_.HasExited}).Count -eq 0) }|ConvertTo-Json -Compress
            } finally {
                $job.Dispose()
                foreach($p in $held){if(-not $p.WaitForExit(5000)){throw 'Contained cleanup failed.'};$p.Dispose()}
                if(-not $launcher.HasExited){$launcher.Kill($true)};$launcher.WaitForExit();$launcher.Dispose()
            }
            """);
        Assert.True(result.GetProperty("ChildSurvivedLauncher").GetBoolean());
        Assert.True(result.GetProperty("JobEmpty").GetBoolean());
        Assert.True(result.GetProperty("AllCapturedExited").GetBoolean());
    }

    [Fact]
    public async Task Protected_child_allows_creation_only_ancestor_but_refuses_child_replacement_or_writable_target()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $ancestor=Join-Path $root 'creation-only-ancestor'
            New-VaultFixtureProtectedDirectory $ancestor
            $users=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
            $acl=Get-Acl -LiteralPath $ancestor
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'Write','ContainerInherit','None','Allow'))
            Set-Acl -LiteralPath $ancestor -AclObject $acl
            $runtime=Join-Path $ancestor 'protected-runtime'
            New-VaultFixtureProtectedDirectory $runtime -ReadSids @($users.Value)
            $payload=Join-Path $runtime 'payload.bin';[IO.File]::WriteAllText($payload,'generated protected bytes')
            $allowed=$false;$controlError='';try{Assert-VaultFixtureTrustedPath $payload;$allowed=$true}catch{$controlError=$_.Exception.Message+' '+$_.ScriptStackTrace}
            $ancestorAsTargetRefused=$false;try{Assert-VaultFixtureTrustedPath $ancestor}catch{$ancestorAsTargetRefused=$true}
            $replacement=Get-Acl -LiteralPath $ancestor
            $replacement.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'DeleteSubdirectoriesAndFiles','None','None','Allow'))
            Set-Acl -LiteralPath $ancestor -AclObject $replacement
            $replacementRefused=$false;try{Assert-VaultFixtureTrustedPath $payload}catch{$replacementRefused=$true}
            Set-Acl -LiteralPath $ancestor -AclObject $acl
            $writable=Get-Acl -LiteralPath $payload
            $writable.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'WriteAttributes','Allow'))
            Set-Acl -LiteralPath $payload -AclObject $writable
            $targetRefused=$false;try{Assert-VaultFixtureTrustedPath $payload}catch{$targetRefused=$true}
            @{Allowed=$allowed;ControlError=$controlError;AncestorAsTargetRefused=$ancestorAsTargetRefused;ReplacementRefused=$replacementRefused;TargetRefused=$targetRefused}|ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Allowed").GetBoolean(), result.GetProperty("ControlError").GetString());
        Assert.True(result.GetProperty("AncestorAsTargetRefused").GetBoolean());
        Assert.True(result.GetProperty("ReplacementRefused").GetBoolean());
        Assert.True(result.GetProperty("TargetRefused").GetBoolean());
    }

    [Fact]
    public async Task Privileged_runtime_refuses_standard_user_write_or_ancestor_replacement_rights()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runtime = Join-Path $root 'runtime'
            New-Item -ItemType Directory -Path $runtime | Out-Null
            $acl = Get-Acl -LiteralPath $runtime
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
            Set-Acl -LiteralPath $runtime -AclObject $acl
            $refused = $false
            try { Assert-VaultFixtureTrustedPath -Path $runtime }
            catch { $refused = $true }
            @{ Refused = $refused } | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
    }

    [Fact]
    public async Task Owned_process_tree_cleanup_joins_both_parent_and_child()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $spawner = Join-Path $root 'spawn.ps1'
            $childIdPath = Join-Path $root 'child.json'
            @'
            param($Output)
            $child = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 30') -WindowStyle Hidden -PassThru
            @{ ProcessId=$child.Id; StartedUtc=$child.StartTime.ToUniversalTime().ToString('o'); Executable=$child.MainModule.FileName } | ConvertTo-Json -Compress | Set-Content -LiteralPath $Output
            Start-Sleep -Seconds 30
            '@ | Set-Content -LiteralPath $spawner
            $parentProcess = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile','-File',('"'+$spawner+'"'),('"'+$childIdPath+'"')) -WindowStyle Hidden -PassThru
            $childIdentity = $null
            try {
                $deadline = [DateTime]::UtcNow.AddSeconds(10)
                while (-not (Test-Path -LiteralPath $childIdPath)) { if([DateTime]::UtcNow -gt $deadline){ throw 'Child did not publish its identity.' }; Start-Sleep -Milliseconds 50 }
                $childIdentity = Get-Content -LiteralPath $childIdPath -Raw | ConvertFrom-Json -AsHashtable
                $childIdentity.StartedUtc = ([DateTimeOffset]$childIdentity.StartedUtc).UtcDateTime.ToString('o')
                $identities = @(Stop-VaultFixtureProcessTree -Identity (Get-VaultFixtureProcessIdentity $parentProcess))
                @{ Count=$identities.Count; ParentCaptured=($parentProcess.Id -in $identities.ProcessId); ChildCaptured=($childIdentity.ProcessId -in $identities.ProcessId); ParentExited=($null -eq (Get-Process -Id $parentProcess.Id -ErrorAction SilentlyContinue)); ChildExited=($null -eq (Get-Process -Id $childIdentity.ProcessId -ErrorAction SilentlyContinue)) } | ConvertTo-Json -Compress
            } finally {
                if($null -ne $childIdentity){ Stop-VaultFixtureProcess -Identity $childIdentity }
                if(-not $parentProcess.HasExited){ $parentProcess.Kill($true) }
                if(-not $parentProcess.WaitForExit(5000)){ throw 'Owned spawner remained.' }
                $parentProcess.Dispose()
            }
            """);
        // Hidden Windows launches can also create conhost descendants; each captured identity is joined by the helper.
        Assert.True(result.GetProperty("Count").GetInt32() >= 2);
        Assert.True(result.GetProperty("ParentCaptured").GetBoolean());
        Assert.True(result.GetProperty("ChildCaptured").GetBoolean());
        Assert.True(result.GetProperty("ParentExited").GetBoolean());
        Assert.True(result.GetProperty("ChildExited").GetBoolean());
    }

    [Fact]
    public async Task Fixture_root_validation_refuses_a_sibling_or_a_different_identity()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $accepted = Resolve-VaultFixtureRoot -Root $root -Parent $parent -FixtureId $fixtureId
            $wrongIdRefused = $false
            try { Resolve-VaultFixtureRoot -Root $root -Parent $parent -FixtureId ([guid]::NewGuid().ToString('N')) | Out-Null }
            catch { $wrongIdRefused = $true }
            $outsideRefused = $false
            try { Resolve-VaultFixtureRoot -Root (Join-Path $parent '../unrelated') -Parent $parent -FixtureId $fixtureId | Out-Null }
            catch { $outsideRefused = $true }
            @{ Accepted = $accepted; WrongIdRefused = $wrongIdRefused; OutsideRefused = $outsideRefused } | ConvertTo-Json -Compress
            """);
        Assert.Equal(fixture.Root, result.GetProperty("Accepted").GetString(), ignoreCase: true);
        Assert.True(result.GetProperty("WrongIdRefused").GetBoolean());
        Assert.True(result.GetProperty("OutsideRefused").GetBoolean());
    }

    [Fact]
    public async Task Interrupted_resource_intent_remains_durable_and_refuses_cleanup()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal = New-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            Add-VaultFixtureIntent -Journal $journal -Kind Account -Name 'FVGateA_261003'
            $reopened = Read-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            $refused = $false
            try { Remove-VaultFixtureTree -Root $root -Parent $parent -FixtureId $fixtureId }
            catch { $refused = $true }
            @{ Refused = $refused; RootRetained = (Test-Path -LiteralPath $root); Kind = $reopened.Resources[0].Kind; State = $reopened.Resources[0].State } | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.True(result.GetProperty("RootRetained").GetBoolean());
        Assert.Equal("Account", result.GetProperty("Kind").GetString());
        Assert.Equal("Intent", result.GetProperty("State").GetString());
    }

    [Fact]
    public async Task Journal_refuses_secret_or_unrecognised_resource_fields()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal = New-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            $refused = $false
            try { Add-VaultFixtureIntent -Journal $journal -Kind Account -Name 'FVGateA_261003' -Identity @{ Password = 'fixture-only-value' } }
            catch { $refused = $true }
            $reopened = Read-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            @{ Refused = $refused; Count = $reopened.Resources.Count; ContainsPassword = ([IO.File]::ReadAllText($journal.Path).Contains('fixture-only-value')) } | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.Equal(0, result.GetProperty("Count").GetInt32());
        Assert.False(result.GetProperty("ContainsPassword").GetBoolean());
    }

    [Fact]
    public async Task Reopened_journal_refuses_top_level_secret_fields_before_any_mutation()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal = New-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            $journal.Password = 'fixture-only-value'
            $refused = $false
            try { Add-VaultFixtureIntent -Journal $journal -Kind Account -Name 'FVGateA_261003' }
            catch { $refused = $true }
            $original = Read-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            @{ Refused = $refused; Count = $original.Resources.Count; ContainsPassword = ([IO.File]::ReadAllText($journal.Path).Contains('fixture-only-value')) } | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.Equal(0, result.GetProperty("Count").GetInt32());
        Assert.False(result.GetProperty("ContainsPassword").GetBoolean());
    }

    [Fact]
    public async Task Fixture_cleanup_refuses_a_junction_and_preserves_its_external_target()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal = New-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            Complete-VaultFixtureJournal -Journal $journal
            $outside = Join-Path $parent 'external-target'
            New-Item -ItemType Directory -Path $outside | Out-Null
            [IO.File]::WriteAllText((Join-Path $outside 'sentinel.txt'), 'preserve these bytes')
            $link = Join-Path $root 'untrusted-output-link'
            New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
            $refused = $false
            try { Remove-VaultFixtureTree -Root $root -Parent $parent -FixtureId $fixtureId }
            catch { $refused = $true }
            $bytes = [IO.File]::ReadAllText((Join-Path $outside 'sentinel.txt'))
            Remove-Item -LiteralPath $link
            @{ Refused = $refused; Bytes = $bytes; RootRetained = (Test-Path -LiteralPath $root) } | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.Equal("preserve these bytes", result.GetProperty("Bytes").GetString());
        Assert.True(result.GetProperty("RootRetained").GetBoolean());
    }

    [Fact]
    public async Task Completed_fixture_cleanup_removes_its_root_and_preserves_a_sibling()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal = New-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            $sentinel = Join-Path $parent 'outside.txt'
            [IO.File]::WriteAllText($sentinel, 'untouched')
            Complete-VaultFixtureJournal -Journal $journal
            Remove-VaultFixtureTree -Root $root -Parent $parent -FixtureId $fixtureId
            @{ Removed = (-not (Test-Path -LiteralPath $root)); Sibling = [IO.File]::ReadAllText($sentinel) } | ConvertTo-Json -Compress
            """);
        Assert.True(result.GetProperty("Removed").GetBoolean());
        Assert.Equal("untouched", result.GetProperty("Sibling").GetString());
    }

    [Fact]
    public async Task Reopened_process_identity_can_be_verified_and_removed_without_losing_its_timestamp()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $journal = New-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
            Add-VaultFixtureIntent -Journal $journal -Kind Process -Name 'owned-sleeper'
            $child = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') -WindowStyle Hidden -PassThru
            try {
                $identity = Get-VaultFixtureProcessIdentity -Process $child
                Set-VaultFixtureResourceState -Journal $journal -Kind Process -Name 'owned-sleeper' -State Created -Identity $identity
                $reopened = Read-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
                Stop-VaultFixtureProcess -Identity $reopened.Resources[0].Identity
                $exited = $child.WaitForExit(5000)
                Set-VaultFixtureResourceState -Journal $reopened -Kind Process -Name 'owned-sleeper' -State Removed -Identity $identity
                Complete-VaultFixtureJournal -Journal $reopened
                $completed = Read-VaultFixtureJournal -Root $root -Parent $parent -FixtureId $fixtureId
                @{ Exited = $exited; State = $completed.State; ResourceState = $completed.Resources[0].State } | ConvertTo-Json -Compress
            } finally {
                if (-not $child.HasExited) { $child.Kill($true) }
                if (-not $child.WaitForExit(5000)) { throw 'Owned child did not exit.' }
                $child.Dispose()
            }
            """);
        Assert.True(result.GetProperty("Exited").GetBoolean());
        Assert.Equal("Complete", result.GetProperty("State").GetString());
        Assert.Equal("Removed", result.GetProperty("ResourceState").GetString());
    }

    [Fact]
    public async Task Process_cleanup_refuses_wrong_identity_then_stops_the_owned_process()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $child = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') -WindowStyle Hidden -PassThru
            try {
                $identity = Get-VaultFixtureProcessIdentity -Process $child
                $wrong = $identity.Clone()
                $wrong.StartedUtc = ([DateTime]::Parse($identity.StartedUtc).AddMinutes(-1)).ToString('o')
                $refused = $false
                try { Stop-VaultFixtureProcess -Identity $wrong }
                catch { $refused = $true }
                $child.Refresh()
                $aliveAfterRefusal = -not $child.HasExited
                Stop-VaultFixtureProcess -Identity $identity
                $exited = $child.WaitForExit(5000)
                @{ Refused = $refused; AliveAfterRefusal = $aliveAfterRefusal; Exited = $exited } | ConvertTo-Json -Compress
            } finally {
                if (-not $child.HasExited) { $child.Kill($true) }
                if (-not $child.WaitForExit(5000)) { throw 'Owned child did not exit.' }
                $child.Dispose()
            }
            """);
        Assert.True(result.GetProperty("Refused").GetBoolean());
        Assert.True(result.GetProperty("AliveAfterRefusal").GetBoolean());
        Assert.True(result.GetProperty("Exited").GetBoolean());
    }

    [Theory]
    [InlineData(false, true, "UninstallNew,InstallOld")]
    [InlineData(false, false, "InstallOld")]
    [InlineData(true, false, "")]
    [InlineData(true, true, "Refused")]
    public async Task Presentation_rollback_uses_actual_settled_registration_and_restores_a_missing_product(bool oldInstalled, bool newInstalled, string expected)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module))
            . (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/update-presentation.ps1')
            $script:calls=[Collections.Generic.List[string]]::new()
            $script:oldInstalled=OLD_VALUE;$script:newInstalled=NEW_VALUE
            function Get-PresentationRegistration { @{Old=$script:oldInstalled;New=$script:newInstalled} }
            function Assert-PresentationSettled {param($Context)}
            function Invoke-PresentationMsi {param($Context,$Phase)
                $script:calls.Add($Phase)
                if($Phase -eq 'UninstallNew'){$script:newInstalled=$false}
                if($Phase -eq 'InstallOld'){$script:oldInstalled=$true}
            }
            $failure=$null;try{Restore-PresentationProduct @{}}catch{$failure=$_.Exception.Message}
            @{Calls=[string]::Join(',',$script:calls);Failure=$failure;Old=$script:oldInstalled;New=$script:newInstalled}|ConvertTo-Json -Compress
            """.Replace("OLD_VALUE", oldInstalled ? "$true" : "$false", StringComparison.Ordinal)
                .Replace("NEW_VALUE", newInstalled ? "$true" : "$false", StringComparison.Ordinal));
        if (expected == "Refused")
        {
            Assert.Contains("parallel", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
            Assert.Equal("", result.GetProperty("Calls").GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
            Assert.Equal(expected, result.GetProperty("Calls").GetString());
            Assert.True(result.GetProperty("Old").GetBoolean());
            Assert.False(result.GetProperty("New").GetBoolean());
        }
    }

    [Fact]
    public async Task Presentation_rollback_passes_the_frozen_old_and_new_candidate_identity_to_every_registration_check()
    {
        using var fixture=new ScriptFixture();
        var result=await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module))
            . (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/update-presentation.ps1')
            $script:oldInstalled=$false;$script:newInstalled=$true;$script:checks=0
            $context=@{OldProductCode='{0D47E056-CFFC-4BA4-91A6-3CD47F3FD688}';NewProductCode='{00000000-0000-0000-0000-000000000008}'}
            function Assert-PresentationSettled {param($Context)}
            function Get-PresentationRegistration {param($Context)
                if($null -eq $Context -or $Context.OldProductCode -cne '{0D47E056-CFFC-4BA4-91A6-3CD47F3FD688}' -or
                    $Context.NewProductCode -cne '{00000000-0000-0000-0000-000000000008}'){throw 'Rollback lost the frozen candidate identity.'}
                $script:checks++;@{Old=$script:oldInstalled;New=$script:newInstalled}
            }
            function Invoke-PresentationMsi {param($Context,$Phase)
                if($Phase -eq 'UninstallNew'){$script:newInstalled=$false}
                elseif($Phase -eq 'InstallOld'){$script:oldInstalled=$true}
                else{throw 'Unexpected rollback effect.'}
            }
            $failure=$null;try{Restore-PresentationProduct $context}catch{$failure=$_.Exception.Message}
            @{Failure=$failure;Checks=$script:checks;Old=$script:oldInstalled;New=$script:newInstalled}|ConvertTo-Json -Compress
            """);
        Assert.Equal(JsonValueKind.Null,result.GetProperty("Failure").ValueKind);
        Assert.Equal(3,result.GetProperty("Checks").GetInt32());
        Assert.True(result.GetProperty("Old").GetBoolean());
        Assert.False(result.GetProperty("New").GetBoolean());
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("unknown", false)]
    public async Task Controls_downgrade_requires_joined_service_and_no_unresolved_deletion(string outcome,bool expectedRestore)
    {
        using var fixture=new ScriptFixture();
        var result=await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module))
            . (Join-Path $repo 'docs/verification/2026-10-08-next004-controls/update-controls.ps1')
            $script:events=[Collections.Generic.List[string]]::new()
            function Get-Service {param($Name) @{Status='Running'}}
            function Stop-ControlsServiceJoined {param($Context) $script:events.Add('joined')}
            function Invoke-ControlsRollbackInspection {param($Context)
                if($script:events.Count -ne 1 -or $script:events[0] -ne 'joined'){throw 'Inspection ran before quiescence.'}
                $script:events.Add('inspect')
                if('OUTCOME' -eq 'unknown'){throw 'Read-only inspection failed.'}
                @{PendingHistoryDeletions=[int]'OUTCOME';OwnerSid='creator';GrantCount=0}
            }
            function Assert-ControlsClientRecordsResolved {param($StateRoot) $script:events.Add('client-records')}
            function Restore-PresentationProduct {param($Context) $script:events.Add('restore')}
            $failure=$null;try{Restore-ControlsProduct @{OperatorSid='creator'}}catch{$failure=$_.Exception.Message}
            @{Failure=$failure;Events=$script:events}|ConvertTo-Json -Compress
            """.Replace("OUTCOME",outcome,StringComparison.Ordinal));
        Assert.Equal(expectedRestore,result.GetProperty("Failure").ValueKind==JsonValueKind.Null);
        Assert.Equal(expectedRestore ? ["joined","inspect","client-records","restore"] : ["joined","inspect"],
            result.GetProperty("Events").EnumerateArray().Select(item=>item.GetString()).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controls_downgrade_preserves_and_refuses_every_pending_client_record(bool recordExists)
    {
        using var fixture=new ScriptFixture();
        var result=await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module))
            . (Join-Path $repo 'docs/verification/2026-10-08-next004-controls/update-controls.ps1')
            $path=Join-Path $root 'pending-protection-save.json'
            if(RECORD){Set-Content -LiteralPath $path -Value '{"origin":2,"historyDeletionFingerprint":"preserve even malformed records"}'}
            $before=if(RECORD){(Get-FileHash -LiteralPath $path).Hash}else{$null}
            $failure=$null;try{Assert-ControlsClientRecordsResolved $root}catch{$failure=$_.Exception.Message}
            @{Failure=$failure;Preserved=if(RECORD){(Get-FileHash -LiteralPath $path).Hash -ceq $before}else{-not(Test-Path -LiteralPath $path)}}|ConvertTo-Json -Compress
            """.Replace("RECORD",recordExists ? "$true" : "$false",StringComparison.Ordinal));
        Assert.Equal(!recordExists,result.GetProperty("Failure").ValueKind==JsonValueKind.Null);
        Assert.True(result.GetProperty("Preserved").GetBoolean());
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("uncertain")]
    [InlineData("reboot")]
    public async Task Presentation_update_blocks_unresolved_installer_state_before_rollback(string scenario)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $repo=Split-Path (Split-Path (Split-Path $module))
            . (Join-Path $repo 'docs/verification/2026-10-05-next002-rollout-preparation/update-presentation.ps1')
            $context=@{WorkRoot=$root};$script:effects=0
            function Get-PresentationRegistration { $script:effects++;@{Old=$false;New=$true} }
            function Invoke-PresentationMsi {param($Context,$Phase) $script:effects++}
            $scenario='SCENARIO_VALUE'
            $name=if($scenario -eq 'intent'){'Update-intent.json'}elseif($scenario -eq 'uncertain'){'installer-uncertain.json'}else{'reboot-required.json'}
            [IO.File]::WriteAllText((Join-Path $root $name),'{}')
            $failure=$null;try{Restore-PresentationProduct $context}catch{$failure=$_.Exception.Message}
            @{Failure=$failure;Effects=$script:effects}|ConvertTo-Json -Compress
            """.Replace("SCENARIO_VALUE", scenario, StringComparison.Ordinal));
        Assert.Contains("unresolved", result.GetProperty("Failure").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, result.GetProperty("Effects").GetInt32());
    }
    private sealed class ScriptFixture : IDisposable
    {
        private readonly string parent;
        internal string Root { get; }

        internal ScriptFixture()
        {
            // Every generated script can run elevated. Keep its source under the protected
            // profile instead of this machine's redirected TEMP on a writable drive.
            var parentRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            parent = Path.Combine(parentRoot, $"FluxVault.WindowsFixtureTools.{Guid.NewGuid():N}");
            Root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal async Task<JsonElement> RunAsync(string body, bool simulateCommissionMembership = false)
        {
            var module = FindModule();
            var script = Path.Combine(parent, $"case-{Guid.NewGuid():N}.ps1");
            // Explicit synthetic orchestration cases supply only the accepted group
            // prerequisite. Native ACLs, hashes, receipts and admission remain real.
            // Other tests, including direct-membership refusal, use the actual lookup.
            var membership = simulateCommissionMembership ? """
                & (Get-Module vault-windows-fixture) {
                    function script:Get-LocalGroupMember {
                        param($SID)
                        if($SID -ne 'S-1-5-32-544'){throw 'Unexpected group lookup in orchestration test.'}
                        foreach($member in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
                            'S-1-5-21-136112424-624261118-1239521417-1001') | Select-Object -Unique) {
                            [pscustomobject]@{SID=[Security.Principal.SecurityIdentifier]::new($member)}
                        }
                    }
                }
                """ : string.Empty;
            await File.WriteAllTextAsync(script, $"""
                $ErrorActionPreference = 'Stop'
                $module = '{Quote(module)}'
                Import-Module $module -Force
                $parent = '{Quote(parent)}'
                $root = '{Quote(Root)}'
                $fixtureId = '{Path.GetFileName(Root)}'
                $testAssemblies = '{Quote(AppContext.BaseDirectory)}'
                {membership}
                {body}
                """);
            var start = new ProcessStartInfo("pwsh")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Fixture tooling test failed to launch.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(process.ExitCode == 0, await error);
                using var result = JsonDocument.Parse(await output);
                return result.RootElement.Clone();
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(output, error);
            }
        }

        private static string Quote(string path) => path.Replace("'", "''", StringComparison.Ordinal);

        private static string FindModule()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "eng", "fixtures", "vault-windows-fixture.psm1");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("Missing Windows fixture tooling module.");
        }

        public void Dispose()
        {
            // Refuse leftover reparse links rather than following them during test cleanup.
            var pending = new Stack<string>([parent]);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Test left a reparse link; preserve the fixture for inspection.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            Directory.Delete(parent, recursive: true);
        }
    }
}
