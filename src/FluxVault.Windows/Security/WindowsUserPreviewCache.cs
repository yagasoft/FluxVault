using FluxVault.Abstractions.Configuration;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

/// <summary>User-context preview copies. Never used by the privileged executor.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserPreviewCache(string root)
{
    private readonly string rootPath = Path.GetFullPath(root);
    private const string Prefix = "FluxVault-preview-";
    private const int MaximumCleanupEntries = 1024;
    public WindowsUserPreviewCache() : this(DefaultRoot()) { }
    private static string DefaultRoot()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("A Windows preview owner is required.");
        return Path.Combine(Path.GetTempPath(), "FluxVault", "preview", sid);
    }
    public string Allocate(string sourcePath, VersionPreviewPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureRoot();
        Cleanup(policy, DateTimeOffset.UtcNow);
        var extension = Path.GetExtension(sourcePath);
        if (extension.Length > 32 || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The preview file extension is unsupported.", nameof(sourcePath));
        return Path.Combine(rootPath, Prefix + Guid.NewGuid().ToString("N") + extension);
    }
    public void MakeReadOnly(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), rootPath, StringComparison.OrdinalIgnoreCase) || !IsOwnedName(Path.GetFileName(path)))
            throw new UnauthorizedAccessException("Only this user's preview entry can be prepared.");
        using var pins = PinRoot();
        using var file = WindowsCallerFileAccess.OpenNative(pins.Handles[^1], Path.GetFileName(path), 0x100180, false);
        WindowsCallerFileAccess.VerifyHandle(file, pins.Physical + "\\" + Path.GetFileName(path), false);
        var basic = ReadBasic(file);
        SetAttributes(file, (basic.Attributes & ~(uint)FileAttributes.Normal) | (uint)FileAttributes.ReadOnly);
    }

    public void Cleanup(VersionPreviewPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Directory.Exists(rootPath)) return;
        using var pins = PinRoot();
        var cutoff = now.AddDays(-policy.Normalise().RetentionDays).UtcDateTime.ToFileTimeUtc();
        // Flat, bounded enumeration. Every eligible leaf is reopened relative to the pinned
        // directory and verified without following links before attributes or deletion change.
        foreach (var path in Directory.EnumerateFileSystemEntries(rootPath).Take(MaximumCleanupEntries))
        {
            var name = Path.GetFileName(path);
            if (!IsOwnedName(name)) continue;
            try
            {
                using var file = WindowsCallerFileAccess.OpenNative(pins.Handles[^1], name, 0x110180, false);
                WindowsCallerFileAccess.VerifyHandle(file, pins.Physical + "\\" + name, false);
                var basic = ReadBasic(file);
                if (basic.CreationTime >= cutoff) continue;
                var attributes = basic.Attributes & ~(uint)FileAttributes.ReadOnly;
                SetAttributes(file, attributes == 0 ? (uint)FileAttributes.Normal : attributes);
                var disposition = new Disposition { Delete = true };
                if (!SetFileInformationByHandle(file, 4, ref disposition, Marshal.SizeOf<Disposition>())) NativeError();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { /* Open viewers, links and refused entries remain for a later bounded pass. */ }
        }
    }

    private void EnsureRoot()
    {
        if (Directory.Exists(rootPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(rootPath)!);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new UnauthorizedAccessException("A Windows preview owner is required.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(sid);
        foreach (var trusted in new[] { sid, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") }.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(trusted, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(rootPath).Create(acl);
    }

    private Pins PinRoot()
    {
        var pinned = WindowsCallerFileAccess.PinParent(Path.Combine(rootPath, "entry"));
        var pins = new Pins(pinned.Pins, pinned.PhysicalParent);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException("A Windows preview owner is required.");
            var acl = new DirectoryInfo(rootPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var trusted = new HashSet<string>(StringComparer.Ordinal) { owner.Value, "S-1-5-18", "S-1-5-32-544" };
            if (!acl.AreAccessRulesProtected || acl.GetOwner(typeof(SecurityIdentifier))?.Value != owner.Value ||
                acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .Any(rule => rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value)))
                throw new UnauthorizedAccessException("The preview directory is not private to this Windows user. It has been preserved.");
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    private static bool IsOwnedName(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) &&
        name.Length >= Prefix.Length + 32 && Guid.TryParseExact(name.AsSpan(Prefix.Length, 32), "N", out var id) && id != Guid.Empty &&
        (name.Length == Prefix.Length + 32 || name[Prefix.Length + 32] == '.');
    private static BasicInfo ReadBasic(SafeFileHandle file)
    {
        if (!GetFileInformationByHandleEx(file, 0, out BasicInfo basic, Marshal.SizeOf<BasicInfo>())) NativeError();
        return basic;
    }
    private static void SetAttributes(SafeFileHandle file, uint attributes)
    {
        var basic = new BasicInfo { Attributes = attributes }; // Zero timestamps leave their values unchanged.
        if (!SetFileInformationByHandle(file, 0, ref basic, Marshal.SizeOf<BasicInfo>())) NativeError();
    }
    private static void NativeError() => throw new IOException("The preview entry could not be prepared or cleaned up.", new Win32Exception(Marshal.GetLastPInvokeError()));
    private sealed class Pins(List<SafeFileHandle> handles, string physical) : IDisposable
    {
        public List<SafeFileHandle> Handles => handles;
        public string Physical => physical;
        public void Dispose() { for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out BasicInfo info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref BasicInfo info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref Disposition info, int size);
}
