using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.App.Services;

namespace FluxVault.App.Tests;

public sealed class SaveOperationStoreTests
{
    [Theory]
    [InlineData(987, false)]
    [InlineData((int)ConfigurationSaveOrigin.Options, true)]
    public void Unknown_origin_and_options_purge_intent_are_refused_before_reservation(int origin, bool purge)
    {
        using var files = new Files();
        var snapshot = files.Snapshot with { Origin = (ConfigurationSaveOrigin)origin, PurgeRemovedSelections = purge };
        Assert.Throws<InvalidDataException>(() => files.Store.Reserve(snapshot));
        Assert.False(File.Exists(files.Path));
    }

    [Fact]
    public void Options_records_with_removed_or_preserved_scopes_are_refused_and_existing_bytes_are_kept()
    {
        using var files = new Files();
        foreach (var preserved in new[] { false, true })
        {
            var scope = new FluxVault.Abstractions.Storage.RepositoryPurgeScope("folder", FluxVault.Abstractions.Storage.RepositoryPurgeScopeKind.RecursiveFolder);
            var snapshot = files.Snapshot with { Origin = ConfigurationSaveOrigin.Options, PurgeRemovedSelections = false,
                RemovedSelections = preserved ? [] : [scope], PreservedSelections = preserved ? [scope] : [] };
            Assert.Throws<InvalidDataException>(() => files.Store.Reserve(snapshot));
            var bytes = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllText(files.Path, bytes);
            Assert.Throws<InvalidDataException>(() => files.Store.Read());
            Assert.Equal(bytes, File.ReadAllText(files.Path));
            File.Delete(files.Path);
        }
    }

    [Fact]
    public void Existing_record_without_origin_keeps_its_filename_and_defaults_to_protection()
    {
        using var files = new Files();
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(files.Snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        node.AsObject().Remove("origin");
        File.WriteAllText(files.Path, node.ToJsonString());
        var pending = files.Store.Read()!;
        Assert.Equal(ConfigurationSaveOrigin.Protect, pending.Origin);
        files.Store.Clear(pending);
        Assert.False(File.Exists(files.Path));
    }
    [Fact]
    public void Case_variant_nested_fields_are_refused_without_reinterpreting_the_snapshot()
    {
        using var files = new Files();
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(files.Snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        node["configuration"]!["RepositoryPath"] = "foreign-alias";
        var content = node.ToJsonString();
        File.WriteAllText(files.Path, content);
        Assert.Throws<InvalidDataException>(() => files.Store.Read());
        Assert.Equal(content, File.ReadAllText(files.Path));
    }
    [Fact]
    public void Oversized_wire_snapshot_is_refused_before_creating_a_record()
    {
        using var files = new Files();
        var snapshot = files.Snapshot;
        var oversized = snapshot with { Configuration = snapshot.Configuration with { RepositoryPath = new string('x', 1024 * 1024) } };
        Assert.Throws<InvalidDataException>(() => files.Store.Reserve(oversized));
        Assert.False(File.Exists(files.Path));
    }

    [Fact]
    public void Duplicate_nested_configuration_fields_are_refused_and_preserved()
    {
        using var files = new Files();
        var content = JsonSerializer.Serialize(files.Snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Replace("\"isEnabled\":false", "\"isEnabled\":false,\"isEnabled\":true", StringComparison.Ordinal);
        File.WriteAllText(files.Path, content);
        Assert.Throws<InvalidDataException>(() => files.Store.Read());
        Assert.Equal(content, File.ReadAllText(files.Path));
    }

    [Fact]
    public void Full_snapshot_survives_reopening_and_cannot_be_overwritten_or_cleared_by_a_different_payload()
    {
        using var files = new Files();
        var first = files.Snapshot;
        files.Store.Reserve(first);
        var read = Assert.IsType<PendingConfigurationSave>(new FileConfigurationSaveOperationStore(files.Path).Read());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(read));
        var changed = first with { Configuration = first.Configuration with { IsEnabled = !first.Configuration.IsEnabled } };
        Assert.Throws<InvalidOperationException>(() => files.Store.Clear(changed));
        Assert.Throws<InvalidOperationException>(() => files.Store.Reserve(first with { OperationId = Guid.NewGuid() }));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(files.Store.Read()));
        files.Store.Clear(read);
        var next = first with { OperationId = Guid.NewGuid() };
        files.Store.Reserve(next);
        Assert.Throws<InvalidOperationException>(() => files.Store.Clear(first));
        files.Store.Clear(next);
        Assert.Null(files.Store.Read());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"revision\":1,\"revision\":2}")]
    public void Corrupt_records_are_preserved_and_block_dispatch_reservation(string content)
    {
        using var files = new Files();
        File.WriteAllText(files.Path, content);
        Assert.Throws<InvalidDataException>(() => files.Store.Read());
        Assert.Throws<InvalidDataException>(() => files.Store.Reserve(files.Snapshot));
        Assert.Equal(content, File.ReadAllText(files.Path));
    }

    [Fact]
    public void Busy_gate_refuses_access_without_creating_a_record()
    {
        using var files = new Files();
        using var gate = new FileStream(files.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => files.Store.Reserve(files.Snapshot));
        Assert.False(File.Exists(files.Path));
    }

    [Fact]
    public async Task Concurrent_sessions_can_reserve_only_one_save()
    {
        using var files = new Files();
        var operations = Enumerable.Range(0, 8).Select(_ => files.Snapshot).ToArray();
        var results = await Task.WhenAll(operations.Select(operation => Task.Run(() => Record.Exception(() => files.Store.Reserve(operation)))));
        Assert.Single(results, exception => exception is null);
        Assert.All(results.Where(exception => exception is not null), exception => Assert.True(exception is IOException or InvalidOperationException));
        Assert.Contains(files.Store.Read()!.OperationId, operations.Select(operation => operation.OperationId));
    }

    private sealed class Files : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluxVault.SaveOperation." + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "pending.json");
        public FileConfigurationSaveOperationStore Store => new(Path);
        public PendingConfigurationSave Snapshot => new(Guid.NewGuid(), Guid.NewGuid(), 3,
            FluxVaultConfiguration.CreateDefault(root) with { IsEnabled = false, VersionPreview = new(19) }, false, [], []);
        public Files() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, true);
    }
}
