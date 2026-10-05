#Requires -Version 7.2
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'fixture-cng-keys.psm1') -Force

function New-VaultFixtureSdkSnapshot {
    param([hashtable]$Journal,[string]$InstalledDirectory)
    $root=Resolve-VaultFixtureRoot $Journal.Root $Journal.Parent $Journal.FixtureId
    Assert-VaultFixtureTrustedPath $root
    $destination=Join-Path $root 'package-sdk'
    if(Test-Path -LiteralPath $destination){throw 'SDK snapshot collision.'}
    New-VaultFixtureProtectedDirectory $destination
    # Exact tool identities and Microsoft signing certificates observed for SDK 28000.
    # Source paths are untrusted. Authenticate the protected destination bytes before use.
    $identities=@{
        'mt.exe'='mt2.exe';'makeappx.exe'='MakeAppx.exe';'signtool.exe'='SIGNTOOL.EXE';
        'appxpackaging.dll'='AppxPackaging.dll';'appxsip.dll'='AppxSip.dll';
        'mssign32.dll'='MSSIGN32.DLL';'wintrust.dll'='WINTRUST.DLL';
        'mrmsupport.dll'='MrmSupport.dll';'opcservices.dll'='OpcServices.dll'
        'midlrtmd.dll'='midlrtmd.dll'
    }
    $signers=@('6ACE61BAE3F09F4DD2697806D73E022CBFE70EB4','F6EECCC7FF116889C2D5466AE7243D7AA7698689')
    $provenance=[Collections.Generic.List[hashtable]]::new()
    foreach($name in @($identities.Keys | Sort-Object)) {
        $source=Join-Path $InstalledDirectory $name
        $target=Join-Path $destination $name
        $input=[IO.File]::Open($source,'Open','Read','Read')
        try {
            if($input.Length -le 0 -or $input.Length -gt 16777216){throw 'SDK payload exceeds its file bound.'}
            $output=[IO.File]::Open($target,'CreateNew','Write','None')
            try {$input.CopyTo($output);$output.Flush($true)} finally {$output.Dispose()}
        } finally {$input.Dispose()}
        Assert-VaultFixtureTrustedPath $target
        $signature=Get-AuthenticodeSignature -LiteralPath $target
        $version=(Get-Item -LiteralPath $target).VersionInfo
        if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -notin $signers -or
            $version.OriginalFilename -ne $identities[$name] -or $version.CompanyName -ne 'Microsoft Corporation') {
            throw 'Copied SDK signature or tool identity is not the reviewed Microsoft payload.'
        }
        $provenance.Add(@{Name=$name;Sha256=(Get-FileHash -LiteralPath $target).Hash;Bytes=(Get-Item -LiteralPath $target).Length;
            OriginalFilename=$version.OriginalFilename;SignerThumbprint=$signature.SignerCertificate.Thumbprint;Status='Valid'})
    }
    # The signed tools require private SxS assemblies. Generate reviewed XML;
    # never copy executable search/redirection metadata from the writable SDK.
    $assemblies=@(
        @{Name='Microsoft.Windows.Build.Appx.AppxPackaging.dll';File='appxpackaging.dll';Depends='Microsoft.Windows.Build.Appx.OpcServices.dll';Classes=@(
            '5842a140-ff9f-4166-8f5c-62f5b7b0c781','dc664fdd-d868-46ee-8780-8d196cb739f7','378e0446-5384-43b7-8877-e7dbdd883446',
            '48de828c-730c-49af-ae84-759c609911ee','f004f2ca-aebc-4b0d-bf58-e516d5bcc0ab','7f00fa1e-9820-47b1-9c4f-8701f1432177',
            '0cf07551-eef2-420c-b5ab-7e4feb2249cf','50ca0a46-1588-4161-8ed2-ef9e469ced5d','fb1b3839-09da-404f-b002-9cbb8da5ca4f')},
        @{Name='Microsoft.Windows.Build.Appx.AppxSip.dll';File='appxsip.dll';Depends='Microsoft.Windows.Build.Appx.AppxPackaging.dll';Classes=@()},
        @{Name='Microsoft.Windows.Build.Appx.OpcServices.dll';File='opcservices.dll';Depends='';Classes=@('6b2d6ba0-9f3e-4f27-920b-313cc426a39e')},
        @{Name='Microsoft.Windows.Build.Signing.mssign32.dll';File='mssign32.dll';Depends='';Classes=@()},
        @{Name='Microsoft.Windows.Build.Signing.wintrust.dll';File='wintrust.dll';Depends='';Classes=@()})
    foreach($assembly in $assemblies) {
        $classes=@($assembly.Classes | ForEach-Object {'<comClass clsid="{'+$_+'}" threadingModel="Both" />'}) -join ''
        $dependency=if($assembly.Depends){'<dependency><dependentAssembly><assemblyIdentity name="'+$assembly.Depends+'" version="0.0.0.0" /></dependentAssembly></dependency>'}else{''}
        $xml='<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><assemblyIdentity name="'+$assembly.Name+'" version="0.0.0.0" /><file name="'+$assembly.File+'">'+$classes+'</file>'+$dependency+'</assembly>'
        $path=Join-Path $destination ($assembly.Name+'.manifest')
        [IO.File]::WriteAllText($path,$xml)
        $provenance.Add(@{Name=($assembly.Name+'.manifest');Sha256=(Get-FileHash -LiteralPath $path).Hash;Bytes=(Get-Item -LiteralPath $path).Length;Origin='Fixed reviewed SxS manifest; authenticated same-directory DLLs only'})
    }
    $dependencies=@('Microsoft.Windows.Build.Signing.mssign32.dll','Microsoft.Windows.Build.Signing.wintrust.dll','Microsoft.Windows.Build.Appx.AppxSip.dll') |
        ForEach-Object {'<dependency><dependentAssembly><assemblyIdentity name="'+$_+'" version="0.0.0.0" /></dependentAssembly></dependency>'}
    $path=Join-Path $destination 'signtool.exe.manifest'
    [IO.File]::WriteAllText($path,('<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><assemblyIdentity name="FluxVault.Fixture.SignTool" version="0.0.0.0" />'+($dependencies -join '')+'</assembly>'))
    $provenance.Add(@{Name='signtool.exe.manifest';Sha256=(Get-FileHash -LiteralPath $path).Hash;Bytes=(Get-Item -LiteralPath $path).Length;Origin='Fixed reviewed SxS manifest; authenticated same-directory DLLs only'})
    @{Version=1;Authentication='Valid Windows Authenticode trust, pinned Microsoft signer and signed tool identity on protected copied bytes; fixed generated SxS metadata';Files=@($provenance)} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'package-sdk-provenance.json')
    return $destination
}

