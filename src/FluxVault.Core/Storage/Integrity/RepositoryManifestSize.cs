using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Integrity;

internal static class RepositoryManifestSize
{
    // Indented string enums conservatively cover legacy JSON and PostgreSQL JSONB text.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static int Measure(FileVersionManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, Options).Length;

    internal static void Validate(FileVersionManifest manifest, RepositoryIntegrityLimits limits)
    {
        if (Measure(manifest) > limits.MaxManifestBytes)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded,
                "Manifest exceeds the supported integrity limit; no version was recorded.");
    }
}
