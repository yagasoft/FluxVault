using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsVaultStorageTests
{
    private const string SystemSid = "S-1-5-18";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";
    private static readonly HashSet<string> Trusted = [SystemSid, "S-1-5-32-544"];

    [Theory]
    [InlineData(false, 0x10000)] // DELETE
    [InlineData(false, 0x40)] // DELETE_CHILD
    [InlineData(false, 0x40000)] // WRITE_DAC
    [InlineData(false, 0x80000)] // WRITE_OWNER
    [InlineData(false, 0x10000000)] // GENERIC_ALL
    [InlineData(true, 1)] // LIST_DIRECTORY
    [InlineData(true, 2)] // ADD_FILE
    [InlineData(true, 4)] // ADD_SUBDIRECTORY
    [InlineData(true, unchecked((int)0x80000000))] // GENERIC_READ
    [InlineData(true, 0x40000000)] // GENERIC_WRITE
    public void Untrusted_access_that_exposes_or_replaces_storage_is_denied(bool root, int rights)
    {
        var descriptor = Descriptor(SystemSid, rights);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.ValidateDescriptor(descriptor, root ? SystemSid : null, Trusted, root));
    }

    [Fact]
    public void Ancestors_allow_sibling_creation_but_private_roots_only_allow_traversal()
    {
        WindowsVaultStorageGuard.ValidateDescriptor(Descriptor(SystemSid, 0x100026), null, Trusted, false);
        WindowsVaultStorageGuard.ValidateDescriptor(Descriptor(SystemSid, 0x100020), SystemSid, Trusted, true);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.ValidateDescriptor(Descriptor(OtherSid, 0), null, Trusted, false));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.ValidateDescriptor(Descriptor("S-1-5-32-544", 0), SystemSid, Trusted, true));
    }

    [Fact]
    public void Null_acl_and_conditional_unknown_policies_are_denied()
    {
        var descriptor = new RawSecurityDescriptor(ControlFlags.SelfRelative, new(SystemSid), null, null, null);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.ValidateDescriptor(descriptor, SystemSid, Trusted, true));
        var acl = new RawAcl(2, 1); acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 1, new(OtherSid), true, [1, 0, 0, 0]));
        descriptor = new(ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent, new(SystemSid), null, null, acl);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.ValidateDescriptor(descriptor, SystemSid, Trusted, true));
    }

    [Theory]
    [InlineData(AceFlags.ObjectInherit | AceFlags.InheritOnly, 2)]
    [InlineData(AceFlags.ContainerInherit | AceFlags.InheritOnly, 4)]
    [InlineData(AceFlags.ObjectInherit | AceFlags.ContainerInherit | AceFlags.InheritOnly, 0x10000000)]
    public void Private_root_rejects_untrusted_rights_that_only_apply_to_future_children(AceFlags flags, int rights)
    {
        var descriptor = Descriptor(SystemSid, rights, flags);
        WindowsVaultStorageGuard.ValidateDescriptor(descriptor, null, Trusted, false);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.ValidateDescriptor(descriptor, SystemSid, Trusted, true));
    }

    [Theory]
    [InlineData(OtherSid)]
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")]
    public void Trusted_ancestor_principals_do_not_receive_private_root_access(string principal)
    {
        var trusted = new HashSet<string>(Trusted) { principal };
        var descriptor = Descriptor(SystemSid, 0x1F01FF, grantSid: principal);
        WindowsVaultStorageGuard.ValidateDescriptor(descriptor, null, trusted, false);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.ValidateDescriptor(descriptor, SystemSid, trusted, true));
    }

    [Fact]
    public void Physical_root_is_denied_when_a_service_created_child_would_inherit_untrusted_write_access()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "storage"); Directory.CreateDirectory(root); Protect(root);
        var acl = new DirectoryInfo(root).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(OtherSid), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
        var child = Path.Combine(root, "repository-record.bin"); File.WriteAllText(child, "generated protected record");
        Assert.Contains(new FileInfo(child).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Value == OtherSid && rule.IsInherited && rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.WriteData) != 0);
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = WindowsVaultStorageGuard.ReadTrustedPrincipals(); trusted.Add(identity.User!.Value);
        Assert.Throws<UnauthorizedAccessException>(() => { using var pins = WindowsVaultStorageGuard.PinRoot(root, identity.User.Value, trusted); });
    }

    [Fact]
    public void Physical_directory_chain_stays_pinned_until_scope_disposal()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "storage"); Directory.CreateDirectory(root); Protect(root);
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = WindowsVaultStorageGuard.ReadTrustedPrincipals(); trusted.Add(identity.User!.Value);
        var renamed = root + "-renamed";
        try
        {
            using (WindowsVaultStorageGuard.PinRoot(root, identity.User.Value, trusted))
                Assert.Throws<IOException>(() => Directory.Move(root, renamed));
        }
        catch (UnauthorizedAccessException exception)
        { throw new Xunit.Sdk.XunitException($"{exception.Message} SID={exception.Data["PrincipalSid"]} Rights={exception.Data["Rights"]} Root={exception.Data["StorageRoot"]}"); }
        Directory.Move(root, renamed); Directory.Move(renamed, root);
    }

    [Fact]
    public void Protected_file_read_pins_file_and_parent_until_disposal()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "bootstrap"); Directory.CreateDirectory(root); Protect(root);
        var path = Path.Combine(root, "installation.json"); File.WriteAllText(path, "generated bootstrap bytes");
        using var identity = WindowsIdentity.GetCurrent();
        var fileAcl = new FileInfo(path).GetAccessControl(); fileAcl.SetOwner(identity.User!); new FileInfo(path).SetAccessControl(fileAcl);
        var trusted = WindowsVaultStorageGuard.ReadTrustedPrincipals(); trusted.Add(identity.User!.Value);
        var opened = WindowsVaultStorageGuard.OpenProtectedFile(path, identity.User.Value, trusted);
        using (opened.Pins)
        using (opened.Stream)
        {
            using var reader = new StreamReader(opened.Stream, leaveOpen: true);
            Assert.Equal("generated bootstrap bytes", reader.ReadToEnd());
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            Assert.Throws<IOException>(() => File.WriteAllText(path, "replacement"));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
        }
        File.Move(path, path + ".moved");
    }

    [Fact]
    public void Protected_file_rejects_untrusted_file_acl_and_nonfollowing_links()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "bootstrap"); Directory.CreateDirectory(root); Protect(root);
        var path = Path.Combine(root, "installation.json"); File.WriteAllText(path, "generated bootstrap bytes");
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = WindowsVaultStorageGuard.ReadTrustedPrincipals(); trusted.Add(identity.User!.Value);
        var acl = new FileInfo(path).GetAccessControl();
        acl.SetOwner(identity.User!);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(OtherSid), FileSystemRights.ReadData, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
        var denied = Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.OpenProtectedFile(path, identity.User.Value, trusted));
        Assert.Contains("Storage permits untrusted access", denied.Message);
        var link = Path.Combine(root, "linked.json"); File.CreateSymbolicLink(link, path);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.OpenProtectedFile(link, identity.User.Value, trusted));
    }

    [Fact]
    public void Physical_untrusted_read_grant_and_reparse_component_are_denied()
    {
        using var workspace = new Workspace();
        var root = Path.Combine(workspace.Root, "storage"); Directory.CreateDirectory(root); Protect(root);
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = WindowsVaultStorageGuard.ReadTrustedPrincipals(); trusted.Add(identity.User!.Value);
        var acl = new DirectoryInfo(root).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(OtherSid), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
        var denied = Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.PinRoot(root, identity.User.Value, trusted));
        Assert.Contains("Storage permits untrusted access", denied.Message);
        var link = Path.Combine(workspace.Root, "link"); Directory.CreateSymbolicLink(link, root);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVaultStorageGuard.PinRoot(link, identity.User.Value, trusted));
        Directory.Delete(link);
    }

    private static RawSecurityDescriptor Descriptor(string owner, int rights, AceFlags flags = AceFlags.None, string grantSid = OtherSid)
    {
        var acl = new RawAcl(2, 2); acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1F01FF, new(SystemSid), false, null));
        acl.InsertAce(1, new CommonAce(flags, AceQualifier.AccessAllowed, rights, new(grantSid), false, null));
        return new(ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent, new(owner), null, null, acl);
    }
    private static void Protect(string root)
    {
        using var identity = WindowsIdentity.GetCurrent(); var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(identity.User!);
        foreach (var sid in new[] { SystemSid, "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
    }
    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FluxVault.Tests", Guid.NewGuid().ToString("N"));
        internal Workspace() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
