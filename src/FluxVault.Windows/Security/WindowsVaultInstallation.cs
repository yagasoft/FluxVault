using System.Runtime.Versioning;
using System.Security.Principal;
using FluxVault.Core.Security;

namespace FluxVault.Windows.Security;

/// <summary>Owns pinned bootstrap handles for the service lifetime. No legacy fallback.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVaultInstallation : IDisposable
{
    private readonly FileStream stream;
    private readonly IDisposable pins;
    public FluxVaultInstallation Configuration { get; }
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FluxVault", "installation.json");

    private WindowsVaultInstallation(FileStream stream, IDisposable pins, FluxVaultInstallation configuration)
    { this.stream = stream; this.pins = pins; Configuration = configuration; }

    public static WindowsVaultInstallation Open(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18" || identity.ImpersonationLevel != TokenImpersonationLevel.None)
            throw new UnauthorizedAccessException("Installation bootstrap requires the service process identity.");
        var (stream, pins) = WindowsVaultStorageGuard.OpenProtectedFile(path, "S-1-5-18", WindowsVaultStorageGuard.ReadTrustedPrincipals());
        try
        {
            if (stream.Length is 0 or > FluxVaultInstallation.MaximumBytes) throw new InvalidDataException("Installation bootstrap size is invalid.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Installation bootstrap changed while reading.");
            var configuration = FluxVaultInstallation.Parse(bytes);
            _ = new SecurityIdentifier(configuration.CreatorSid);
            return new(stream, pins, configuration);
        }
        catch { stream.Dispose(); pins.Dispose(); throw; }
    }

    public void Dispose() { stream.Dispose(); pins.Dispose(); }
}
