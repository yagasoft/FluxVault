#Requires -Version 7.2
param([Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][ValidateSet('A','B')][string]$Actor,
    [ValidateSet('Client','Cleanup')][string]$Action='Client')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'vault-windows-fixture.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'packaged-identity.psm1') -Force
$fixtureId=Split-Path $Root -Leaf
$Root=Resolve-VaultFixtureRoot $Root 'C:\ProgramData\FluxVault.Tests\NEXT002' $fixtureId
$configuration=Get-Content -LiteralPath (Join-Path $Root 'runtime/database-probe.json') -Raw | ConvertFrom-Json
$metadata=Get-Content -LiteralPath (Join-Path $Root 'runtime/package-identity.json') -Raw | ConvertFrom-Json
$actualSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if($actualSid -ne $configuration.Actors.$Actor -or $metadata.Version -ne 1 -or $metadata.FixtureId -ne $fixtureId -or
    $metadata.Root -ne $Root -or $metadata.PackageName -ne ('FVGate.Package.'+$fixtureId) -or
    $metadata.Publisher -ne ('CN=FluxVault Fixture '+$fixtureId) -or $metadata.Thumbprint -notmatch '^[A-F0-9]{40}$') {
    throw 'Packaged fixture identity mismatch.'
}
$runtime=Join-Path $Root 'runtime'
foreach($pair in @(@{Path='identity.msix';Hash=$metadata.PackageSha256},@{Path='identity.cer';Hash=$metadata.CertificateSha256},
    @{Path='FluxVault.TestHost.exe';Hash=$metadata.ApphostSha256})) {
    $path=Join-Path $runtime $pair.Path
    Assert-VaultFixtureTrustedPath $path
    if((Get-FileHash -LiteralPath $path).Hash -ne $pair.Hash){throw 'Packaged fixture payload changed.'}
}
$ownerPath=Join-Path $Root ("output-$Actor/package-owner.json")
$state=@{Version=1;FixtureId=$fixtureId;UserSid=$actualSid;PackageName=$metadata.PackageName;Publisher=$metadata.Publisher;
    Thumbprint=$metadata.Thumbprint;PackageFullName='';State='Intent';PackageRemoved=$false;CertificateRemoved=$false}
$certificate=[Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile((Join-Path $runtime 'identity.cer'))
$store=[Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','CurrentUser')
$child=$null
$failure=$null
$proof=$null
$identity=$null
$mayCleanup=$Action -eq 'Cleanup'
$storeOpened=$false
try {
    if($certificate.Thumbprint -ne $metadata.Thumbprint -or $certificate.Subject -ne $metadata.Publisher){throw 'Wrong fixture certificate.'}
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $storeOpened=$true
    if($Action -eq 'Client') {
        if(@(Get-AppxPackage -Name $metadata.PackageName).Count -or
            $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$metadata.Thumbprint,$false).Count) {
            throw 'Owned package or certificate collision; no existing resource was changed.'
        }
        Write-VaultFixturePackageRecord $ownerPath $state
        $mayCleanup=$true
        $store.Add($certificate)
        $state.State='CertificateCreated';Write-VaultFixturePackageRecord $ownerPath $state
        Add-AppxPackage -Path (Join-Path $runtime 'identity.msix') -ExternalLocation $runtime -ErrorAction Stop
        $package=@(Get-AppxPackage -Name $metadata.PackageName)
        if($package.Count -ne 1 -or $package[0].Publisher -ne $metadata.Publisher -or
            $package[0].PackageFullName -notlike ($metadata.PackageName+'_1.0.0.0_x64__*')){throw 'Wrong registered identity.'}
        $state.PackageFullName=$package[0].PackageFullName;$state.State='PackageCreated'
        Write-VaultFixturePackageRecord $ownerPath $state
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $runtime 'FluxVault.TestHost.exe'))
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$runtime
        $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        foreach($argument in @('--mode','windows-packaged-identity','--configuration',(Join-Path $runtime 'database-probe.json'),
            '--actor',$Actor,'--package-full-name',$state.PackageFullName)){$start.ArgumentList.Add($argument)}
        $child=[Diagnostics.Process]::Start($start)
        $identity=Get-VaultFixtureLiveProcessIdentity $child
        if($null -ne $identity){($identity | ConvertTo-Json -Compress) | Add-Content -LiteralPath (Join-Path $Root ("output-$Actor/package-processes.jsonl"))}
        $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
        if(-not $child.WaitForExit(18000)){throw 'Packaged caller exceeded its finite deadline.'}
        $output=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if($child.ExitCode -ne 0){throw ('Packaged native caller failed: '+$errorText)}
        $proof=$output | ConvertFrom-Json
        if(-not $proof.NativePackagedVerified -or $proof.Actor -ne $Actor -or $proof.WindowsSid -ne $actualSid -or
            $proof.PackageFullName -ne $state.PackageFullName -or $proof.Passed -ne $(if($Actor -eq 'A'){5}else{4})){
            throw 'Packaged native proof is incomplete.'
        }
    }
} catch { $failure=$_ }
finally {
    try {
    if($child) {
        try {if(-not $child.HasExited){$child.Kill($true)};if(-not $child.WaitForExit(5000)){throw 'Packaged child remains.'}}
        finally {$child.Dispose()}
    }
        if($mayCleanup -and $storeOpened) {
        # This user's exact GUID identity only. No provisioned or other package is touched.
        foreach($package in @(Get-AppxPackage -Name $metadata.PackageName)) {
            if($package.Publisher -ne $metadata.Publisher -or
                $package.PackageFullName -notlike ($metadata.PackageName+'_1.0.0.0_x64__*')){throw 'Package cleanup identity mismatch.'}
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
        }
        if(@(Get-AppxPackage -Name $metadata.PackageName).Count){throw 'Owned per-user package remains.'}
        $state.PackageRemoved=$true
        foreach($existing in $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$metadata.Thumbprint,$false)) {
            if($existing.Subject -ne $metadata.Publisher){throw 'Certificate cleanup identity mismatch.'}
            $store.Remove($existing)
        }
        if($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$metadata.Thumbprint,$false).Count){throw 'Owned per-user certificate trust remains.'}
        $state.CertificateRemoved=$true;$state.State='Removed'
        Write-VaultFixturePackageRecord $ownerPath $state
        }
    } finally {$store.Dispose();$certificate.Dispose()}
}
if($failure){throw $failure}
@{Actor=$Actor;WindowsSid=$actualSid;Action=$Action;Proof=$proof;ProcessIdentity=$identity;PackageOwner=$state;
    PackageRemoved=$state.PackageRemoved;CertificateRemoved=$state.CertificateRemoved} | ConvertTo-Json -Depth 6
