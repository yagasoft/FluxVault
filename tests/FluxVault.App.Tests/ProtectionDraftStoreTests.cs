using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.App.Services;

namespace FluxVault.App.Tests;

public sealed class ProtectionDraftStoreTests
{
    [Fact]
    public void Corrupt_record_quarantine_preserves_exact_bytes_and_refuses_changed_or_oversized_records()
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.Path,"{ interrupted");
        var expected=fixture.Store.InspectPreservedRecord();
        File.WriteAllText(fixture.Path,"{ changed");
        Assert.Throws<InvalidOperationException>(()=>fixture.Store.Quarantine(expected));
        Assert.Equal("{ changed",File.ReadAllText(fixture.Path));
        expected=fixture.Store.InspectPreservedRecord();
        var retained=fixture.Store.Quarantine(expected);
        Assert.False(File.Exists(fixture.Path)); Assert.Equal("{ changed",File.ReadAllText(retained));
        File.WriteAllBytes(fixture.Path,new byte[1024*1024+1]);
        Assert.Throws<InvalidDataException>(()=>fixture.Store.InspectPreservedRecord());
        Assert.Equal(1024*1024+1,new FileInfo(fixture.Path).Length);
    }
    [Fact]
    public void Incomplete_editable_values_are_preserved_without_applying_service_validation()
    {
        using var files = new Files();
        var configuration = files.Draft.Configuration with
        {
            RepositoryPath = string.Empty,
            SelectionRules = [new("scope",files.Root,ProtectionSelectionMode.RegexScope,CompressionPreference.Zstd,ResourceProfile.Balanced,true,
                IncludeRegexRules:[new("typing","[",ProtectionExclusionTarget.Both)])]
        };
        var published = files.Store.Write(null,files.Draft with{Configuration=configuration});
        Assert.Equal(Json(published),Json(new FileProtectionDraftStore(files.Path).Read()));
        Assert.Equal(string.Empty,published.Configuration.RepositoryPath);
        Assert.Equal("[",published.Configuration.SelectionRules.Single().IncludeRegexRules!.Single().Pattern);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("repository")]
    [InlineData("publication")]
    public void A_publication_cannot_reuse_its_compare_identity_or_reassign_the_existing_editing_session(string failure)
    {
        using var files = new Files();
        var first = files.Store.Write(null,files.Draft);
        var next = first with{RecordId=Guid.NewGuid()};
        next = failure switch
        {
            "draft" => next with{DraftId=Guid.NewGuid()},
            "repository" => next with{RepositoryId=Guid.NewGuid()},
            "publication" => next with{RecordId=first.RecordId},
            _ => throw new InvalidOperationException()
        };
        var bytes = File.ReadAllBytes(files.Path);
        Assert.Throws<InvalidDataException>(()=>files.Store.Write(first,next));
        Assert.Equal(bytes,File.ReadAllBytes(files.Path));
    }

    [Fact]
    public void Full_configuration_and_save_correlation_survive_atomic_publication_and_reopening()
    {
        using var files = new Files();
        Assert.Null(files.Store.Read());
        var draft = files.Draft with { SaveOperationId = Guid.NewGuid() };
        var published = files.Store.Write(null, draft);
        Assert.Equal(Json(draft), Json(published));
        Assert.Equal(Json(draft), Json(new FileProtectionDraftStore(files.Path).Read()));
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
        files.Store.Clear(published);
        Assert.Null(files.Store.Read());
    }

    [Fact]
    public void Published_and_read_snapshots_do_not_alias_mutable_caller_collections()
    {
        using var files = new Files();
        var watched = new List<WatchedFolderConfiguration>();
        var draft = files.Draft with { Configuration = files.Draft.Configuration with { WatchedFolders = watched } };
        var published = files.Store.Write(null, draft);
        watched.Add(new("draft",files.Root,true,[],[],CompressionPreference.Zstd,ResourceProfile.Balanced,true));
        Assert.Empty(published.Configuration.WatchedFolders);
        var reopened = files.Store.Read()!;
        Assert.Empty(reopened.Configuration.WatchedFolders);
        Assert.Equal(Json(published), Json(reopened));
    }

    [Fact]
    public void Competing_session_cannot_overwrite_or_discard_a_newer_record()
    {
        using var files = new Files();
        var first = files.Store.Write(null, files.Draft);
        var other = new FileProtectionDraftStore(files.Path);
        var second = other.Write(first, first with { RecordId = Guid.NewGuid(), Configuration = first.Configuration with { IsEnabled = false } });
        var bytes = File.ReadAllBytes(files.Path);
        Assert.Throws<InvalidOperationException>(() => files.Store.Write(first, first with { RecordId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => files.Store.Clear(first));
        Assert.Throws<InvalidOperationException>(() => files.Store.Write(null, first));
        Assert.Equal(bytes, File.ReadAllBytes(files.Path));
        Assert.Equal(Json(second), Json(files.Store.Read()));
        files.Store.Clear(second);
    }

    [Fact]
    public void Expected_snapshot_must_match_content_as_well_as_record_identity()
    {
        using var files = new Files();
        var first = files.Store.Write(null, files.Draft);
        var changed = first with { Configuration = first.Configuration with { IsEnabled = false } };
        var bytes = File.ReadAllBytes(files.Path);
        Assert.Throws<InvalidOperationException>(() => files.Store.Write(changed, first with { RecordId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => files.Store.Clear(changed));
        Assert.Equal(bytes, File.ReadAllBytes(files.Path));
    }

    [Fact]
    public void Failed_or_cancelled_publication_preserves_prior_bytes_and_removes_owned_temporary_file()
    {
        using var files = new Files();
        var first = files.Store.Write(null, files.Draft);
        var bytes = File.ReadAllBytes(files.Path);
        var next = first with { RecordId = Guid.NewGuid(), Configuration = first.Configuration with { IsEnabled = false } };
        using (var locked = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => files.Store.Write(first, next));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => files.Store.Write(first, next, cancelled.Token));
        Assert.Equal(bytes, File.ReadAllBytes(files.Path));
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    [Fact]
    public void Existing_reader_keeps_a_complete_snapshot_across_atomic_replacement()
    {
        using var files = new Files();
        var first = files.Store.Write(null, files.Draft);
        using var oldReader = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var second = files.Store.Write(first, first with { RecordId = Guid.NewGuid(), Configuration = first.Configuration with { IsEnabled = false } });
        using var read = new StreamReader(oldReader);
        Assert.Equal(Json(first), Json(JsonSerializer.Deserialize<PendingProtectionDraft>(read.ReadToEnd(), JsonOptions)));
        Assert.Equal(Json(second), Json(files.Store.Read()));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("draft")]
    [InlineData("record")]
    [InlineData("revision")]
    [InlineData("fingerprint")]
    [InlineData("operation")]
    [InlineData("configuration")]
    [InlineData("oversize")]
    public void Invalid_records_cannot_be_published_or_reinterpreted(string failure)
    {
        using var files = new Files();
        var invalid = failure switch
        {
            "repository" => files.Draft with { RepositoryId = Guid.Empty },
            "draft" => files.Draft with { DraftId = Guid.Empty },
            "record" => files.Draft with { RecordId = Guid.Empty },
            "revision" => files.Draft with { BaselineRevision = 0 },
            "fingerprint" => files.Draft with { BaselineFingerprint = "not-a-fingerprint" },
            "operation" => files.Draft with { SaveOperationId = Guid.Empty },
            "configuration" => files.Draft with { Configuration = null! },
            "oversize" => files.Draft with { Configuration = files.Draft.Configuration with { RepositoryPath = new string('x', 1024 * 1024) } },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() => files.Store.Write(null, invalid));
        Assert.False(File.Exists(files.Path));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(invalid, JsonOptions);
        File.WriteAllBytes(files.Path, bytes);
        Assert.Throws<InvalidDataException>(() => files.Store.Read());
        Assert.Throws<InvalidDataException>(() => files.Store.Write(null, files.Draft));
        Assert.Equal(bytes, File.ReadAllBytes(files.Path));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("alias")]
    [InlineData("truncated")]
    public void Malformed_existing_records_are_preserved_and_block_replacement(string failure)
    {
        using var files = new Files();
        var node = JsonNode.Parse(JsonSerializer.Serialize(files.Draft, JsonOptions))!;
        if (failure == "unknown") node["unknown"] = 1;
        if (failure == "missing") node.AsObject().Remove("configuration");
        if (failure == "alias") node["configuration"]!["RepositoryPath"] = "foreign";
        var content = node.ToJsonString();
        if (failure == "duplicate") content = content.Replace("\"isEnabled\":true", "\"isEnabled\":true,\"isEnabled\":false", StringComparison.Ordinal);
        if (failure == "truncated") content = content[..^7];
        File.WriteAllText(files.Path, content);
        Assert.Throws<InvalidDataException>(() => files.Store.Read());
        Assert.Throws<InvalidDataException>(() => files.Store.Write(null, files.Draft));
        Assert.Equal(content, File.ReadAllText(files.Path));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string Json(PendingProtectionDraft? draft) => JsonSerializer.Serialize(draft, JsonOptions);
    private sealed class Files : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluxVault-draft-store-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "draft.json");
        public FileProtectionDraftStore Store => new(Path);
        public PendingProtectionDraft Draft { get; }
        public Files()
        {
            Directory.CreateDirectory(Root);
            Draft = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, new string('A', 64), FluxVaultConfiguration.CreateDefault(Root));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
