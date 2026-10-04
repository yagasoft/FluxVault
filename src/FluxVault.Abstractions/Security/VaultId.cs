using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.Abstractions.Security;

[JsonConverter(typeof(VaultIdJsonConverter))]
public readonly record struct VaultId
{
    public Guid Value { get; }
    public bool IsValid => Value != Guid.Empty;
    public VaultId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A vault requires a nonempty UUID.", nameof(value));
        Value = value;
    }
    public static VaultId New() => new(Guid.NewGuid());
    public string MetadataNamespace
    {
        get { RequireValid(); return "fv_" + Value.ToString("N"); }
    }
    public override string ToString() { RequireValid(); return Value.ToString("D"); }
    private void RequireValid()
    {
        if (!IsValid) throw new InvalidOperationException("An empty vault identity cannot select storage or enter the protocol.");
    }
}

public sealed class VaultIdJsonConverter : JsonConverter<VaultId>
{
    public override VaultId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A canonical vault UUID is required.");
        var text = reader.GetString();
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty || text != value.ToString("D"))
            throw new JsonException("A canonical nonempty vault UUID is required.");
        return new(value);
    }
    public override void Write(Utf8JsonWriter writer, VaultId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
