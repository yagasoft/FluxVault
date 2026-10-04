namespace FluxVault.Core.Security;

/// <summary>Pure syntax validation shared by catalogue admission and native caller file access.</summary>
/// <remarks>Does not inspect, resolve or create filesystem objects. Native handles still enforce actual caller access.</remarks>
public static class WindowsLocalPath
{
    public static string Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32767 || value.Length < 3 || !char.IsAsciiLetter(value[0]) ||
            value[1] != ':' || value[2] != '\\') Denied("An absolute local drive path is required.");
        if (value[2..].Contains("\\\\", StringComparison.Ordinal)) Denied("Ambiguous Windows paths are unsupported.");
        var trimmed = value.TrimEnd('\\');
        foreach (var part in trimmed[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(character => character < 32 || "<>:\"/|?*".Contains(character))) Denied("Ambiguous Windows paths are unsupported.");
            var device = part.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(device[3])) Denied("Windows device names are unsupported.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static void Denied(string reason) => throw new UnauthorizedAccessException(reason);
}
