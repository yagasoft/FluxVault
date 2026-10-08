using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

/// <summary>Pins existing service-provisioned roots. This guard never creates or adopts storage.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVaultStorageGuard
{
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string InstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public IDisposable Open(VaultCatalogueEntry vault, CancellationToken cancellationToken = default)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != SystemSid || identity.ImpersonationLevel != TokenImpersonationLevel.None)
            throw new UnauthorizedAccessException("Vault storage requires the service process identity.");
        vault.Binding.Validate();
        var trusted = ReadTrustedPrincipals();
        var scopes = new List<IDisposable>();
        try
        {
            foreach (var root in new[] { vault.Binding.RepositoryPath, vault.Binding.StateRoot }
                .Concat(vault.Configuration.MirrorSet.EnabledNodes.Select(node => node.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
                scopes.Add(PinRoot(root, SystemSid, trusted, cancellationToken));
            return new StoragePins(scopes);
        }
        catch { foreach (var scope in Enumerable.Reverse(scopes)) scope.Dispose(); throw; }
    }

    internal static IDisposable PinRoot(string root, string expectedOwner, IReadOnlySet<string> trusted, CancellationToken cancellationToken = default)
    {
        var path = WindowsCallerFileAccess.ValidatePath(root);
        var mount = Path.GetPathRoot(path)!;
        var volume = WindowsCallerFileAccess.ResolveVolume(path);
        var components = path[mount.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0) throw new UnauthorizedAccessException("A private storage directory is required.");
        var handles = new List<SafeFileHandle>();
        try
        {
            var expected = volume.TrimEnd('\\');
            // The fixed volume name uses an object-manager symbolic link. Resolve that
            // service volume anchor, verify its final identity, then forbid reparses in
            // every filesystem component opened relative to the retained handle.
            var anchor = WindowsCallerFileAccess.OpenNative(null, "\\??\\" + volume[4..], 0x1200A0, directory: true);
            handles.Add(anchor);
            WindowsCallerFileAccess.VerifyHandle(anchor, expected, directory: true);
            CheckDescriptor(anchor, expectedOwner: null, trusted, storageRoot: false);
            foreach (var component in components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expected += "\\" + component;
                var handle = WindowsCallerFileAccess.OpenNative(handles[^1], component, 0x1200A0, directory: true, dontReparse: true);
                handles.Add(handle);
                WindowsCallerFileAccess.VerifyHandle(handle, expected, directory: true);
                var last = handles.Count == components.Length + 1;
                CheckDescriptor(handle, last ? expectedOwner : null, trusted, storageRoot: last);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new StoragePins(handles.Cast<IDisposable>().ToList());
        }
        catch { foreach (var handle in Enumerable.Reverse(handles)) handle.Dispose(); throw; }
    }

    internal static (FileStream Stream, IDisposable Pins) OpenProtectedFile(string path, string expectedOwner, IReadOnlySet<string> trusted)
    {
        path = WindowsCallerFileAccess.ValidatePath(path);
        var (pins, physicalParent) = WindowsCallerFileAccess.PinParent(path, readSecurity: true);
        SafeFileHandle? file = null;
        try
        {
            for (var index = 0; index < pins.Count; index++)
                CheckDescriptor(pins[index], index == pins.Count - 1 ? expectedOwner : null, trusted, index == pins.Count - 1);
            file = WindowsCallerFileAccess.OpenNative(pins[^1], Path.GetFileName(path), 0x120089, directory: false, sharing: 1, dontReparse: true);
            WindowsCallerFileAccess.VerifyHandle(file, physicalParent + "\\" + Path.GetFileName(path), directory: false);
            CheckDescriptor(file, expectedOwner, trusted, storageRoot: true);
            var stream = new FileStream(file, FileAccess.Read);
            file = null;
            return (stream, new StoragePins(pins.Cast<IDisposable>().ToList()));
        }
        catch { file?.Dispose(); foreach (var pin in Enumerable.Reverse(pins)) pin.Dispose(); throw; }
    }

    internal static void CheckDescriptor(SafeFileHandle handle, string? expectedOwner, IReadOnlySet<string> trusted, bool storageRoot)
    {
        var error = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new IOException("Protected storage security could not be read.", new Win32Exception((int)error));
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            ValidateDescriptor(new(bytes, 0), expectedOwner, trusted, storageRoot);
        }
        finally { LocalFree(descriptor); }
    }

    internal static void ValidateDescriptor(RawSecurityDescriptor descriptor, string? expectedOwner, IReadOnlySet<string> trusted, bool storageRoot)
    {
        if (descriptor.Owner is null || !trusted.Contains(descriptor.Owner.Value) ||
            expectedOwner is not null && descriptor.Owner.Value != expectedOwner || descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("Storage ownership or its access policy is untrusted.");
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            if (entry is not QualifiedAce ace || ace.IsCallback) throw new UnauthorizedAccessException("Unsupported storage access policy.");
            if (ace.AceQualifier != AceQualifier.AccessAllowed) continue;
            var inheritOnly = (ace.AceFlags & AceFlags.InheritOnly) != 0;
            var propagates = (ace.AceFlags & (AceFlags.ObjectInherit | AceFlags.ContainerInherit)) != 0;
            // Private roots govern newly created files/directories too. Ancestor-only
            // inheritance is checked at the selected root after private provisioning.
            if (inheritOnly && (!storageRoot || !propagates)) continue;
            if (storageRoot ? ace.SecurityIdentifier.Value is SystemSid or AdministratorsSid : trusted.Contains(ace.SecurityIdentifier.Value)) continue;
            var rights = unchecked((uint)ace.AccessMask);
            if ((rights & 0x10000000) != 0) rights |= 0x1F01FF; // GENERIC_ALL
            if ((rights & 0x40000000) != 0) rights |= 0x120116; // GENERIC_WRITE
            if ((rights & 0x80000000) != 0) rights |= 0x120089; // GENERIC_READ
            if ((rights & 0x20000000) != 0) rights |= 0x1200A0; // GENERIC_EXECUTE
            // Ancestors may allow sibling creation. Their pinned protected next child
            // prevents conversion into a reparse point; removal/control rights do not.
            var forbidden = storageRoot ? ~0x100020u : 0xD0040u;
            if ((rights & forbidden) != 0)
            {
                var failure = new UnauthorizedAccessException("Storage permits untrusted access or replacement.");
                failure.Data["PrincipalSid"] = ace.SecurityIdentifier.Value;
                failure.Data["Rights"] = rights.ToString("X8");
                failure.Data["StorageRoot"] = storageRoot;
                throw failure;
            }
        }
    }

    internal static HashSet<string> ReadTrustedPrincipals()
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal) { SystemSid, AdministratorsSid, InstallerSid };
        var account = (NTAccount)new SecurityIdentifier(AdministratorsSid).Translate(typeof(NTAccount));
        var group = account.Value[(account.Value.IndexOf('\\') + 1)..];
        IntPtr resume = IntPtr.Zero;
        uint status;
        do
        {
            status = NetLocalGroupGetMembers(null, group, 0, out var buffer, 65536, out var count, out _, ref resume);
            try
            {
                if (status is not (0 or 234)) throw new IOException("Local administrator membership could not be verified.", new Win32Exception((int)status));
                for (var index = 0; index < count; index++)
                    trusted.Add(new SecurityIdentifier(Marshal.ReadIntPtr(buffer, checked(index * IntPtr.Size))).Value);
            }
            finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        } while (status == 234);
        return trusted;
    }

    private sealed class StoragePins(List<IDisposable> handles) : IDisposable
    {
        public void Dispose() { foreach (var handle in Enumerable.Reverse(handles)) handle.Dispose(); handles.Clear(); }
    }
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int kind, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetLocalGroupGetMembers(string? server, string group,
        uint level, out IntPtr buffer, uint preferredLength, out int entriesRead, out int totalEntries, ref IntPtr resume);
    [DllImport("netapi32.dll")] private static extern uint NetApiBufferFree(IntPtr buffer);
}
