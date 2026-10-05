# Internal checked preservation/restore steps. Import performs no moves or service
# changes. The rollout caller must join its workers and quiesce FluxVault first.
#Requires -Version 7.2
Set-StrictMode -Version Latest

function Assert-CommissionPreservationPaths {
    param([hashtable]$Context)
    $parent=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Context.Parent))
    $id=[guid]::Empty
    if(-not [guid]::TryParseExact($Context.InstallationId,'N',[ref]$id) -or $id -eq [guid]::Empty){throw 'Exact preservation identity is required.'}
    if($Context.NormalInstallation -and ($parent -ine 'C:\ProgramData' -or $Context.InstallationId -cne '7871ff7f8d1b404db20771f2e742364f')){throw 'Normal preservation target changed.'}
    if(-not $Context.NormalInstallation){Assert-VaultFixtureTrustedPath $parent}
    foreach($entry in @(@('ActiveRoot','FluxVault'),@('RollbackRoot',('FluxVault.Rollback.'+$Context.InstallationId)),@('FailedRoot',('FluxVault.Failed.'+$Context.InstallationId)))) {
        if([IO.Path]::GetFullPath($Context[$entry[0]]) -ine (Join-Path $parent $entry[1])){throw 'Preservation path escapes the exact approved parent/name.'}
        if(Test-Path -LiteralPath $Context[$entry[0]]) {
            $item=Get-Item -LiteralPath $Context[$entry[0]] -Force
            if(-not $item.PSIsContainer -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Preservation root is not an ordinary directory.'}
        }
    }
}

function Move-CommissionLegacyState {
    param([Parameter(Mandatory)][hashtable]$Context,[Parameter(Mandatory)][ValidateSet('Preserve','Restore')][string]$Phase)
    Assert-CommissionPreservationPaths $Context
    $source=if($Phase -eq 'Preserve'){$Context.ActiveRoot}else{$Context.RollbackRoot}
    $destination=if($Phase -eq 'Preserve'){$Context.RollbackRoot}else{$Context.ActiveRoot}
    if(-not(Test-Path -LiteralPath $source)){throw 'Retained legacy root is missing; no move is allowed.'}
    $config=Join-Path $source 'config.json'
    # ProgramData permits creating ordinary siblings. The retained configuration
    # anchors this exact legacy directory; its existing guard checks the entire
    # ancestor chain for replacement/control without adopting legacy contents.
    if($Context.NormalInstallation){Assert-VaultFixtureTrustedPath $config}
    if((Get-FileHash -LiteralPath $config).Hash -cne $Context.LegacyConfigSha256){throw 'Retained legacy configuration changed; preserve it and reconcile.'}
    $sddl=(Get-Acl -LiteralPath $source).Sddl
    if($Phase -eq 'Preserve') {
        if((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $Context.FailedRoot)){throw 'Preservation destination already exists; nothing is overwritten.'}
    } else {
        if(Test-Path -LiteralPath $Context.FailedRoot){throw 'Failed-state preservation path already exists; nothing is overwritten.'}
        if(Test-Path -LiteralPath $destination) {
            # A successful or partial fresh setup is retained independently. No
            # database, repository, metadata or child ACL is deleted/hardened.
            Assert-VaultFixtureTrustedPath $destination
            if($Context.NormalInstallation -and (Get-Acl -LiteralPath $destination).GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18'){
                throw 'Fresh state does not have its required SYSTEM ownership.'
            }
            [IO.Directory]::Move($destination,$Context.FailedRoot)
        }
    }
    # Same-volume atomic rename refuses a competing destination. It neither
    # recurses through children nor adopts their data or access controls.
    [IO.Directory]::Move($source,$destination)
    if(Test-Path -LiteralPath $source){throw 'Legacy source remained after its atomic move.'}
    if((Get-FileHash -LiteralPath (Join-Path $destination 'config.json')).Hash -cne $Context.LegacyConfigSha256 -or
        (Get-Acl -LiteralPath $destination).Sddl -cne $sddl){throw 'Legacy bytes or root security changed during preservation.'}
    return @{Phase=$Phase;SourceAbsent=$true;Destination=$destination;ConfigSha256=$Context.LegacyConfigSha256;Sddl=$sddl;
        FreshStateRetained=(Test-Path -LiteralPath $Context.FailedRoot)}
}

Export-ModuleMember -Function Move-CommissionLegacyState
