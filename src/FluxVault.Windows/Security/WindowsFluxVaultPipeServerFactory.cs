using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ComponentModel;
using FluxVault.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsFluxVaultPipeServerFactory : IFluxVaultPipeServerFactory
{
    private readonly string pipeName;
    private readonly SecurityIdentifier owner;
    private int ownershipState;

    private WindowsFluxVaultPipeServerFactory(string pipeName, SecurityIdentifier owner)
    {
        this.pipeName = pipeName;
        this.owner = owner;
    }

    public static WindowsFluxVaultPipeServerFactory ForService()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || !WindowsPipeSecurity.SystemSid.Equals(identity.User))
            throw new UnauthorizedAccessException("The installed pipe must be created by LocalSystem.");
        return new(NamedPipeFluxVaultServer.DefaultPipeName, WindowsPipeSecurity.SystemSid);
    }

    public static WindowsFluxVaultPipeServerFactory ForPrivateFixture(string pipeName)
    {
        WindowsPipeSecurity.ValidateFixtureName(pipeName);
        using var identity = WindowsIdentity.GetCurrent();
        return new(pipeName, identity.User ?? throw new UnauthorizedAccessException("A Windows user SID is required."));
    }

    public NamedPipeServerStream CreateFirstListener()
    {
        if (Interlocked.CompareExchange(ref ownershipState, 1, 0) != 0)
            throw new InvalidOperationException("Pipe ownership can be acquired only once per server lifetime.");
        try { return Create(first: true); }
        catch { Volatile.Write(ref ownershipState, -1); throw; }
    }

    public NamedPipeServerStream CreateAdditionalListener()
    {
        if (Volatile.Read(ref ownershipState) != 1)
            throw new InvalidOperationException("The served first listener must own the pipe name before adding instances.");
        return Create(first: false);
    }

    private NamedPipeServerStream Create(bool first)
    {
        var descriptor = WindowsPipeSecurity.Create(owner).GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = pinned.AddrOfPinnedObject(), InheritHandle = 0 };
            var handle = CreateNamedPipeW($@"\\.\pipe\{pipeName}",
                3 | 0x40000000u | (first ? 0x00080000u : 0),
                8, // Byte/wait mode, PIPE_REJECT_REMOTE_CLIENTS.
                32, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException("Could not acquire the local service pipe.", new Win32Exception(error));
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);
}
