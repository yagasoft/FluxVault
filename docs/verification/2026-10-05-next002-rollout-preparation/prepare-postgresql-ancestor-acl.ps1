# Read-only preparation. This does not apply an ACL or launch a process.
#Requires -Version 7.2
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$proposals=@(foreach($path in @('D:\Program Files','D:\Program Files\PostgreSQL')) {
    $original=Get-Acl -LiteralPath $path
    $descriptor=[Security.AccessControl.RawSecurityDescriptor]::new($original.Sddl)
    $changes=[Collections.Generic.List[object]]::new()
    foreach($ace in $descriptor.DiscretionaryAcl) {
        if($ace -isnot [Security.AccessControl.CommonAce] -or $ace.IsCallback){throw 'Unreviewed ACL shape; no proposal generated.'}
        if($ace.AceQualifier -eq 'AccessAllowed' -and $ace.SecurityIdentifier.Value -eq 'S-1-5-11' -and
            -not($ace.AceFlags -band [Security.AccessControl.AceFlags]::InheritOnly) -and ($ace.AccessMask -band 0x10000)) {
            $before=$ace.AccessMask;$ace.AccessMask=$before -band (-bnot 0x10000)
            $changes.Add(@{Sid='S-1-5-11';BeforeMask=$before;AfterMask=$ace.AccessMask;Scope='This directory only'})
        }
        # Protection copies the current inherited grants as explicit grants.
        # Modify the actual effective ACE, never add a combining Allow duplicate.
        $ace.AceFlags=$ace.AceFlags -band (-bnot [Security.AccessControl.AceFlags]::Inherited)
    }
    if($changes.Count -ne 1){throw 'Exactly one reviewed effective DELETE grant is required.'}
    $descriptor.SetFlags($descriptor.ControlFlags -bor [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected)
    $proposed=$descriptor.GetSddlForm('All')
    if((Get-Acl -LiteralPath $path).Sddl -cne $original.Sddl){throw 'Original descriptor changed during read-only preparation.'}
    @{Path=$path;OriginalSddl=$original.Sddl;ProposedSddl=$proposed;OriginalProtected=$original.AreAccessRulesProtected;
        ProposedProtected=$true;Changes=@($changes);Applied=$false;ChildDescriptorsMustRemainUnchanged=$true;ObservedUtc=[DateTime]::UtcNow.ToString('o')}
})
$proposals | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'postgresql-ancestor-acl-proposal.json')
'Prepared exact two-directory ACL proposal and original descriptors; no ACL applied.'