function Write-VaultFixturePackageRecord {
    param([string]$Path,[hashtable]$Record)
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Record | ConvertTo-Json -Depth 4))
    if($bytes.Length -gt 16384){throw 'Package ownership record exceeds its bound.'}
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {$stream.Write($bytes);$stream.Flush($true)} finally {$stream.Dispose()}
        [IO.File]::Move($temporary,$Path,$true)
    } finally {if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}

function New-VaultFixtureIdentityPackage {
    param([hashtable]$Journal,[string]$SdkDirectory,[scriptblock]$InvokeTool)
    $root=Resolve-VaultFixtureRoot $Journal.Root $Journal.Parent $Journal.FixtureId
    $runtime=Join-Path $root 'runtime'
    $packageName='FVGate.Package.'+$Journal.FixtureId
    $publisher='CN=FluxVault Fixture '+$Journal.FixtureId
    $SdkDirectory=New-VaultFixtureSdkSnapshot $Journal $SdkDirectory
    $staging=Join-Path $root 'catalogue/package-signing'
    New-VaultFixtureProtectedDirectory $staging
    $keyPath=Join-Path $staging 'ephemeral-key.pfx'
    $manifestPath=Join-Path $staging 'application.manifest'
    $packageDirectory=Join-Path $staging 'identity'
    New-VaultFixtureProtectedDirectory $packageDirectory
    $apphost=Join-Path $runtime 'FluxVault.TestHost.exe'
    $assets=Join-Path $packageDirectory 'Assets'
    New-VaultFixtureProtectedDirectory $assets
    $logo=Join-Path $PSScriptRoot '../../src/FluxVault.App/Assets/YagasoftLogo.png'
    Assert-VaultFixtureTrustedPath $logo
    Copy-Item -LiteralPath $logo -Destination (Join-Path $assets 'logo.png')
    $packagePath=Join-Path $runtime 'identity.msix'
    $certificatePath=Join-Path $runtime 'identity.cer'
    foreach($path in @($keyPath,$packagePath,$certificatePath)){if(Test-Path -LiteralPath $path){throw 'Identity-package asset collision.'}}
    $keyState=Join-Path $staging 'key-owner.json'
    Write-VaultFixturePackageRecord $keyState @{Path=$keyPath;Algorithm='RSA-2048';Persistence='Ephemeral';State='Intent'}
    $rsa=[Security.Cryptography.RSA]::Create(2048)
    $certificate=$null
    try {
        $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new($publisher,$rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
        $usages=[Security.Cryptography.OidCollection]::new()
        $null=$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages,$false))
        $certificate=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddHours(3))
        # No certificate trust is added to the runner's stores. SignTool may persist
        # current-user software keys; intent and native recovery below remove them.
        # The transient PFX stays private and is never copied into evidence.
        [IO.File]::WriteAllBytes($keyPath,$certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,''))
        Write-VaultFixturePackageRecord $keyState @{Path=$keyPath;Algorithm='RSA-2048';Persistence='Ephemeral';State='Created'}
        [IO.File]::WriteAllBytes($certificatePath,$certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $packageManifest=@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap uap10 rescap">
<Identity Name="$packageName" Publisher="$publisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
<Properties><DisplayName>FluxVault identity fixture</DisplayName><PublisherDisplayName>FluxVault fixture</PublisherDisplayName><Logo>Assets\logo.png</Logo><uap10:AllowExternalContent>true</uap10:AllowExternalContent></Properties>
<Resources><Resource Language="en-us" /></Resources>
<Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
<Capabilities><rescap:Capability Name="runFullTrust" /><rescap:Capability Name="unvirtualizedResources" /></Capabilities>
<Applications><Application Id="Probe" Executable="FluxVault.TestHost.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App"><uap:VisualElements AppListEntry="none" DisplayName="FluxVault identity fixture" Description="Disposable identity validation" BackgroundColor="transparent" Square150x150Logo="Assets\logo.png" Square44x44Logo="Assets\logo.png" /></Application></Applications>
</Package>
"@
        [IO.File]::WriteAllText((Join-Path $packageDirectory 'AppxManifest.xml'),$packageManifest)
        [IO.File]::WriteAllText($manifestPath,@"
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1"><assemblyIdentity version="1.0.0.0" name="FluxVault.IdentityFixture" /><msix xmlns="urn:schemas-microsoft-com:msix.v1" publisher="$publisher" packageName="$packageName" applicationId="Probe" /><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo></assembly>
"@)
        foreach($call in @(
            @{Executable='mt.exe';Arguments=@('-manifest',$manifestPath,('-outputresource:'+$apphost+';#1'))},
            @{Executable='makeappx.exe';Arguments=@('pack','/o','/nv','/d',$packageDirectory,'/p',$packagePath)},
            @{Executable='signtool.exe';Arguments=@('sign','/fd','SHA256','/f',$keyPath,$apphost)},
            @{Executable='signtool.exe';Arguments=@('sign','/fd','SHA256','/f',$keyPath,$packagePath)})) {
            if($call.Executable -eq 'signtool.exe') {
                $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo()))
                $null=New-VaultFixtureKeyImportIntent $Journal $fingerprint
            }
            try {
                $result=& $InvokeTool (Join-Path $SdkDirectory $call.Executable) $call.Arguments
                if($result.ExitCode -ne 0){throw ('Identity-package preparation failed: '+$call.Executable+' '+$result.Error)}
            } finally {
                if($call.Executable -eq 'signtool.exe'){Remove-VaultFixtureImportedKeys $Journal}
            }
        }
        $metadata=@{Version=1;FixtureId=$Journal.FixtureId;Root=$root;PackageName=$packageName;Publisher=$publisher;
            Thumbprint=$certificate.Thumbprint;PackageSha256=(Get-FileHash -LiteralPath $packagePath).Hash;
            CertificateSha256=(Get-FileHash -LiteralPath $certificatePath).Hash;ApphostSha256=(Get-FileHash -LiteralPath $apphost).Hash}
        Write-VaultFixturePackageRecord (Join-Path $runtime 'package-identity.json') $metadata
        return $metadata
    } finally {
        try {
            if(Test-Path -LiteralPath $keyPath){Remove-Item -LiteralPath $keyPath -Force}
            if(Test-Path -LiteralPath $keyPath){throw 'The owned transient signing key remains.'}
        } finally {if($certificate){$certificate.Dispose()};$rsa.Dispose()}
        Write-VaultFixturePackageRecord $keyState @{Path=$keyPath;Algorithm='RSA-2048';Persistence='Ephemeral';State='Removed'}
    }
}

