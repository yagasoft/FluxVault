using System.Diagnostics;
using System.Text.Json;

namespace FluxVault.Integration.Tests;

// Current-user tooling checks. These do not claim the real A/B/SYSTEM acceptance gates.
public sealed class WindowsFixtureToolingTests
{
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

    [Fact]
    public async Task Interrupted_package_cleanup_reaches_B_after_A_is_already_unregistered()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.RunAsync("""
            $runner=Join-Path (Split-Path (Split-Path $module)) 'test-windows-database-boundary.ps1'
            $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$null)
            $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Remove-OwnedPackageUsers'},$true)
            Invoke-Expression $definition.Extent.Text
            $fixtureJournal=New-VaultFixtureJournal $root $parent $fixtureId
            $fixtureDescription='FluxVault N2 '+$fixtureId
            $fixtureCredentials=@{A='not used';B='not used'}
            $users=@{}
            foreach($actor in @('A','B')) {
                $sid='S-1-5-21-1-2-3-'+$(if($actor -eq 'A'){9198}else{9199})
                $name="FVGate${actor}_261003"
                $users[$name]=[pscustomobject]@{Name=$name;SID=[pscustomobject]@{Value=$sid};Description=$fixtureDescription}
                Add-VaultFixtureIntent $fixtureJournal Account $name
                Set-VaultFixtureResourceState $fixtureJournal Account $name Created @{Sid=$sid}
                $identity=@{Sid=$sid;PackageName=('FVGate.Package.'+$fixtureId);Publisher=('CN=FluxVault Fixture '+$fixtureId);Thumbprint=('A'*40)}
                Add-VaultFixtureIntent $fixtureJournal PackageUser "package-$actor" $identity
                Set-VaultFixtureResourceState $fixtureJournal PackageUser "package-$actor" Created $identity
            }
            $remaining=[Collections.Generic.HashSet[string]]::new();$null=$remaining.Add('B')
            $visited=[Collections.Generic.List[string]]::new()
            function Get-LocalUser {param($Name) $users[$Name]}
            function Invoke-UserActor {
                param($Actor,$HostAddress,$ClientKind)
                $visited.Add($Actor);$null=$remaining.Remove($Actor)
                [pscustomobject]@{Results=@([pscustomobject]@{Kind='Packaged';Result=[pscustomobject]@{PackageRemoved=$true;CertificateRemoved=$true}})}
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
            """);
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

        internal async Task<JsonElement> RunAsync(string body)
        {
            var module = FindModule();
            var script = Path.Combine(parent, $"case-{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(script, $"""
                $ErrorActionPreference = 'Stop'
                $module = '{Quote(module)}'
                Import-Module $module -Force
                $parent = '{Quote(parent)}'
                $root = '{Quote(Root)}'
                $fixtureId = '{Path.GetFileName(Root)}'
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
