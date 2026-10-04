using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

[SupportedOSPlatform("windows")]
internal static class WindowsPipeSecurity
{
    internal static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    // Generic write includes FILE_CREATE_PIPE_INSTANCE; ordinary clients receive individual rights.
    private const uint UnsafeRights = 0x000D0004 | 0x40000000 | 0x10000000;

    internal static void ValidateFixtureName(string name)
    {
        if (!(IsGuidName(name, "FluxVault.Tests.") || IsGuidName(name, "FluxVault.Integrity.")))
            throw new ArgumentException("A private FluxVault GUID-named fixture pipe is required.", nameof(name));
    }

    private static bool IsGuidName(string value, string prefix) => value is not null &&
        value.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(value[prefix.Length..], "N", out _);

    internal static PipeSecurity Create(SecurityIdentifier owner)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        foreach (var sid in new[] { owner, SystemSid, AdministratorsSid }.Distinct())
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        foreach (var type in new[] { WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.WinBuiltinAnyPackageSid })
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(type, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    internal static void VerifyConnectedServer(SafePipeHandle handle, SecurityIdentifier expectedOwner)
    {
        // The connected handle pins this object. No name re-open or server PID/token assumption.
        var error = GetSecurityInfo(handle, 6, 1 | 4, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new UnauthorizedAccessException("The service pipe identity could not be verified.", new Win32Exception((int)error));
        try
        {
            var size = GetSecurityDescriptorLength(descriptor);
            if (size is 0 or > 65536) throw new UnauthorizedAccessException("Invalid service pipe security descriptor.");
            var bytes = new byte[checked((int)size)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (security.Owner is null || !expectedOwner.Equals(security.Owner) || security.DiscretionaryAcl is null ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
                throw new UnauthorizedAccessException("The connected pipe is not owned by the expected service identity.");
            foreach (var entry in security.DiscretionaryAcl)
            {
                if (entry is not CommonAce ace || ace.IsCallback ||
                    ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                    throw new UnauthorizedAccessException("Unsupported service pipe access rule.");
                if (ace.AceQualifier == AceQualifier.AccessAllowed &&
                    (unchecked((uint)ace.AccessMask) & UnsafeRights) != 0 &&
                    !ace.SecurityIdentifier.Equals(SystemSid) && !ace.SecurityIdentifier.Equals(AdministratorsSid) &&
                    !ace.SecurityIdentifier.Equals(expectedOwner))
                    throw new UnauthorizedAccessException("The connected pipe allows an untrusted server instance or security change.");
            }
        }
        finally { _ = LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint securityInformation,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
