using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Tests;

public sealed class VaultAuthorizationTests
{
    [Fact]
    public void Selection_preview_is_read_only_but_still_requires_recovery_permission()
    {
        var request = FluxVaultIpcRequest.PreviewRestoreSelection(@"C:\work\file.txt", false,
            RestoreSelectionDestinationMode.Elsewhere, @"C:\recovery\file.txt");
        Assert.False(PostgreSqlVaultCatalogue.IsMutation(request.Command));
        Assert.True(VaultCommandPolicy.TryGet(request, out var policy));
        Assert.Equal(VaultPermission.Recover, policy.Permissions);
        Assert.True(PostgreSqlVaultCatalogue.IsMutation(FluxVaultIpcCommand.RunRestoreSelection));
    }

    private const string Owner = "S-1-5-21-1-2-3-1001";
    private const string Other = "S-1-5-21-1-2-3-1002";
    private const string Group = "S-1-5-21-1-2-3-2001";
    private const string Administrators = "S-1-5-32-544";

    [Theory]
    [InlineData(VaultPermission.ReadHistory)]
    [InlineData(VaultPermission.Recover)]
    [InlineData(VaultPermission.ManageProtection)]
    [InlineData(VaultPermission.Maintain)]
    [InlineData(VaultPermission.DeleteHistory)]
    public void Creator_owns_the_vault_but_other_users_have_no_default_permission(VaultPermission permission)
    {
        using var creator = new Caller(Owner);
        using var other = new Caller(Other);
        var policy = VaultAccessPolicy.ForCreator(creator);
        Assert.Equal(Owner, policy.OwnerSid);
        Assert.True(VaultAuthorizer.IsAllowed(creator, policy, permission));
        Assert.False(VaultAuthorizer.IsAllowed(other, policy, permission));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Grants_are_limited_to_their_named_permissions_and_enabled_membership(bool groupGrant)
    {
        var policy = new VaultAccessPolicy(Owner, [new(groupGrant ? Group : Other, VaultPermission.ReadHistory)]);
        using var permitted = new Caller(Other, groupGrant ? new HashSet<string> { Group } : []);
        using var disabledGroup = new Caller(Other);
        Assert.True(VaultAuthorizer.IsAllowed(permitted, policy, VaultPermission.ReadHistory));
        Assert.False(VaultAuthorizer.IsAllowed(permitted, policy, VaultPermission.Recover));
        if (groupGrant) Assert.False(VaultAuthorizer.IsAllowed(disabledGroup, policy, VaultPermission.ReadHistory));
    }

    [Fact]
    public void Destructive_conjunction_requires_every_permission()
    {
        var policy = new VaultAccessPolicy(Owner, [new(Other, VaultPermission.Maintain)]);
        using var caller = new Caller(Other);
        Assert.True(VaultAuthorizer.IsAllowed(caller, policy, VaultPermission.Maintain));
        Assert.False(VaultAuthorizer.IsAllowed(caller, policy, VaultPermission.Maintain | VaultPermission.DeleteHistory));
        var granted = new VaultAccessPolicy(Owner, [new(Other, VaultPermission.Maintain), new(Other, VaultPermission.DeleteHistory)]);
        Assert.True(VaultAuthorizer.IsAllowed(caller, granted, VaultPermission.Maintain | VaultPermission.DeleteHistory));
    }

    [Fact]
    public void Access_management_requires_an_elevated_enabled_administrator_even_for_the_owner()
    {
        var policy = new VaultAccessPolicy(Owner, [new(Owner, VaultPermission.ManageAccess)]);
        using var owner = new Caller(Owner);
        using var unelevated = new Caller(Owner, new HashSet<string> { Administrators });
        using var deniedOnlyAdmin = new Caller(Owner, elevated: true);
        using var administrator = new Caller(Other, new HashSet<string> { Administrators }, elevated: true);
        Assert.False(VaultAuthorizer.IsAllowed(owner, policy, VaultPermission.ManageAccess));
        Assert.False(VaultAuthorizer.IsAllowed(unelevated, policy, VaultPermission.ManageAccess));
        Assert.False(VaultAuthorizer.IsAllowed(deniedOnlyAdmin, policy, VaultPermission.ManageAccess));
        Assert.True(VaultAuthorizer.IsAllowed(administrator, policy, VaultPermission.ManageAccess));
    }

    [Fact]
    public void Policy_owns_its_grant_snapshot()
    {
        var input = new List<VaultAccessGrant>();
        var policy = new VaultAccessPolicy(Owner, input);
        input.Add(new(Other, VaultPermission.ReadHistory));
        using var caller = new Caller(Other);
        Assert.False(VaultAuthorizer.IsAllowed(caller, policy, VaultPermission.ReadHistory));
        Assert.Empty(policy.Grants);
    }

    [Fact]
    public void Missing_impersonation_and_unknown_permissions_fail_closed_even_for_an_administrator()
    {
        var policy = new VaultAccessPolicy(Owner, []);
        using var caller = new Caller(Other, new HashSet<string> { Administrators }, elevated: true);
        using var noToken = new Caller(Owner, impersonation: false);
        Assert.False(VaultAuthorizer.IsAllowed(caller, policy, (VaultPermission)1024));
        Assert.False(VaultAuthorizer.IsAllowed(caller, policy, VaultPermission.None));
        Assert.False(VaultAuthorizer.IsAllowed(noToken, policy, VaultPermission.ReadHistory));
        Assert.Throws<UnauthorizedAccessException>(() => VaultAccessPolicy.ForCreator(noToken));
    }

    [Fact]
    public void All_existing_commands_have_explicit_nonempty_policy_and_unknown_values_fail_closed()
    {
        foreach (var command in Enum.GetValues<FluxVaultIpcCommand>())
        {
            Assert.True(VaultCommandPolicy.TryGet(command, out var policy), $"Missing policy for {command}.");
            Assert.NotEqual(VaultPermission.None, policy.Permissions);
        }
        Assert.False(VaultCommandPolicy.TryGet((FluxVaultIpcCommand)int.MaxValue, out _));
    }

    [Theory]
    [InlineData(FluxVaultIpcCommand.RestoreVersion, VaultPermission.Recover)]
    [InlineData(FluxVaultIpcCommand.RestoreVersionPreview, VaultPermission.Recover)]
    [InlineData(FluxVaultIpcCommand.RunBackupNow, VaultPermission.ManageProtection)]
    [InlineData(FluxVaultIpcCommand.SaveConfiguration, VaultPermission.ManageProtection)]
    [InlineData(FluxVaultIpcCommand.RunRetentionNow, VaultPermission.Maintain | VaultPermission.DeleteHistory)]
    [InlineData(FluxVaultIpcCommand.RunMirrorDrain, VaultPermission.Maintain | VaultPermission.DeleteHistory)]
    public void Command_contract_requires_its_specific_permission(FluxVaultIpcCommand command, VaultPermission required)
    {
        Assert.True(VaultCommandPolicy.TryGet(command, out var policy));
        Assert.Equal(required, policy.Permissions);
    }

    [Fact]
    public void Saving_with_history_purge_requires_destructive_rights_in_addition_to_protection_management()
    {
        var request = FluxVaultIpcRequest.SaveConfiguration(FluxVaultConfiguration.CreateDefault(@"C:\Fixture"), purgeRemovedSelections: true);
        Assert.True(VaultCommandPolicy.TryGet(request, out var purge));
        Assert.Equal(VaultPermission.ManageProtection | VaultPermission.Maintain | VaultPermission.DeleteHistory, purge.Permissions);
        Assert.True(VaultCommandPolicy.TryGet(request with { PurgeRemovedSelections = false }, out var plain));
        Assert.Equal(VaultPermission.ManageProtection, plain.Permissions);
    }

    private sealed class Caller(string sid, IReadOnlySet<string>? groups = null, bool elevated = false, bool impersonation = true) : FluxVaultCallerContext
    {
        public override string UserSid => sid;
        public override IReadOnlySet<string> EnabledGroupSids => groups ?? new HashSet<string>();
        public override bool IsElevated => elevated;
        public override bool ImpersonationPermitted => impersonation;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new NotSupportedException("Policy-only fixture cannot impersonate.");
        public override void Dispose() { }
    }
}
