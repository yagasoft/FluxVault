using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;
using FluxVault.Windows.Security;
using FluxVault.Windows.Capture;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsProtectionSourceTests
{
    [Fact]
    public async Task Missing_selected_root_is_missing_for_status_but_unavailable_for_child_reconciliation()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var root = Path.Combine(workspace.Root, "selected"); Directory.CreateDirectory(root);
        var child = Path.Combine(root, "document.txt"); await File.WriteAllTextAsync(child, "working bytes");
        Directory.Move(root, root + "-moved"); var access = new WindowsProtectionSourceAccess(caller);
        Assert.Equal(ProtectionSourceAvailability.Missing, access.Inspect(root, root, RepositoryEntryKind.Folder).Availability);
        Assert.Equal(ProtectionSourceAvailability.Unavailable, access.Inspect(root, child, RepositoryEntryKind.File).Availability);
    }
    [Fact]
    public async Task Live_capture_uses_the_caller_handle_and_releases_pins_with_the_result()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var folder = Path.Combine(workspace.Root, "selected"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "document.txt"); await File.WriteAllTextAsync(file, "working bytes");
        await using (var capture = await new WindowsCallerCaptureProvider(caller, [folder]).CaptureAsync(new(file)))
        {
            Assert.True(capture.Success); Assert.Equal(FluxVault.Abstractions.Storage.CaptureConsistency.BestEffort, capture.Consistency);
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved"));
            using var reader = new StreamReader(capture.Content!, leaveOpen: true); Assert.Equal("working bytes", await reader.ReadToEndAsync());
        }
        Directory.Move(folder, folder + "-moved");
    }

    [Fact]
    public async Task Denied_capture_has_no_privileged_fallback_and_outside_selection_is_failed()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var folder = Path.Combine(workspace.Root, "selected"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "document.txt"); await File.WriteAllTextAsync(file, "working bytes");
        var original = new FileInfo(file).GetAccessControl(); var denied = new FileInfo(file).GetAccessControl();
        denied.AddAccessRule(new(new SecurityIdentifier(caller.UserSid), FileSystemRights.ReadData, AccessControlType.Deny));
        new FileInfo(file).SetAccessControl(denied);
        try
        {
            await using var capture = await new WindowsCallerCaptureProvider(caller, [folder]).CaptureAsync(new(file));
            Assert.False(capture.Success); Assert.Null(capture.Content); Assert.NotEmpty(capture.Message);
        }
        finally { PrepareRestore(original); new FileInfo(file).SetAccessControl(original); }
        await using var outside = await new WindowsCallerCaptureProvider(caller, [folder]).CaptureAsync(new(Path.Combine(workspace.Root, "outside.txt")));
        Assert.False(outside.Success); Assert.Null(outside.Content);
    }

    [Fact]
    public async Task Cancelled_capture_throws_and_acquires_no_handles()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var file = Path.Combine(workspace.Root, "document.txt"); await File.WriteAllTextAsync(file, "working bytes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsCallerCaptureProvider(caller, [workspace.Root]).CaptureAsync(new(file), cancelled.Token));
        File.Move(file, file + "-moved");
    }
    [Fact]
    public async Task Metadata_is_read_from_a_caller_opened_handle_and_has_no_data_read_requirement()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var file = Path.Combine(workspace.Root, "drawing.txt"); await File.WriteAllTextAsync(file, "working bytes");
        var written = File.GetLastWriteTimeUtc(file); var original = new FileInfo(file).GetAccessControl();
        var denied = new FileInfo(file).GetAccessControl();
        denied.AddAccessRule(new(new SecurityIdentifier(caller.UserSid), FileSystemRights.ReadData, AccessControlType.Deny));
        new FileInfo(file).SetAccessControl(denied);
        try
        {
            var result = new WindowsProtectionSourceAccess(caller).Inspect(workspace.Root, file, RepositoryEntryKind.File);
            Assert.Equal(ProtectionSourceAvailability.Present, result.Availability); Assert.Equal(13, result.Length);
            Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), result.LastWriteUtc);
            Assert.Equal(TimeSpan.Zero, result.LastWriteUtc!.Value.Offset);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            { await using var unexpected = await new WindowsCallerFileAccess().OpenSourceAsync(caller, workspace.Root, file); });
        }
        finally { PrepareRestore(original); new FileInfo(file).SetAccessControl(original); }
    }

    [Fact]
    public async Task Denied_metadata_is_unavailable_and_not_missing()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var file = Path.Combine(workspace.Root, "document.txt"); await File.WriteAllTextAsync(file, "working bytes");
        var original = new FileInfo(file).GetAccessControl(); var denied = new FileInfo(file).GetAccessControl();
        DenyMask(denied, caller.UserSid, 0x100080);
        new FileInfo(file).SetAccessControl(denied);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.RunAsCallerAsync(() =>
            { using var unexpected = WindowsCallerFileAccess.OpenNative(null, "\\??\\" + file, 0x100080, false); return Task.FromResult(true); }));
            var result = new WindowsProtectionSourceAccess(caller).Inspect(workspace.Root, file, RepositoryEntryKind.File);
            Assert.Equal(ProtectionSourceAvailability.Unavailable, result.Availability); Assert.NotEmpty(result.FailureReason!);
        }
        finally { PrepareRestore(original); new FileInfo(file).SetAccessControl(original); }
    }

    [Fact]
    public async Task Confirmed_missing_leaf_and_component_are_distinct_from_denied_parent()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var access = new WindowsProtectionSourceAccess(caller);
        Assert.Equal(ProtectionSourceAvailability.Missing, access.Inspect(workspace.Root, Path.Combine(workspace.Root, "absent.txt"), RepositoryEntryKind.File).Availability);
        Assert.Equal(ProtectionSourceAvailability.Missing, access.Inspect(workspace.Root, Path.Combine(workspace.Root, "absent", "absent.txt"), RepositoryEntryKind.File).Availability);
        var folder = Path.Combine(workspace.Root, "denied"); Directory.CreateDirectory(folder);
        var original = new DirectoryInfo(folder).GetAccessControl(); var denied = new DirectoryInfo(folder).GetAccessControl();
        DenyMask(denied, caller.UserSid, 0x100080);
        new DirectoryInfo(folder).SetAccessControl(denied);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.RunAsCallerAsync(() =>
            { using var unexpected = WindowsCallerFileAccess.OpenNative(null, "\\??\\" + folder, 0x1000A0, true); return Task.FromResult(true); }));
            Assert.Equal(ProtectionSourceAvailability.Unavailable, access.Inspect(workspace.Root, Path.Combine(folder, "absent.txt"), RepositoryEntryKind.File).Availability);
        }
        finally { PrepareRestore(original); new DirectoryInfo(folder).SetAccessControl(original); }
    }

    [Fact]
    public async Task Reparse_and_outside_roots_are_unavailable_even_if_the_leaf_is_missing()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var selected = Path.Combine(workspace.Root, "selected"); var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(selected); Directory.CreateDirectory(outside);
        var link = Path.Combine(selected, "link"); Directory.CreateSymbolicLink(link, outside);
        var access = new WindowsProtectionSourceAccess(caller);
        Assert.Equal(ProtectionSourceAvailability.Unavailable, access.Inspect(selected, Path.Combine(link, "absent.txt"), RepositoryEntryKind.File).Availability);
        Assert.Equal(ProtectionSourceAvailability.Unavailable, access.Inspect(selected, Path.Combine(outside, "absent.txt"), RepositoryEntryKind.File).Availability);
    }

    [Fact]
    public async Task Denied_directory_listing_throws_instead_of_returning_an_empty_scan()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "document.txt"), "working bytes");
        var original = new DirectoryInfo(workspace.Root).GetAccessControl(); var denied = new DirectoryInfo(workspace.Root).GetAccessControl();
        denied.AddAccessRule(new(new SecurityIdentifier(caller.UserSid), FileSystemRights.ListDirectory, AccessControlType.Deny));
        new DirectoryInfo(workspace.Root).SetAccessControl(denied);
        try { Assert.Throws<UnauthorizedAccessException>(() => new WindowsProtectionSourceAccess(caller).EnumerateDirectory(workspace.Root, workspace.Root).ToArray()); }
        finally
        {
            PrepareRestore(original);
            new DirectoryInfo(workspace.Root).SetAccessControl(original);
        }
    }

    [Fact]
    public async Task Lazy_scan_pins_the_directory_until_disposal_and_releases_it_after_cancellation()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var folder = Path.Combine(workspace.Root, "selected"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "document.txt"), "working bytes");
        using var cancelled = new CancellationTokenSource();
        using (var scan = new WindowsProtectionSourceAccess(caller).EnumerateDirectory(folder, folder, cancelled.Token).GetEnumerator())
        {
            Assert.True(scan.MoveNext()); Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved"));
            cancelled.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => scan.MoveNext());
        }
        Directory.Move(folder, folder + "-moved");
    }

    [Fact]
    public async Task Multiple_native_batches_preserve_every_unicode_candidate_and_child_kind()
    {
        using var workspace = new Workspace(); using var caller = await WindowsCallerSourceTests.Caller();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 1200; index++)
        {
            var path = Path.Combine(workspace.Root, $"drawing-{index:D4}-مخطط-图纸.txt");
            await File.WriteAllTextAsync(path, "x"); expected.Add(path);
        }
        var child = Path.Combine(workspace.Root, "child"); Directory.CreateDirectory(child);
        var entries = new WindowsProtectionSourceAccess(caller).EnumerateDirectory(workspace.Root, workspace.Root).ToArray();
        Assert.Equal(1201, entries.Length);
        Assert.Equal(RepositoryEntryKind.Folder, entries.Single(item => item.Path == child).Kind);
        Assert.True(expected.SetEquals(entries.Where(item => item.Kind == RepositoryEntryKind.File).Select(item => item.Path)));
    }

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FluxVault.Tests", Guid.NewGuid().ToString("N"));
        internal Workspace() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static void DenyMask(FileSystemSecurity acl, string sid, int mask)
    {
        // FileSystemAccessRule removes SYNCHRONIZE from partial deny rules. Preserve the
        // exact native mask so this fixture actually refuses a synchronous metadata open.
        var raw = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        raw.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, mask,
            new SecurityIdentifier(sid), false, null));
        var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
    }
    private static void PrepareRestore(FileSystemSecurity original) =>
        original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
}
