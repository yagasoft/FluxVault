#Requires -Version 7.2
Set-StrictMode -Version Latest

function Get-VaultFixtureKeyNames {
    if(-not ('FluxVault.Fixtures.FixtureCngKeys' -as [type])) {
        $source=Join-Path $PSScriptRoot 'fixture-cng-keys.cs'
        Assert-VaultFixtureTrustedPath $source
        Add-Type -Path $source
    }
    return [FluxVault.Fixtures.FixtureCngKeys]::CurrentUserNames()
}

function New-VaultFixtureKeyImportIntent {
    param([hashtable]$Journal,[string]$PublicFingerprint)
    $root=Resolve-VaultFixtureRoot $Journal.Root $Journal.Parent $Journal.FixtureId
    Assert-VaultFixtureTrustedPath $root
    if($PublicFingerprint -notmatch '^[A-F0-9]{64}$'){throw 'A canonical RSA public-key fingerprint is required.'}
    $path=Join-Path $root 'catalogue/package-signing/native-key-recovery.json'
    $oldKeys=@()
    if(Test-Path -LiteralPath $path) {
        Assert-VaultFixtureTrustedPath $path
        $old=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable -Depth 5
        if($old.State -ne 'Removed' -or $old.FixtureId -ne $Journal.FixtureId -or
            $old.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or $old.PublicFingerprint -ne $PublicFingerprint){throw 'Prior key import requires recovery.'}
        $oldKeys=$old.OwnedKeys
    }
    $record=@{Version=1;FixtureId=$Journal.FixtureId;OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
        Provider='Microsoft Software Key Storage Provider';PublicFingerprint=$PublicFingerprint;
        BaselineNames=@(Get-VaultFixtureKeyNames);OwnedKeys=@($oldKeys);State='Intent'}
    # This private record may contain unrelated key names. Never publish it as evidence.
    Write-VaultFixturePackageRecord $path $record
    return $path
}

function Remove-VaultFixtureImportedKeys {
    param([hashtable]$Journal)
    $root=Resolve-VaultFixtureRoot $Journal.Root $Journal.Parent $Journal.FixtureId
    $path=Join-Path $root 'catalogue/package-signing/native-key-recovery.json'
    if(-not(Test-Path -LiteralPath $path)){return}
    Assert-VaultFixtureTrustedPath $path
    if((Get-Item -LiteralPath $path).Length -gt 131072){throw 'Key-import recovery record exceeds its bound.'}
    $record=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 5
    if($record.Version -ne 1 -or $record.FixtureId -ne $Journal.FixtureId -or
        $record.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
        $record.Provider -ne 'Microsoft Software Key Storage Provider' -or
        $record.PublicFingerprint -notmatch '^[A-F0-9]{64}$' -or $record.BaselineNames.Count -gt 1024 -or $record.OwnedKeys.Count -gt 8){throw 'Key-import recovery identity changed.'}
    $provider=[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    foreach($name in @(Get-VaultFixtureKeyNames)) {
        if($name -in $record.BaselineNames){continue}
        $key=[Security.Cryptography.CngKey]::Open($name,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent)
        $rsa=$null
        try {
            if($key.AlgorithmGroup -ne [Security.Cryptography.CngAlgorithmGroup]::Rsa){continue}
            $rsa=[Security.Cryptography.RSACng]::new($key)
            $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo()))
            if($fingerprint -ne $record.PublicFingerprint){continue}
            if($key.IsMachineKey -or $key.Provider.Provider -ne $record.Provider){throw 'Imported key provider or user namespace changed.'}
            $identity=@{KeyName=$key.KeyName;UniqueName=$key.UniqueName;PublicFingerprint=$fingerprint;State='Created'}
            $old=@($record.OwnedKeys | Where-Object KeyName -eq $identity.KeyName)
            if($old.Count -gt 1 -or ($old.Count -eq 1 -and $old[0].UniqueName -ne $identity.UniqueName)){throw 'Imported key identity changed.'}
            if($old.Count){$old[0].State='Created'}else{$record.OwnedKeys+=@($identity)}
            if($record.OwnedKeys.Count -gt 8){throw 'Owned imported-key count exceeds its bound.'}
            Write-VaultFixturePackageRecord $path $record
            # Delete through this exact, fingerprint-verified native object.
            $key.Delete()
        } finally {if($rsa){$rsa.Dispose()};$key.Dispose()}
    }
    $remaining=@(Get-VaultFixtureKeyNames)
    foreach($owned in $record.OwnedKeys) {
        if($owned.KeyName -in $remaining){throw 'An owned imported key remains.'}
        $owned.State='Removed'
    }
    $record.State='Removed'
    Write-VaultFixturePackageRecord $path $record
    # Public closeout contains only fixture keys; unrelated baseline is never copied.
    Write-VaultFixturePackageRecord (Join-Path $root 'catalogue/package-signing/native-key-cleanup.json') @{
        FixtureId=$Journal.FixtureId;OwnerSid=$record.OwnerSid;Provider=$record.Provider;PublicFingerprint=$record.PublicFingerprint;
        OwnedKeys=$record.OwnedKeys;State='Removed';PrivateMaterialExported=$false}
}

Export-ModuleMember -Function Get-VaultFixtureKeyNames,New-VaultFixtureKeyImportIntent,Remove-VaultFixtureImportedKeys
