using System.Security.Cryptography;
using System.Text;

namespace FluxVault.Abstractions.Configuration;

public enum DeviceTrustState
{
    Local = 0,
    Trusted = 1,
    Blocked = 2
}

public sealed record SyncConfiguration(
    DeviceIdentityConfiguration LocalDevice = null!,
    IReadOnlyList<TrustedDeviceConfiguration> TrustedDevices = null!)
{
    public static SyncConfiguration CreateDefault(string programDataPath)
    {
        var localDevice = DeviceIdentityConfiguration.CreateDefault(programDataPath);
        return new SyncConfiguration(localDevice, [TrustedDeviceConfiguration.FromLocalDevice(localDevice)]);
    }

    public SyncConfiguration Normalise(string programDataPath)
    {
        var localDevice = (LocalDevice ?? DeviceIdentityConfiguration.CreateDefault(programDataPath))
            .Normalise(programDataPath);
        var trustedDevices = (TrustedDevices ?? [])
            .Select(device => device.Normalise())
            .Where(device => !string.IsNullOrWhiteSpace(device.DeviceId))
            .GroupBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var localRecord = TrustedDeviceConfiguration.FromLocalDevice(localDevice);
        var localIndex = trustedDevices.FindIndex(device =>
            string.Equals(device.DeviceId, localDevice.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (localIndex >= 0)
        {
            trustedDevices[localIndex] = localRecord;
        }
        else
        {
            trustedDevices.Insert(0, localRecord);
        }

        return new SyncConfiguration(localDevice, trustedDevices);
    }
}

public sealed record DeviceIdentityConfiguration(
    string DeviceId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc)
{
    public static DeviceIdentityConfiguration CreateDefault(string programDataPath)
    {
        return new DeviceIdentityConfiguration(
            CreateStableDeviceId(programDataPath),
            Environment.MachineName,
            DateTimeOffset.UtcNow);
    }

    public DeviceIdentityConfiguration Normalise(string programDataPath)
    {
        var fallback = CreateDefault(programDataPath);
        return this with
        {
            DeviceId = string.IsNullOrWhiteSpace(DeviceId) ? fallback.DeviceId : DeviceId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? fallback.DisplayName : DisplayName.Trim(),
            CreatedAtUtc = CreatedAtUtc == default ? fallback.CreatedAtUtc : CreatedAtUtc
        };
    }

    private static string CreateStableDeviceId(string programDataPath)
    {
        var fullPath = string.IsNullOrWhiteSpace(programDataPath)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(programDataPath);
        var seed = $"{Environment.MachineName}|{fullPath}".ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"fv-device-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}

public sealed record TrustedDeviceConfiguration(
    string DeviceId,
    string DisplayName,
    DeviceTrustState TrustState = DeviceTrustState.Trusted,
    DateTimeOffset TrustedAtUtc = default,
    DateTimeOffset? LastSeenAtUtc = null)
{
    public static TrustedDeviceConfiguration FromLocalDevice(DeviceIdentityConfiguration localDevice)
    {
        return new TrustedDeviceConfiguration(
            localDevice.DeviceId,
            localDevice.DisplayName,
            DeviceTrustState.Local,
            localDevice.CreatedAtUtc);
    }

    public TrustedDeviceConfiguration Normalise()
    {
        return this with
        {
            DeviceId = DeviceId?.Trim() ?? string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? DeviceId?.Trim() ?? string.Empty : DisplayName.Trim(),
            TrustedAtUtc = TrustedAtUtc == default ? DateTimeOffset.UtcNow : TrustedAtUtc
        };
    }
}
