using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Windows.Capture;
using FluxVault.Windows.Security;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.TestHost;

internal static class WindowsAutomaticSessionProbe
{
    internal static async Task<object> RunAsync(string creatorSid, string scratch)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18") throw new UnauthorizedAccessException("The native session probe requires its owned SYSTEM actor.");
        var root = ValidateRoot(scratch);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var diagnostics = new List<string>();
        await using var session = await new WindowsAutomaticProtectionSessionProvider(diagnostics.Add).AcquireAsync(creatorSid, deadline.Token)
            ?? throw new InvalidOperationException("Ordinary creator acquisition refused: " + string.Join(" ", diagnostics));
        await session.VerifyAsync(deadline.Token);
        var sourceRoot = Path.Combine(root, "source");
        try
        {
        Directory.CreateDirectory(sourceRoot);
        DirectoryAcl(sourceRoot, creatorSid, false);
        var readable = Path.Combine(sourceRoot, "creator.txt");
        var systemOnly = Path.Combine(sourceRoot, "system.txt");
        var adminOnly = Path.Combine(sourceRoot, "administrators.txt");
        var denied = Path.Combine(sourceRoot, "denied.txt");
        foreach (var path in new[] { readable, systemOnly, adminOnly, denied }) File.WriteAllText(path, "owned native authority bytes");
        FileAcl(readable, creatorSid, false);
        FileAcl(systemOnly, null, false);
        FileAcl(adminOnly, "S-1-5-32-544", false);
        FileAcl(denied, creatorSid, true);
        var capture = new WindowsCallerCaptureProvider(session.Caller, [sourceRoot]);
        await using var allowed = await capture.CaptureAsync(new(readable), deadline.Token);
        if (!allowed.Success || allowed.Content is null) throw new InvalidDataException("Creator-readable source refused: " + allowed.Message);
        foreach (var path in new[] { systemOnly, adminOnly, denied })
        {
            await using var refused = await capture.CaptureAsync(new(path), deadline.Token);
            if (refused.Success || refused.Content is not null) throw new InvalidDataException("Restricted source access unexpectedly succeeded: " + Path.GetFileName(path));
        }
        return new { Passed = 5, NativeWts = true, session.SessionId, session.AuthenticationId, session.Caller.UserSid,
            NonElevated = !session.Caller.IsElevated, CreatorReadable = true, SystemOnlyDenied = true,
            AdministratorOnlyDenied = true, ExplicitDenyPreserved = true, OrdinaryPostconditionsVerified = true };
        }
        finally { CleanupOwnedSources(root); }
    }

    internal static object CleanupOwnedSources(string scratch)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18") throw new UnauthorizedAccessException();
        var root = ValidateRoot(scratch);
        var sourceRoot = Path.Combine(root, "source");
        if (!Directory.Exists(sourceRoot)) return new { AclsRestored = true };
        StorageOwnership.RejectReparseComponents(sourceRoot);
        foreach (var path in Directory.GetFiles(sourceRoot))
        {
            if (Path.GetFileName(path) is not ("creator.txt" or "system.txt" or "administrators.txt" or "denied.txt"))
                throw new InvalidDataException("Unexpected fixture source; preserve cleanup target.");
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        }
        var directoryAcl = new DirectorySecurity(); directoryAcl.SetAccessRuleProtection(true, false); directoryAcl.SetOwner(new SecurityIdentifier("S-1-5-18"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) directoryAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        new DirectoryInfo(sourceRoot).SetAccessControl(directoryAcl);
        return new { AclsRestored = true };
    }
    private static string ValidateRoot(string scratch)
    {
        var root = Path.GetFullPath(scratch);
        if (Path.GetDirectoryName(root) != @"C:\ProgramData\FluxVault.Tests\NEXT005" || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
            throw new ArgumentException("A GUID-owned NEXT005 fixture root is required.");
        StorageOwnership.RejectReparseComponents(root);
        return root;
    }

    private static void DirectoryAcl(string path, string sid, bool deny)
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, deny ? AccessControlType.Deny : AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    private static void FileAcl(string path, string? sid, bool deny)
    {
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl, AccessControlType.Allow));
        if (sid is not null) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Read, deny ? AccessControlType.Deny : AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
