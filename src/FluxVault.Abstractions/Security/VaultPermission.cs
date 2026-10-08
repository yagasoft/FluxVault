namespace FluxVault.Abstractions.Security;

[Flags]
public enum VaultPermission
{
    None = 0, ReadHistory = 1, Recover = 2, ManageProtection = 4, Maintain = 8,
    DeleteHistory = 16, ManageAccess = 32,
    All = ReadHistory | Recover | ManageProtection | Maintain | DeleteHistory | ManageAccess
}

public sealed record VaultAccessGrant(string PrincipalSid, VaultPermission Permissions);
