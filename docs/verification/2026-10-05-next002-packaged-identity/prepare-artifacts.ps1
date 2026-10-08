#Requires -Version 7.2
# Artifact-only rehearsal. No account/profile/package/trust/database/service changes.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $repo 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $repo 'eng/fixtures/packaged-identity.psm1') -Force
Import-Module (Join-Path $repo 'eng/fixtures/fixture-cng-keys.psm1') -Force
Import-VaultFixtureJobType
$runner=Join-Path $repo 'eng/test-windows-database-boundary.ps1'
$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$null,[ref]$parseErrors)
if($parseErrors.Count){throw 'Prepared runner does not parse.'}
# Execute the same tool supervisor, not a substitute process launcher.
$definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-OwnedTool'},$true)
Invoke-Expression $definition.Extent.Text
$fixtureId=[guid]::NewGuid().ToString('N')
$fixtureParent='C:\ProgramData\FluxVault.Tests\NEXT002'
$fixtureRoot=Resolve-VaultFixtureRoot (Join-Path $fixtureParent $fixtureId) $fixtureParent $fixtureId
$fixturePwsh=(Get-Command pwsh).Source
$fixtureBin=Join-Path $fixtureRoot 'runtime'
$fixtureJournal=$null
$fixtureJobs=@{}
$failure=$null
$evidence=Join-Path $PSScriptRoot ('artifacts/'+$fixtureId)
if(@(Get-ChildItem -LiteralPath $fixtureParent -Directory -ErrorAction SilentlyContinue).Count){throw 'Resolve the existing private fixture first.'}
Assert-VaultFixtureTrustedPath $fixtureParent
Assert-VaultFixtureTrustedPath $fixturePwsh
New-VaultFixtureProtectedDirectory $fixtureRoot
try {
    $fixtureJournal=New-VaultFixtureJournal $fixtureRoot $fixtureParent $fixtureId
    New-VaultFixtureProtectedDirectory (Join-Path $fixtureRoot 'runtime')
    foreach($source in Get-ChildItem -LiteralPath (Join-Path $repo 'tests/FluxVault.TestHost/bin/Release/net10.0-windows') -File) {
        Assert-VaultFixtureTrustedPath $source.FullName
        Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $fixtureRoot 'runtime')
    }
    foreach($leaf in @('invoke-owned-tool.ps1','vault-windows-fixture.psm1','owned-windows-job.cs')) {
        Copy-Item -LiteralPath (Join-Path $repo ('eng/fixtures/'+$leaf)) -Destination (Join-Path $fixtureRoot ('runtime/'+$leaf))
    }
    $jobIdentity=@{KernelName='Global\FluxVault.NEXT002.'+[guid]::NewGuid().ToString('N');OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
    Add-VaultFixtureIntent $fixtureJournal Job Cluster $jobIdentity
    $fixtureJobs.Cluster=[FluxVault.Fixtures.OwnedWindowsJob]::Create($jobIdentity.KernelName,$jobIdentity.OwnerSid)
    Set-VaultFixtureResourceState $fixtureJournal Job Cluster Created $jobIdentity
    $metadata=New-VaultFixtureIdentityPackage $fixtureJournal 'E:\Windows Kits\10\bin\10.0.28000.0\x64' ${function:Invoke-OwnedTool}.GetNewClosure()
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    foreach($pair in @(@{Source='runtime/identity.msix';Target='identity.msix'},@{Source='runtime/identity.cer';Target='identity.cer'},
        @{Source='runtime/package-identity.json';Target='package-identity.json'},@{Source='catalogue/package-signing/key-owner.json';Target='signing-key-owner.json'},
        @{Source='package-sdk-provenance.json';Target='package-sdk-provenance.json'})) {
        Copy-Item -LiteralPath (Join-Path $fixtureRoot $pair.Source) -Destination (Join-Path $evidence $pair.Target)
    }
    $metadata | ConvertTo-Json -Compress
} catch {$failure=$_}
finally {
    if($fixtureJobs.ContainsKey('Cluster')) {
        try {$fixtureJobs.Cluster.StopAndJoin()} finally {$fixtureJobs.Cluster.Dispose()}
        Set-VaultFixtureResourceState $fixtureJournal Job Cluster Removed $jobIdentity
    }
    if($null -ne $fixtureJournal) {
        Remove-VaultFixtureImportedKeys $fixtureJournal
        foreach($resource in @($fixtureJournal.Resources | Where-Object {$_.Kind -eq 'Process'})) {
            if($resource.State -eq 'Created') {Stop-VaultFixtureProcessTree $resource.Identity | Out-Null;Set-VaultFixtureResourceState $fixtureJournal Process $resource.Name Removed $resource.Identity}
            elseif($resource.State -eq 'Intent') {Set-VaultFixtureIntentAbsent $fixtureJournal Process $resource.Name}
        }
        Complete-VaultFixtureJournal $fixtureJournal
        New-Item -ItemType Directory -Path $evidence -Force | Out-Null
        foreach($pair in @(@{Source='catalogue/package-signing/key-owner.json';Target='signing-key-owner.json'},@{Source='catalogue/package-signing/native-key-cleanup.json';Target='native-key-cleanup.json'},@{Source='package-sdk-provenance.json';Target='package-sdk-provenance.json'})) {
            $source=Join-Path $fixtureRoot $pair.Source
            if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $evidence $pair.Target)}
        }
        foreach($file in Get-ChildItem -LiteralPath $fixtureRoot -File | Where-Object {$_.Name -match '^(owner|tool-[0-9a-f]{32}(-child)?)\.json$'}) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidence $file.Name)
        }
        Remove-VaultFixtureTree $fixtureRoot $fixtureParent $fixtureId
        @{FixtureId=$fixtureId;RootRemoved=(-not(Test-Path -LiteralPath $fixtureRoot));OwnedJobJoined=$true;
            PackageRegistered=$false;TrustChanged=$false;AccountsCreated=$false;ProfilesCreated=$false;DatabaseCreated=$false;
            Failure=$(if($failure){$failure.Exception.Message}else{$null})} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'cleanup.json')
    } else {throw 'No ownership journal was published; preserve the private root.'}
}
if($failure){throw $failure}
