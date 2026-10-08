using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FluxVault.Core.Security;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsCallerSourceTests
{
    [Fact]
    public async Task Ordinary_case_variant_resolves_the_same_selected_root()
    {
        using var workspace = new Workspace(); using var caller = await Caller();
        var selected = Path.Combine(workspace.Root, "Selected"); Directory.CreateDirectory(selected);
        await File.WriteAllTextAsync(Path.Combine(selected, "document.txt"), "selected bytes");
        await using var opened = await new WindowsCallerFileAccess().OpenSourceAsync(caller, selected,
            Path.Combine(workspace.Root, "selected", "document.txt"));
        using var reader = new StreamReader(opened); Assert.Equal("selected bytes", await reader.ReadToEndAsync());
        Assert.Equal(FluxVault.Abstractions.Capture.ProtectionSourceAvailability.Missing,
            new WindowsProtectionSourceAccess(caller).Inspect(selected, Path.Combine(workspace.Root, "selected", "absent.txt"),
                FluxVault.Abstractions.Storage.RepositoryEntryKind.File).Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_distinct_requested_root_is_denied_before_source_bytes(bool blindAncestor)
    {
        using var workspace = new Workspace(); using var caller = await Caller();
        var parent = Path.Combine(workspace.Root, "case-parent"); Directory.CreateDirectory(parent);
        using (var writable = CreateFile(parent, 0x100100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert.False(writable.IsInvalid);
            uint enabled = 1;
            Assert.True(SetFileInformationByHandle(writable, 23, ref enabled, sizeof(uint)),
                "Native case-sensitive parent setup failed: " + Marshal.GetLastPInvokeError());
        }
        using (var parentHandle = WindowsCallerFileAccess.Open(null, "\\??\\" + parent, true))
        {
            using var first = CreateExactDirectory(parentHandle, "Root");
            using var second = CreateExactDirectory(parentHandle, "root");
            // Only the blind ancestor is case-sensitive; selected roots themselves remain supported.
            uint disabled = 0;
            Assert.True(SetFileInformationByHandle(first, 23, ref disabled, sizeof(uint)));
            Assert.True(SetFileInformationByHandle(second, 23, ref disabled, sizeof(uint)));
            await WriteChild(first, "selected bytes"); await WriteChild(second, "other bytes");
            using var selectedRead = WindowsCallerFileAccess.Open(first, "document.txt", false);
            using var otherRead = WindowsCallerFileAccess.Open(second, "document.txt", false);
            using var selectedStream = new FileStream(selectedRead, FileAccess.Read, 4096, true);
            using var otherStream = new FileStream(otherRead, FileAccess.Read, 4096, true);
            using var selectedReader = new StreamReader(selectedStream); using var otherReader = new StreamReader(otherStream);
            Assert.Equal("selected bytes", await selectedReader.ReadToEndAsync());
            Assert.Equal("other bytes", await otherReader.ReadToEndAsync());
        }
        var original = new DirectoryInfo(parent).GetAccessControl();
        if (blindAncestor)
        {
            var acl = new DirectoryInfo(parent).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(caller.UserSid), FileSystemRights.ReadAttributes, AccessControlType.Deny));
            new DirectoryInfo(parent).SetAccessControl(acl);
        }
        try
        {
            await Rejected<UnauthorizedAccessException>(() => new WindowsCallerFileAccess().OpenSourceAsync(caller,
                Path.Combine(parent, "Root"), Path.Combine(parent, "root", "document.txt")));
            Assert.Equal(FluxVault.Abstractions.Capture.ProtectionSourceAvailability.Unavailable,
                new WindowsProtectionSourceAccess(caller).Inspect(Path.Combine(parent, "Root"),
                    Path.Combine(parent, "root", "document.txt"), FluxVault.Abstractions.Storage.RepositoryEntryKind.File).Availability);
        }
        finally { new DirectoryInfo(parent).SetAccessControl(original); }
    }

    private static async Task WriteChild(SafeFileHandle parent, string content)
    {
        using var file = WindowsCallerFileAccess.OpenNative(parent, "document.txt", 0x12019F, false, disposition: 2, asynchronous: true);
        await using var stream = new FileStream(file, FileAccess.ReadWrite, 4096, true);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }

    private static SafeFileHandle CreateExactDirectory(SafeFileHandle parent, string name)
    {
        var text = Marshal.StringToHGlobalUni(name); var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = text }, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = pointer };
            var status = NtCreateFile(out var handle, 0x1001A0, ref attributes, out _, IntPtr.Zero, 0, 7, 2, 0x200021, IntPtr.Zero, 0);
            Assert.Equal(0, status); return handle;
        }
        finally { Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
    }
    [Fact]
    public async Task Traversal_only_ancestor_allows_root_bound_source_reads()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var parent = Path.Combine(workspace.Root, "blind-parent"); Directory.CreateDirectory(parent);
        var selected = Path.Combine(parent, "selected"); Directory.CreateDirectory(selected);
        var source = Path.Combine(selected, "document.txt"); await File.WriteAllTextAsync(source, "working bytes");
        var original = new DirectoryInfo(parent).GetAccessControl();
        var acl = new DirectoryInfo(parent).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(caller.UserSid), FileSystemRights.ReadAttributes, AccessControlType.Deny));
        new DirectoryInfo(parent).SetAccessControl(acl);
        try
        {
            await using var opened = await new WindowsCallerFileAccess().OpenSourceAsync(caller, selected, source);
            using var reader = new StreamReader(opened); Assert.Equal("working bytes", await reader.ReadToEndAsync());
        }
        finally { new DirectoryInfo(parent).SetAccessControl(original); }
    }
    [Fact]
    public async Task Parent_is_pinned_before_the_source_file_is_opened()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var folder = Path.Combine(workspace.Root, "working");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "document.txt");
        await File.WriteAllTextAsync(file, "working bytes");
        var access = new WindowsCallerFileAccess(_ => Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved")));
        await using (var source = await access.OpenSourceAsync(caller, folder, file))
            Assert.Equal(13, source.Length);
        Directory.Move(folder, folder + "-moved");
    }

    [Fact]
    public async Task In_place_junction_mutation_cannot_redirect_source_bytes()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var folder = Path.Combine(workspace.Root, "working");
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "document.txt"), "outside");
        var access = new WindowsCallerFileAccess(_ => SetJunction(folder, outside));
        try
        {
            await Rejected<UnauthorizedAccessException>(() => access.OpenSourceAsync(caller, folder, Path.Combine(folder, "document.txt")));
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "document.txt")));
        }
        finally { RemoveJunction(folder); }
    }
    [Fact]
    public async Task Source_handle_retains_parent_identity_until_disposed()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var folder = Path.Combine(workspace.Root, "working");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "document.txt");
        await File.WriteAllTextAsync(file, "working bytes");
        await using (var source = await new WindowsCallerFileAccess().OpenSourceAsync(caller, folder, file))
        {
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved"));
            using var reader = new StreamReader(source, leaveOpen: true);
            Assert.Equal("working bytes", await reader.ReadToEndAsync());
        }
        Directory.Move(folder, folder + "-moved");
    }

    [Fact]
    public async Task Outside_selection_is_rejected_before_reading_bytes()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var allowed = Path.Combine(workspace.Root, "working");
        Directory.CreateDirectory(allowed);
        var outside = Path.Combine(workspace.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside");
        await Rejected<UnauthorizedAccessException>(() => new WindowsCallerFileAccess().OpenSourceAsync(caller, allowed, outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leaf_or_parent_reparse_point_is_rejected(bool parent)
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var allowed = Path.Combine(workspace.Root, "working");
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "document.txt"), "outside");
        var link = Path.Combine(allowed, parent ? "link" : "document.txt");
        if (parent) Directory.CreateSymbolicLink(link, outside);
        else File.CreateSymbolicLink(link, Path.Combine(outside, "document.txt"));
        await Rejected<UnauthorizedAccessException>(() => new WindowsCallerFileAccess().OpenSourceAsync(caller, allowed,
            parent ? Path.Combine(link, "document.txt") : link));
    }

    [Fact]
    public async Task Hard_linked_source_is_rejected()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var original = Path.Combine(workspace.Root, "document.txt");
        var alias = Path.Combine(workspace.Root, "alias.txt");
        await File.WriteAllTextAsync(original, "working bytes");
        Assert.True(CreateHardLink(alias, original, IntPtr.Zero), "Native hard-link fixture creation failed.");
        await Rejected<UnauthorizedAccessException>(() => new WindowsCallerFileAccess().OpenSourceAsync(caller, workspace.Root, alias));
    }

    [Fact]
    public async Task Replaced_leaf_is_opened_without_following_its_new_reparse_target()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var folder = Path.Combine(workspace.Root, "working");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "document.txt");
        var outside = Path.Combine(workspace.Root, "outside.txt");
        await File.WriteAllTextAsync(file, "original");
        await File.WriteAllTextAsync(outside, "outside");
        var access = new WindowsCallerFileAccess(path =>
        {
            File.Move(path, path + "-original");
            File.CreateSymbolicLink(path, outside);
        });
        await Rejected<UnauthorizedAccessException>(() => access.OpenSourceAsync(caller, folder, file));
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task A_child_restricted_after_parent_admission_is_denied_by_native_open()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var file = Path.Combine(workspace.Root, "document.txt");
        await File.WriteAllTextAsync(file, "working bytes");
        var original = new FileInfo(file).GetAccessControl();
        var access = new WindowsCallerFileAccess(path =>
        {
            var acl = new FileInfo(path).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(caller.UserSid), FileSystemRights.ReadData, AccessControlType.Deny));
            new FileInfo(path).SetAccessControl(acl);
        });
        try { await Rejected<UnauthorizedAccessException>(() => access.OpenSourceAsync(caller, workspace.Root, file)); }
        finally { new FileInfo(file).SetAccessControl(original); }
    }

    [Fact]
    public async Task Cancelled_open_does_not_acquire_handles()
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var file = Path.Combine(workspace.Root, "document.txt");
        await File.WriteAllTextAsync(file, "working bytes");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Rejected<OperationCanceledException>(() => new WindowsCallerFileAccess().OpenSourceAsync(caller, workspace.Root, file, cancelled.Token));
        File.Move(file, file + "-moved");
    }

    [Theory]
    [InlineData("document.txt:stream")]
    [InlineData("sub\\..\\document.txt")]
    public async Task Ambiguous_path_forms_are_rejected(string relative)
    {
        using var workspace = new Workspace();
        using var caller = await Caller();
        var file = Path.Combine(workspace.Root, "document.txt");
        await File.WriteAllTextAsync(file, "working bytes");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "sub"));
        await Rejected<UnauthorizedAccessException>(() => new WindowsCallerFileAccess().OpenSourceAsync(caller, workspace.Root, Path.Combine(workspace.Root, relative)));
    }

    internal static async Task<FluxVaultCallerContext> Caller()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var name = "FluxVault.Tests." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name).CreateFirstListener();
        var wait = server.WaitForConnectionAsync(deadline.Token);
        using var client = await WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, identity.User!.Value).ConnectAsync(deadline.Token);
        await wait;
        await client.WriteAsync(new byte[] { 1 }, deadline.Token);
        var read = new byte[1];
        Assert.Equal(1, await server.ReadAsync(read, deadline.Token));
        return new WindowsFluxVaultCallerContextProvider().Capture(server);
    }

    private static Task<T> Rejected<T>(Func<Task<Stream>> action) where T : Exception => Assert.ThrowsAsync<T>(async () =>
    {
        await using var unexpected = await action();
    });

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FluxVault.Tests", Guid.NewGuid().ToString("N"));
        internal Workspace() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr attributes);

    private static void SetJunction(string directory, string target)
    {
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFile(directory, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid, "Native junction handle was not acquired.");
        Assert.True(DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            "The in-place junction mutation was not performed: " + Marshal.GetLastPInvokeError());
    }

    private static void RemoveJunction(string directory)
    {
        if (!Directory.Exists(directory) && !File.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) return;
        using var handle = CreateFile(directory, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid, "Native junction cleanup handle was not acquired.");
        var header = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0xA0000003);
        Assert.True(DeviceIoControl(handle, 0x000900AC, header, header.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            "The test junction was not removed: " + Marshal.GetLastPInvokeError());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref uint value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { internal int Length; internal IntPtr Root, Name; internal uint Attributes; internal IntPtr SecurityDescriptor, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status, Information; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status,
        IntPtr allocation, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr ea, uint eaLength);
}
