using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ComponentModel;
using System.Collections.Frozen;
using Microsoft.Win32.SafeHandles;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;

namespace FluxVault.Windows.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsFluxVaultCallerContextProvider : IFluxVaultCallerContextProvider
{
    public FluxVaultCallerContext Capture(NamedPipeServerStream connectedPipe)
    {
        ArgumentNullException.ThrowIfNull(connectedPipe);
        SafeAccessTokenHandle? token = null;
        try
        {
            connectedPipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
                if (identity?.User is null || identity.ImpersonationLevel < TokenImpersonationLevel.Impersonation)
                    throw new UnauthorizedAccessException("The pipe caller cannot be impersonated safely.");
                if (!DuplicateTokenEx(identity.AccessToken, 0xE, IntPtr.Zero, 2, 2, out token))
                    throw new UnauthorizedAccessException("The effective pipe caller token could not be retained.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
            });
            return new OwnedWindowsCallerContext(token ?? throw new UnauthorizedAccessException("No pipe caller token was captured."));
        }
        catch { token?.Dispose(); throw; }
    }

    private sealed class OwnedWindowsCallerContext : FluxVaultCallerContext
    {
        private readonly SafeAccessTokenHandle token;
        private int disposed;
        public override string UserSid { get; }
        public override IReadOnlySet<string> EnabledGroupSids { get; }
        public override bool IsElevated { get; }
        public override bool ImpersonationPermitted => true;

        internal OwnedWindowsCallerContext(SafeAccessTokenHandle token)
        {
            this.token = token;
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            UserSid = identity.User?.Value ?? throw new UnauthorizedAccessException("The caller token has no user SID.");
            var principal = new WindowsPrincipal(identity);
            // CheckTokenMembership excludes disabled/deny-only groups and honours restricting SIDs.
            EnabledGroupSids = (identity.Groups?.OfType<SecurityIdentifier>() ?? [])
                .Where(principal.IsInRole).Select(sid => sid.Value).ToFrozenSet(StringComparer.Ordinal);
            if (!GetTokenInformation(token, 20, out var elevation, sizeof(int), out _))
                throw new UnauthorizedAccessException("The caller elevation could not be verified.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            IsElevated = elevation != 0;
        }

        public override async Task<T> RunAsCallerAsync<T>(Func<Task<T>> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var held = false;
            try
            {
                token.DangerousAddRef(ref held);
                return await WindowsIdentity.RunImpersonatedAsync(token, action).ConfigureAwait(false);
            }
            finally { if (held) token.DangerousRelease(); }
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) token.Dispose();
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existingToken, uint desiredAccess,
        IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int information, int informationLength, out int returnLength);
}
