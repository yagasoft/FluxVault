# Retire only the two cryptographically identified artifact keys. No private export.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $repo 'eng/fixtures/vault-windows-fixture.psm1') -Force
$metadata=Get-Content (Join-Path $PSScriptRoot 'artifacts/02c4a688f1354b6ca4ad5a342e957db9/package-identity.json') -Raw|ConvertFrom-Json
$certificatePath=Join-Path $PSScriptRoot 'artifacts/02c4a688f1354b6ca4ad5a342e957db9/identity.cer'
if((Get-FileHash -LiteralPath $certificatePath).Hash -ne $metadata.CertificateSha256){throw 'Prepared public certificate changed.'}
$certificate=[Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($certificatePath)
$public=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
$expected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($public.ExportSubjectPublicKeyInfo()))
$owned=Get-Content (Join-Path $PSScriptRoot 'imported-key-ownership.json') -Raw|ConvertFrom-Json -AsHashtable
$currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$provider=[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
$record=@{State='Intent';PublicFingerprint=$expected;OwnerSid=$currentSid;Keys=@()}
$recordPath=Join-Path $PSScriptRoot 'imported-key-cleanup.json'
try {
    foreach($identity in $owned) {
        if($identity.CurrentUserSid -ne $currentSid -or $identity.ExpectedFingerprint -ne $expected){throw 'Owned key namespace/fingerprint changed.'}
        $file=Join-Path (Join-Path $env:APPDATA 'Microsoft/Crypto/Keys') $identity.UniqueName
        # Provider data may have writable Windows capability ancestors. These
        # checks corroborate identity; no execution or filesystem deletion occurs here.
        $store=[IO.Path]::GetFullPath((Join-Path $env:APPDATA 'Microsoft/Crypto/Keys'))
        if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($file)) -ne $store){throw 'Key path escaped the current-user store.'}
        for($component=[IO.Path]::GetFullPath($file);$component;$component=[IO.Path]::GetDirectoryName($component)) {
            if((Get-Item -LiteralPath $component -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Key path has a reparse component.'}
        }
        $trusted=@($currentSid,'S-1-5-18','S-1-5-32-544')
        foreach($component in @($file,$store)) {
            $acl=Get-Acl -LiteralPath $component
            if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Key data has an unrelated owner.'}
            $write=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
                [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
            foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
                if($rule.AccessControlType -eq 'Allow' -and -not($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
                    $rule.IdentityReference.Value -notin $trusted -and ($rule.FileSystemRights -band $write)){throw 'Key data permits unrelated write.'}
            }
        }
        $key=[Security.Cryptography.CngKey]::Open($identity.KeyName,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent)
        $rsa=$null
        try {
            $rsa=[Security.Cryptography.RSACng]::new($key)
            $actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo()))
            if($key.IsMachineKey -or $key.Provider.Provider -ne $provider.Provider -or $key.KeyName -ne $identity.KeyName -or
                $key.UniqueName -ne $identity.UniqueName -or $actual -ne $expected){throw 'Opened native key is not the exact fixture key.'}
            $row=@{KeyName=$key.KeyName;UniqueName=$key.UniqueName;Provider=$key.Provider.Provider;PublicFingerprint=$actual;State='Deleting'}
            $record.Keys+=@($row);$record|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $recordPath
            $key.Delete()
        } finally {if($rsa){$rsa.Dispose()};$key.Dispose()}
        if([Security.Cryptography.CngKey]::Exists($identity.KeyName,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent) -or
            (Test-Path -LiteralPath $file)){throw 'Owned native key remains.'}
        $row.State='Removed';$record|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $recordPath
    }
    $record.State='Removed';$record.PrivateMaterialExported=$false
    $record|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $recordPath
    Write-Output 'Both exact imported fixture keys are absent through native and backing-file checks.'
} finally {$public.Dispose();$certificate.Dispose()}