function New-VaultFixtureMachineTrustStore {
    return [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
}

function Invoke-VaultFixtureMachineTrust {
    param([hashtable]$Journal,[ValidateSet('Add','Remove')][string]$Mode,[switch]$Approved)
    $root=Resolve-VaultFixtureRoot $Journal.Root $Journal.Parent $Journal.FixtureId
    Assert-VaultFixtureTrustedPath $root
    $path=Join-Path $root 'catalogue/package-signing/machine-trust-owner.json'
    if($Mode -eq 'Add') {
        if(-not $Approved){throw 'Explicit machine-certificate scope approval is required.'}
        if(Test-Path -LiteralPath $path){throw 'Machine trust ownership already exists; recover it first.'}
        $metadataPath=Join-Path $root 'runtime/package-identity.json'
        Assert-VaultFixtureTrustedPath $metadataPath
        $metadata=Get-Content -LiteralPath $metadataPath -Raw|ConvertFrom-Json -AsHashtable
        if($metadata.Version -ne 1 -or $metadata.FixtureId -ne $Journal.FixtureId -or $metadata.Root -ne $root -or
            $metadata.PackageName -ne ('FVGate.Package.'+$Journal.FixtureId) -or
            $metadata.Publisher -ne ('CN=FluxVault Fixture '+$Journal.FixtureId) -or
            $metadata.Thumbprint -notmatch '^[A-F0-9]{40}$' -or $metadata.CertificateSha256 -notmatch '^[A-F0-9]{64}$') {
            throw 'Machine trust package binding is invalid.'
        }
        $keyPath=Join-Path $root 'catalogue/package-signing/native-key-cleanup.json'
        Assert-VaultFixtureTrustedPath $keyPath
        $keys=Get-Content -LiteralPath $keyPath -Raw|ConvertFrom-Json -AsHashtable
        if($keys.State -ne 'Removed' -or $keys.FixtureId -ne $Journal.FixtureId -or
            $keys.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
            $keys.Provider -ne 'Microsoft Software Key Storage Provider' -or
            $keys.PublicFingerprint -notmatch '^[A-F0-9]{64}$' -or $keys.OwnedKeys.Count -gt 8 -or
            @($keys.OwnedKeys|Where-Object State -ne 'Removed').Count -or
            (Test-Path -LiteralPath (Join-Path $root 'catalogue/package-signing/ephemeral-key.pfx'))) {
            throw 'Signing private keys have not been retired.'
        }
        $certificatePath=Join-Path $root 'runtime/identity.cer'
        Assert-VaultFixtureTrustedPath $certificatePath
        if((Get-FileHash -LiteralPath $certificatePath).Hash -ne $metadata.CertificateSha256){throw 'Machine trust public certificate bytes changed.'}
        $record=@{Version=1;FixtureId=$Journal.FixtureId;Root=$root;Store='LocalMachine/TrustedPeople';
            OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;Publisher=$metadata.Publisher;
            Thumbprint=$metadata.Thumbprint;CertificateSha256=$metadata.CertificateSha256;State='Intent'}
        $certificate=[Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($certificatePath)
    } else {
        if(-not(Test-Path -LiteralPath $path)){return}
        Assert-VaultFixtureTrustedPath $path
        $record=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable
        if($record.Version -ne 1 -or $record.FixtureId -ne $Journal.FixtureId -or $record.Root -ne $root -or
            $record.Store -ne 'LocalMachine/TrustedPeople' -or
            $record.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
            $record.Publisher -ne ('CN=FluxVault Fixture '+$Journal.FixtureId) -or
            $record.Thumbprint -notmatch '^[A-F0-9]{40}$' -or $record.CertificateSha256 -notmatch '^[A-F0-9]{64}$' -or
            $record.State -notin @('Intent','Created','Removed')){throw 'Machine trust recovery binding is invalid.'}
        $certificate=$null
    }
    $store=New-VaultFixtureMachineTrustStore
    $matches=@()
    try {
        if($Mode -eq 'Add') {
            $rsa=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
            try {
                if($null -eq $rsa -or
                    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())) -ne $keys.PublicFingerprint){throw 'Signing private keys have not been retired.'}
            } finally {if($rsa){$rsa.Dispose()}}
            foreach($key in $keys.OwnedKeys) {
                $parsedKey=[guid]::Empty
                if($key.PublicFingerprint -ne $keys.PublicFingerprint -or
                    -not [guid]::TryParseExact($key.KeyName,'B',[ref]$parsedKey) -or $parsedKey -eq [guid]::Empty -or
                    [Security.Cryptography.CngKey]::Exists($key.KeyName,[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider,[Security.Cryptography.CngKeyOpenOptions]::Silent)) {
                    throw 'Signing private keys have not been retired.'
                }
            }
            $constraints=@($certificate.Extensions|Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]})
            $usage=@($certificate.Extensions|Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension]})
            $enhanced=@($certificate.Extensions|Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]})
            if($certificate.HasPrivateKey -or $certificate.Thumbprint -ne $record.Thumbprint -or $certificate.Subject -ne $record.Publisher -or
                $constraints.Count -ne 1 -or $constraints[0].CertificateAuthority -or $usage.Count -ne 1 -or
                $usage[0].KeyUsages -ne [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -or
                $enhanced.Count -ne 1 -or $enhanced[0].EnhancedKeyUsages.Count -ne 1 -or
                $enhanced[0].EnhancedKeyUsages[0].Value -ne '1.3.6.1.5.5.7.3.3' -or
                $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or
                ($certificate.NotAfter-$certificate.NotBefore).TotalHours -gt 4){throw 'Only the short-lived public code-signing leaf may be trusted.'}
        }
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $matches=@($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$record.Thumbprint,$false))
        if($Mode -eq 'Remove' -and $record.State -eq 'Removed') {
            if($matches.Count){throw 'A retired machine certificate reappeared; preserve it for review.'}
            return
        }
        if($Mode -eq 'Add') {
            if($matches.Count){throw 'Existing machine certificate is not owned; no trust was changed.'}
            Write-VaultFixturePackageRecord $path $record
            $store.Add($certificate)
            $matches=@($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$record.Thumbprint,$false))
            if($matches.Count -ne 1){throw 'Machine certificate publication is incomplete.'}
        }
        foreach($item in $matches) {
            if($item.HasPrivateKey -or $item.Subject -ne $record.Publisher -or
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($item.RawData)) -ne $record.CertificateSha256) {
                throw 'Machine certificate identity changed; preserve it for review.'
            }
            if($Mode -eq 'Remove'){$store.Remove($item)}
        }
        if($Mode -eq 'Remove' -and $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$record.Thumbprint,$false).Count){throw 'Owned machine certificate remains.'}
        $record.State=if($Mode -eq 'Add'){'Created'}else{'Removed'}
        Write-VaultFixturePackageRecord $path $record
    } finally {foreach($item in $matches){$item.Dispose()};$store.Dispose();if($certificate){$certificate.Dispose()}}
}

Export-ModuleMember -Function New-VaultFixtureIdentityPackage,Write-VaultFixturePackageRecord,New-VaultFixtureSdkSnapshot,Invoke-VaultFixtureMachineTrust
