using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using FluxVault.Core.Ipc;

namespace FluxVault.Windows.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsFluxVaultPipeClientFactory : IFluxVaultPipeClientFactory
{
    private readonly string pipeName;
    private readonly SecurityIdentifier expectedOwner;
    private WindowsFluxVaultPipeClientFactory(string pipeName, SecurityIdentifier expectedOwner)
    {
        this.pipeName = pipeName;
        this.expectedOwner = expectedOwner;
    }

    public static WindowsFluxVaultPipeClientFactory ForService() =>
        new(NamedPipeFluxVaultServer.DefaultPipeName, WindowsPipeSecurity.SystemSid);

    public static WindowsFluxVaultPipeClientFactory ForPrivateFixture(string pipeName, string expectedServerSid)
    {
        WindowsPipeSecurity.ValidateFixtureName(pipeName);
        return new(pipeName, new SecurityIdentifier(expectedServerSid));
    }

    public async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            WindowsPipeSecurity.VerifyConnectedServer(pipe.SafePipeHandle, expectedOwner);
            return pipe;
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    async Task<Stream> IFluxVaultPipeClientFactory.ConnectAsync(CancellationToken cancellationToken) =>
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
}
