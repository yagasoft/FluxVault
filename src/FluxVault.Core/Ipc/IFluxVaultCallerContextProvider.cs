using System.IO.Pipes;
using FluxVault.Core.Security;

namespace FluxVault.Core.Ipc;

public interface IFluxVaultCallerContextProvider
{
    FluxVaultCallerContext Capture(NamedPipeServerStream connectedPipe);
}
