using System.Runtime.Versioning;
using System.Security.Principal;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsSingleVaultProvisioningTests
{
    [Fact]
    public void Ordinary_process_cannot_open_provisioning_intent_or_own_the_fixed_setup_pipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.NotEqual("S-1-5-18", identity.User!.Value);
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.UntrustedSetup", Guid.NewGuid().ToString("N"));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsSingleVaultProvisioner.Open(Path.Combine(root, "ticket.json"), Path.Combine(root, "installation.json")));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsFluxVaultPipeServerFactory.ForSetup());
        Assert.False(Directory.Exists(root));
    }
}
