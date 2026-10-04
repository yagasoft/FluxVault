using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Abstractions.Storage;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class SelectionOutputInspectionTests
{
    [Fact]
    public void Validated_parent_walk_proves_component_absence_but_preserves_volume_open_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.OutputInspectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "absent", "file.txt");
            Assert.Throws<WindowsCallerFileAccess.ConfirmedSourceMissingException>(() => WindowsCallerFileAccess.PinParent(path));
            Assert.Throws<WindowsCallerFileAccess.NativeOpenException>(() => WindowsCallerFileAccess.PinParent(path,
                volumeIdentity: @"\\?\Volume{" + Guid.NewGuid() + @"}\"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Blind_ancestor_inspection_proves_present_and_missing_without_creation()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.OutputInspectionTests", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "destination"); Directory.CreateDirectory(parent);
        var file = Path.Combine(parent, "existing.txt"); await File.WriteAllTextAsync(file, "keep");
        var folder = Path.Combine(parent, "existing-folder"); Directory.CreateDirectory(folder);
        var parentAcl = new DirectoryInfo(parent).GetAccessControl(); parentAcl.SetAccessRuleProtection(true, true);
        new DirectoryInfo(parent).SetAccessControl(parentAcl);
        using var caller = await WindowsCallerSourceTests.Caller();
        var original = new DirectoryInfo(root).GetAccessControl();
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(caller.UserSid));
        acl.AddAccessRule(new(new SecurityIdentifier(caller.UserSid), FileSystemRights.Traverse | FileSystemRights.Synchronize |
            FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
        try
        {
            var access = new WindowsRestoreSelectionOutputAccess(caller);
            Assert.True(await access.ExistsAsync(file, RepositoryEntryKind.File, default));
            Assert.True(await access.ExistsAsync(folder, RepositoryEntryKind.File, default));
            Assert.False(await access.ExistsAsync(Path.Combine(parent, "absent.txt"), RepositoryEntryKind.File, default));
            Assert.Equal("keep", await File.ReadAllTextAsync(file));
            Assert.Equal(["existing-folder", "existing.txt"], Directory.EnumerateFileSystemEntries(parent).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray());
        }
        finally { Restore(original); new DirectoryInfo(root).SetAccessControl(original); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_leaf_or_required_parent_is_never_classified_as_missing(bool denyParent)
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.OutputInspectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var file = Path.Combine(root, "existing.txt"); await File.WriteAllTextAsync(file, "keep");
        using var caller = await WindowsCallerSourceTests.Caller();
        var entry = denyParent ? (FileSystemInfo)new DirectoryInfo(root) : new FileInfo(file);
        var original = denyParent ? (FileSystemSecurity)((DirectoryInfo)entry).GetAccessControl() : ((FileInfo)entry).GetAccessControl();
        var acl = denyParent ? (FileSystemSecurity)((DirectoryInfo)entry).GetAccessControl() : ((FileInfo)entry).GetAccessControl();
        var raw = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        raw.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x100080,
            new SecurityIdentifier(caller.UserSid), false, null));
        var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
        Set(acl);
        try
        {
            var access = new WindowsRestoreSelectionOutputAccess(caller);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.ExistsAsync(file, RepositoryEntryKind.File, default));
            if (denyParent) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.ExistsAsync(Path.Combine(root, "absent.txt"), RepositoryEntryKind.File, default));
        }
        finally { Restore(original); Set(original); Directory.Delete(root, true); }
        void Set(FileSystemSecurity value) { if (denyParent) ((DirectoryInfo)entry).SetAccessControl((DirectorySecurity)value); else ((FileInfo)entry).SetAccessControl((FileSecurity)value); }
    }
    private static void Restore(FileSystemSecurity original) =>
        original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
}
