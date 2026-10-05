using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.App.Services;

public sealed record PendingProtectionDraft(Guid DraftId, Guid RecordId, Guid RepositoryId,
    long BaselineRevision, string BaselineFingerprint, FluxVaultConfiguration Configuration,
    Guid? SaveOperationId = null)
{
    internal void Validate()
    {
        if (DraftId == Guid.Empty || RecordId == Guid.Empty || RepositoryId == Guid.Empty || BaselineRevision <= 0 ||
            BaselineFingerprint is not { Length: 64 } || !BaselineFingerprint.All(Uri.IsHexDigit) ||
            Configuration is null || SaveOperationId == Guid.Empty)
            throw new InvalidDataException("The local protection draft has invalid or missing identity, baseline or configuration fields. It has been preserved.");
        if (Configuration.RepositoryPath is null || Configuration.MirrorSet?.Nodes?.Any(node => node is null) == true ||
            Configuration.SelectionRules?.Any(rule => rule is null || rule.Path is null || rule.Id is null ||
                rule.IncludeRegexRules?.Any(regex => regex is null || regex.Pattern is null) == true ||
                rule.ExcludeRegexRules?.Any(regex => regex is null || regex.Pattern is null) == true) == true)
            throw new InvalidDataException("The local protection draft contains malformed editable collections. It has been preserved.");
        // Drafts may contain incomplete editable values. Service validation belongs
        // to an explicit save; local persistence validates structure and bounds.
    }

    internal static string Fingerprint(FluxVaultConfiguration configuration) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration, FileConfigurationSaveOperationStore.JsonOptions)));
}

public sealed record ProtectionDraftEvidence(long ByteLength, string Fingerprint);

public interface IProtectionDraftStore
{
    PendingProtectionDraft? Read();
    PendingProtectionDraft Write(PendingProtectionDraft? expected, PendingProtectionDraft next, CancellationToken cancellationToken = default);
    void Clear(PendingProtectionDraft expected);
    ProtectionDraftEvidence InspectPreservedRecord() => throw new NotSupportedException("This draft store does not support reviewed quarantine.");
    string Quarantine(ProtectionDraftEvidence expected) => throw new NotSupportedException("This draft store does not support reviewed quarantine.");
}

public sealed class FileProtectionDraftStore(string path) : IProtectionDraftStore
{
    private readonly string recordPath = Path.GetFullPath(path);
    public FileProtectionDraftStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxVault", "protection-draft.json")) { }
    public PendingProtectionDraft? Read() { using var gate = OpenGate(); return ReadRecord(); }
    public ProtectionDraftEvidence InspectPreservedRecord()
    {
        using var gate = OpenGate(); return InspectBytes();
    }
    public string Quarantine(ProtectionDraftEvidence expected)
    {
        using var gate = OpenGate();
        if (InspectBytes() != expected)
            throw new InvalidOperationException("The preserved local draft changed after review. It has been kept.");
        var destination = recordPath + "." + Guid.NewGuid().ToString("N") + ".quarantine";
        File.Move(recordPath,destination); // Never overwrite an earlier preserved record.
        return destination;
    }
    private ProtectionDraftEvidence InspectBytes()
    {
        using var input = new FileStream(recordPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (input.Length > 1024 * 1024)
            throw new InvalidDataException("The preserved draft exceeds the inspection bound and requires manual review.");
        return new(input.Length,Convert.ToHexString(SHA256.HashData(input)));
    }
    public PendingProtectionDraft Write(PendingProtectionDraft? expected, PendingProtectionDraft next, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encode(next);
        var frozen = Decode(bytes);
        using var gate = OpenGate();
        var current = ReadRecord();
        VerifyExpected(expected, current);
        if (current is not null && (next.DraftId != current.DraftId || next.RepositoryId != current.RepositoryId || next.RecordId == current.RecordId))
            throw new InvalidDataException("An updated local draft must retain its editing identity and repository, with a new publication identity.");
        var temporary = recordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                output.Write(bytes);
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (current is null) File.Move(temporary, recordPath);
            else File.Replace(temporary, recordPath, null);
            return frozen;
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clear(PendingProtectionDraft expected)
    {
        using var gate = OpenGate();
        VerifyExpected(expected, ReadRecord());
        File.Delete(recordPath);
    }
    private FileStream OpenGate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        return new(recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private PendingProtectionDraft? ReadRecord()
    {
        FileStream input;
        try { input = new(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            if (input.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("The local protection draft is empty or exceeds its bound. It has been preserved.");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            return Decode(bytes);
        }
    }
    private static void VerifyExpected(PendingProtectionDraft? expected, PendingProtectionDraft? current)
    {
        if (expected is null && current is null) return;
        if (expected is null || current is null || !Encode(expected).AsSpan().SequenceEqual(Encode(current)))
            throw new InvalidOperationException("The local protection draft changed in another session. It and your edits are kept; reload and review before continuing.");
    }
    internal static byte[] Encode(PendingProtectionDraft draft)
    {
        draft.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, FileConfigurationSaveOperationStore.JsonOptions);
        FileConfigurationSaveOperationStore.ValidateJson(bytes);
        return bytes;
    }
    private static PendingProtectionDraft Decode(byte[] bytes)
    {
        try
        {
            FileConfigurationSaveOperationStore.ValidateJson(bytes);
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() is not (6 or 7) ||
                new[] { "draftId", "recordId", "repositoryId", "baselineRevision", "baselineFingerprint", "configuration" }
                    .Any(name => !document.RootElement.TryGetProperty(name, out _)))
                throw new InvalidDataException("The local protection draft has missing fields. It has been preserved.");
            var draft = document.RootElement.Deserialize<PendingProtectionDraft>(FileConfigurationSaveOperationStore.JsonOptions)!;
            draft.Validate();
            return draft;
        }
        catch (JsonException exception) { throw new InvalidDataException("The local protection draft could not be read. It has been preserved.", exception); }
    }
}
