using FluxVault.Abstractions.Configuration;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsUserPreviewCacheTests
{
    [Fact]
    public void Product_default_cache_allocates_for_the_current_windows_user()
    {
        var cache = new WindowsUserPreviewCache();
        var path = cache.Allocate("document.docx", new());
        Assert.Equal(".docx", Path.GetExtension(path));
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.Equal(Path.Combine(Path.GetTempPath(), "FluxVault", "preview", identity.User!.Value), Path.GetDirectoryName(path));
        Assert.False(File.Exists(path));
    }
    [Fact]
    public void Allocations_are_distinct_keep_extensions_and_become_read_only()
    {
        using var fixture = new Files();
        var first = fixture.Cache.Allocate(@"C:\work\drawing.dwg", new());
        var second = fixture.Cache.Allocate(@"C:\work\drawing.dwg", new());
        Assert.NotEqual(first, second);
        Assert.Equal(".dwg", Path.GetExtension(first));
        Assert.Equal(fixture.Root, Path.GetDirectoryName(first));
        File.WriteAllText(first, "verified preview");
        fixture.Cache.MakeReadOnly(first);
        Assert.True(File.GetAttributes(first).HasFlag(FileAttributes.ReadOnly));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Cache.MakeReadOnly(Path.Combine(fixture.Root, "unrelated.txt")));
    }

    [Fact]
    public void Retention_removes_only_expired_preview_files_and_never_traverses_directories()
    {
        using var fixture = new Files();
        var stale = fixture.Cache.Allocate("old.docx", new()); File.WriteAllText(stale, "old");
        var recent = fixture.Cache.Allocate("recent.docx", new()); File.WriteAllText(recent, "recent");
        File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-4));
        fixture.Cache.MakeReadOnly(stale);
        var unrelated = Path.Combine(fixture.Root, "unrelated.txt"); File.WriteAllText(unrelated, "keep");
        var directory = Path.Combine(fixture.Root, "nested"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Path.GetFileName(stale)), "keep nested");
        fixture.Cache.Cleanup(new(RetentionDays: 5), DateTimeOffset.UtcNow);
        Assert.True(File.Exists(stale));
        fixture.Cache.Cleanup(new(RetentionDays: 2), DateTimeOffset.UtcNow);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent)); Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(Path.Combine(directory, Path.GetFileName(stale))));
    }

    [Fact]
    public void Cleanup_refuses_hard_linked_files_and_preserves_the_other_name()
    {
        using var fixture = new Files();
        var preview = fixture.Cache.Allocate("linked.txt", new()); File.WriteAllText(preview, "keep");
        var other = Path.Combine(fixture.Root, "other.txt");
        Assert.True(CreateHardLink(other, preview, IntPtr.Zero));
        File.SetCreationTimeUtc(preview, DateTime.UtcNow.AddDays(-4));
        fixture.Cache.Cleanup(new(), DateTimeOffset.UtcNow);
        Assert.True(File.Exists(preview)); Assert.Equal("keep", File.ReadAllText(other));
    }

    [Fact]
    public void Existing_shared_cache_is_refused_without_adopting_its_permissions_or_files()
    {
        using var fixture = new Files();
        Directory.CreateDirectory(fixture.Root);
        var existing = Path.Combine(fixture.Root, "existing.txt"); File.WriteAllText(existing, "keep");
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Cache.Allocate("drawing.dwg", new()));
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public void Reparse_cache_root_is_refused_and_its_target_is_preserved()
    {
        using var fixture = new Files();
        var target = fixture.Root + "-target"; Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt"); File.WriteAllText(sentinel, "keep");
        try
        {
            Directory.CreateSymbolicLink(fixture.Root, target);
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Cache.Allocate("drawing.dwg", new()));
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root);
            Directory.Delete(target, true);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr attributes);

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "FluxVault.Preview." + Guid.NewGuid().ToString("N"));
        public WindowsUserPreviewCache Cache => new(Root);
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach(var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
