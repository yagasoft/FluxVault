using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;

namespace FluxVault.Core.Security;

/// <summary>Immutable protected bootstrap, published only after durable provisioning.</summary>
public sealed record FluxVaultInstallation(VaultCatalogueEndpoint Endpoint, VaultBinding Binding, string CreatorSid, int SchemaVersion = 1)
{
    public const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static FluxVaultInstallation Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new InvalidDataException("Installation bootstrap size is invalid.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        var installation = JsonSerializer.Deserialize<FluxVaultInstallation>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Installation bootstrap is missing.");
        installation.Validate();
        return installation;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Endpoint is null || Binding is null) throw new InvalidDataException("Installation schema is invalid.");
        Endpoint.Validate(); Binding.Validate();
        if (Endpoint.Host != "localhost" && (!IPAddress.TryParse(Endpoint.Host, out var address) || !IPAddress.IsLoopback(address)) ||
            Binding.MetadataStore.Host != Endpoint.Host || Binding.MetadataStore.Port != Endpoint.Port ||
            Binding.MetadataStore.DatabaseName != Endpoint.Database || Binding.MetadataStore.Username != Endpoint.ServiceRole)
            throw new InvalidDataException("Installation metadata binding is invalid.");
        var parts = CreatorSid?.Split('-');
        if (parts is not { Length: 8 } || parts[0] != "S" || parts[1] != "1" || parts[2] != "5" || parts[3] != "21" ||
            parts.Skip(4).Any(part => !uint.TryParse(part, out var value) || value == 0))
            throw new InvalidDataException("An installation requires its recorded Windows creator.");
    }

    internal static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate installation field.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
