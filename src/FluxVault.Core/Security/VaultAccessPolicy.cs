using FluxVault.Abstractions.Security;

namespace FluxVault.Core.Security;

public sealed class VaultAccessPolicy
{
    public string OwnerSid { get; }
    public IReadOnlyList<VaultAccessGrant> Grants { get; }

    public VaultAccessPolicy(string ownerSid, IReadOnlyList<VaultAccessGrant> grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Any(grant => string.IsNullOrWhiteSpace(grant.PrincipalSid) ||
            grant.Permissions == VaultPermission.None || (grant.Permissions & ~VaultPermission.All) != 0))
            throw new ArgumentException("A grant requires a principal SID and known permissions.", nameof(grants));
        OwnerSid = ownerSid;
        Grants = Array.AsReadOnly(grants.ToArray());
    }

    public static VaultAccessPolicy ForCreator(FluxVaultCallerContext creator)
    {
        ArgumentNullException.ThrowIfNull(creator);
        if (!creator.ImpersonationPermitted || string.IsNullOrWhiteSpace(creator.UserSid))
            throw new UnauthorizedAccessException("An authenticated Windows creator is required.");
        return new(creator.UserSid, []);
    }
}

public static class VaultAuthorizer
{
    public static bool IsAllowed(FluxVaultCallerContext caller, VaultAccessPolicy policy, VaultPermission permissions)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(policy);
        if (!caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid) ||
            permissions == VaultPermission.None || (permissions & ~VaultPermission.All) != 0) return false;
        var administrator = caller.UserSid == "S-1-5-18" ||
            (caller.IsElevated && caller.EnabledGroupSids.Contains("S-1-5-32-544"));
        if (administrator) return true;
        if ((permissions & VaultPermission.ManageAccess) != 0) return false;
        var granted = caller.UserSid == policy.OwnerSid ? VaultPermission.All & ~VaultPermission.ManageAccess : VaultPermission.None;
        foreach (var grant in policy.Grants)
            if (caller.UserSid == grant.PrincipalSid || caller.EnabledGroupSids.Contains(grant.PrincipalSid))
                granted |= grant.Permissions;
        return (granted & permissions) == permissions;
    }
}
