using System.IO.Pipes;

namespace FluxVault.Core.Ipc;

public interface IFluxVaultPipeServerFactory
{
    NamedPipeServerStream CreateFirstListener();
    NamedPipeServerStream CreateAdditionalListener();
}
