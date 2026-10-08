using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.App.Services;

public enum ConfigurationSaveOrigin { Protect, Options, HistoryDeletion }

public sealed record PendingConfigurationSave(Guid RepositoryId, Guid OperationId, long Revision,
    FluxVaultConfiguration Configuration, bool PurgeRemovedSelections,
    IReadOnlyList<RepositoryPurgeScope> RemovedSelections, IReadOnlyList<RepositoryPurgeScope> PreservedSelections,
    ConfigurationSaveOrigin Origin = ConfigurationSaveOrigin.Protect, Guid? ProtectionDraftId = null,
    string? ProtectionDraftBaselineFingerprint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsProtectionPaused = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HistoryDeletionFingerprint = null)
{
    internal void Validate()
    {
        if (RepositoryId == Guid.Empty || OperationId == Guid.Empty || Revision <= 0 || Revision == long.MaxValue ||
            Configuration is null || string.IsNullOrWhiteSpace(Configuration.RepositoryPath) || RemovedSelections is null || PreservedSelections is null)
            throw new InvalidDataException("The pending protection save has invalid or missing fields. It has been preserved.");
        if (!Enum.IsDefined(Origin) || Origin == ConfigurationSaveOrigin.Options &&
            (PurgeRemovedSelections || RemovedSelections.Count != 0 || PreservedSelections.Count != 0))
            throw new InvalidDataException("The pending save has an invalid origin or incompatible purge intent. It has been preserved.");
        if (IsProtectionPaused is { } paused && (Origin != ConfigurationSaveOrigin.Protect ||
            Configuration.IsEnabled == paused || PurgeRemovedSelections || RemovedSelections.Count != 0 || PreservedSelections.Count != 0))
            throw new InvalidDataException("The pending protection control has incompatible settings or deletion intent. It has been preserved.");
        if(Origin==ConfigurationSaveOrigin.HistoryDeletion ? PurgeRemovedSelections || IsProtectionPaused is not null ||
            RemovedSelections.Count!=1 || PreservedSelections.Count!=0 || HistoryDeletionFingerprint is not {Length:64} ||
            !HistoryDeletionFingerprint.All(Uri.IsHexDigit) : HistoryDeletionFingerprint is not null)
            throw new InvalidDataException("The pending history deletion requires its exact scope and reviewed fingerprint. Its record has been preserved.");
        if (ProtectionDraftId is null != (ProtectionDraftBaselineFingerprint is null) || ProtectionDraftId == Guid.Empty ||
            ProtectionDraftBaselineFingerprint is not null &&
            (ProtectionDraftBaselineFingerprint.Length != 64 || !ProtectionDraftBaselineFingerprint.All(Uri.IsHexDigit)) ||
            Origin != ConfigurationSaveOrigin.Protect && ProtectionDraftId is not null)
            throw new InvalidDataException("Only a protection save may reference a valid local editing draft. Its record has been preserved.");
    }
    internal FluxVaultIpcRequest Request => (Origin==ConfigurationSaveOrigin.HistoryDeletion
        ? FluxVaultIpcRequest.DeleteHistory(RemovedSelections.Single(),HistoryDeletionFingerprint!)
        : IsProtectionPaused is { } paused
        ? FluxVaultIpcRequest.SetProtectionPaused(paused)
        : FluxVaultIpcRequest.SaveConfiguration(Configuration,
            purgeRemovedSelections: PurgeRemovedSelections, removedSelections: RemovedSelections, preservedSelections: PreservedSelections)) with
        { VaultId = new VaultId(RepositoryId), OperationId = OperationId, ExpectedVaultRevision = Revision };
    internal PendingConfigurationSave Freeze()
    {
        Validate();
        var request = JsonSerializer.SerializeToUtf8Bytes(Request, FileConfigurationSaveOperationStore.JsonOptions);
        FileConfigurationSaveOperationStore.ValidateJson(request);
        return JsonSerializer.Deserialize<PendingConfigurationSave>(FileConfigurationSaveOperationStore.Encode(this), FileConfigurationSaveOperationStore.JsonOptions)!;
    }
}

public interface IConfigurationSaveOperationStore
{
    PendingConfigurationSave? Read();
    void Reserve(PendingConfigurationSave save);
    void Clear(PendingConfigurationSave save);
}

public sealed class FileConfigurationSaveOperationStore(string path) : IConfigurationSaveOperationStore
{
    private readonly string recordPath = Path.GetFullPath(path);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    private const int MaximumBytes = 1024 * 1024;
    public FileConfigurationSaveOperationStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxVault", "pending-protection-save.json")) { }
    public PendingConfigurationSave? Read() { using var gate = OpenGate(); return ReadRecord(); }
    public void Reserve(PendingConfigurationSave save)
    {
        var bytes = Encode(save.Freeze()); // Validate the complete frozen wire snapshot before creating a record.
        using var gate = OpenGate();
        if (ReadRecord() is not null) throw new InvalidOperationException("A protection save outcome is still pending.");
        using var output = new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }
    public void Clear(PendingConfigurationSave save)
    {
        var expected = Encode(save);
        using var gate = OpenGate();
        var pending = ReadRecord();
        if (pending is null) return;
        if (!expected.AsSpan().SequenceEqual(Encode(pending)))
            throw new InvalidOperationException("The pending protection save changed in another session. It has been preserved.");
        File.Delete(recordPath);
    }
    private FileStream OpenGate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        return new(recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private PendingConfigurationSave? ReadRecord()
    {
        FileStream input;
        try { input = new(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("The pending save exceeds its bound or is empty.");
            var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            try
            {
                ValidateJson(bytes);
                using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() is not (7 or 8 or 9 or 10 or 11 or 12) ||
                    new[] { "repositoryId", "operationId", "revision", "configuration", "purgeRemovedSelections", "removedSelections", "preservedSelections" }
                        .Any(name => !document.RootElement.TryGetProperty(name, out _)))
                    throw new InvalidDataException("The pending save has missing fields. It has been preserved.");
                var pending = document.RootElement.Deserialize<PendingConfigurationSave>(JsonOptions)!;
                pending.Validate();
                return pending.Freeze();
            }
            catch (JsonException exception) { throw new InvalidDataException("The pending save could not be read. It has been preserved.", exception); }
        }
    }
    internal static byte[] Encode(PendingConfigurationSave save)
    {
        save.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(save, JsonOptions);
        ValidateJson(bytes);
        return bytes;
    }
    internal static void ValidateJson(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("The pending save exceeds its bound or is empty.");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The pending save contains duplicate fields. It has been preserved.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}

internal sealed class MemoryConfigurationSaveOperationStore : IConfigurationSaveOperationStore
{
    private PendingConfigurationSave? pending;
    public PendingConfigurationSave? Read() => pending;
    public void Reserve(PendingConfigurationSave save)
    {
        if (pending is not null) throw new InvalidOperationException("A protection save is still pending.");
        pending = save.Freeze();
    }
    public void Clear(PendingConfigurationSave save)
    {
        if (pending is not null && !FileConfigurationSaveOperationStore.Encode(pending).AsSpan().SequenceEqual(FileConfigurationSaveOperationStore.Encode(save)))
            throw new InvalidOperationException("The pending protection save changed.");
        pending = null;
    }
}
