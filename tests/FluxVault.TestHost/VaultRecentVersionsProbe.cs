using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Metadata;
using Npgsql;
using NpgsqlTypes;

namespace FluxVault.TestHost;

/// <summary>Runs only inside the ownership-checked, disposable SSPI database fixture.</summary>
internal static class VaultRecentVersionsProbe
{
    internal static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource source, VaultBinding binding,
        PostgreSqlRepositoryMetadataStore store, FileVersionManifest template)
    {
        var checks = new List<string>();
        var captured = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var manifests = Enumerable.Range(1000, 80).Select(index => template with
        {
            VersionId = index.ToString("x32"), SourcePath = Path.Combine(Path.GetDirectoryName(template.SourcePath)!, "recent.docx"),
            CapturedAtUtc = captured
        }).Append(template with { VersionId = "a" + new string('0', 31), CapturedAtUtc = captured })
          .Append(template with { VersionId = "B" + new string('0', 31), CapturedAtUtc = captured }).ToArray();
        var preciseBaseTicks = template.CapturedAtUtc.AddDays(1).UtcTicks;
        var preciseBase = new DateTimeOffset(preciseBaseTicks - preciseBaseTicks % 10, TimeSpan.Zero);
        var precise = new[]
        {
            template with { VersionId = new string('f', 32), CapturedAtUtc = preciseBase },
            template with { VersionId = new string('0', 32), CapturedAtUtc = preciseBase.AddTicks(1) }
        };
        var ids = manifests.Concat(precise).Select(manifest => manifest.VersionId).ToArray();
        IRepositoryMetadataStore recent = store;
        await using var connection = await source.OpenConnectionAsync();
        var schema = '"' + binding.MetadataNamespace + '"';
        try
        {
            await store.RecordVersionsAsync(manifests);
            var complete = await store.ListVersionsAsync();
            foreach (var count in new[] { 1, 50, 200 })
                Check(JsonSerializer.Serialize(complete.Take(count)) == JsonSerializer.Serialize(await recent.ListRecentVersionsAsync(count)),
                    "recent limit " + count + " preserves full-inventory order and fields");
            Check(complete.Where(version => version.CapturedAtUtc == captured).Take(2).Select(version => version.VersionId)
                .SequenceEqual(new[] { manifests[^2].VersionId, manifests[^1].VersionId }), "mixed-case identifier ties use ordinal order");
            await store.RecordVersionsAsync(precise);
            Check((await recent.ListRecentVersionsAsync(1)).Single().VersionId == precise[1].VersionId,
                "one-tick boundary retains manifest precision instead of PostgreSQL timestamp precision");

            var oldest = manifests[0];
            var original = await Scalar($"SELECT manifest_json::text FROM {schema}.versions WHERE version_id=@id", oldest.VersionId);
            try
            {
                await SetManifest(oldest.VersionId, "{}");
                Check((await recent.ListRecentVersionsAsync(50)).Count == 50, "recent query never deserialises a corrupt manifest outside its window");
                await Invalid(() => store.ListVersionsAsync(), "complete history still validates out-of-window manifests");
            }
            finally { await SetManifest(oldest.VersionId, (string)original!); }

            var newest = precise[1];
            original = await Scalar($"SELECT manifest_json::text FROM {schema}.versions WHERE version_id=@id", newest.VersionId);
            try
            {
                await SetManifest(newest.VersionId, "{}");
                await Invalid(() => recent.ListRecentVersionsAsync(1), "selected manifest corruption fails closed");
            }
            finally { await SetManifest(newest.VersionId, (string)original!); }

            var caseSensitive = manifests[^1];
            original = await Scalar($"SELECT manifest_json::text FROM {schema}.versions WHERE version_id=@id", caseSensitive.VersionId);
            try
            {
                var poisoned = JsonNode.Parse((string)original!)!;
                poisoned["versionId"] = caseSensitive.VersionId.ToLowerInvariant();
                await SetManifest(caseSensitive.VersionId, poisoned.ToJsonString());
                await Invalid(() => recent.ListRecentVersionsAsync(50), "selected identifier must exactly match its ordinal SQL ordering key");
            }
            finally { await SetManifest(caseSensitive.VersionId, (string)original!); }

            await Execute($"UPDATE {schema}.versions SET captured_at_ticks=captured_at_ticks+1 WHERE version_id=@id", newest.VersionId);
            try { await Invalid(() => recent.ListRecentVersionsAsync(1), "selected ordering key must match its immutable manifest"); }
            finally { await Execute($"UPDATE {schema}.versions SET captured_at_ticks=captured_at_ticks-1 WHERE version_id=@id", newest.VersionId); }

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await recent.ListRecentVersionsAsync(1, cancelled.Token); throw new InvalidOperationException("Cancelled recent read succeeded."); }
            catch (OperationCanceledException) { checks.Add("recent read honours cancellation"); }
            try { await recent.ListRecentVersionsAsync(0); throw new InvalidOperationException("Invalid limit succeeded."); }
            catch (ArgumentOutOfRangeException) { checks.Add("recent read refuses a non-positive limit"); }

            // The normal query planner is free to scan a tiny fixture; prove the supporting index is usable.
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var disableScan = new NpgsqlCommand("SET LOCAL enable_seqscan=off", connection, transaction);
                await disableScan.ExecuteNonQueryAsync();
                await using var plan = new NpgsqlCommand($"""
                    EXPLAIN (FORMAT JSON) SELECT version_id FROM {schema}.versions
                    ORDER BY captured_at_ticks DESC, version_id COLLATE "C" DESC LIMIT 50
                    """, connection, transaction);
                var explanation = (string)(await plan.ExecuteScalarAsync())!;
                Check(explanation.Contains("ix_versions_recent_ticks", StringComparison.Ordinal), "bounded recent ordering can use its supporting index");
                await transaction.RollbackAsync();
            }
            await RefuseOlderSchema(source, binding);
            checks.Add("actual store refuses v1 and malformed v2 schemas without changing existing schema or data");
        }
        finally
        {
            try { await store.DeleteVersionsAsync(ids); }
            finally
            {
                await using var retire = new NpgsqlCommand($"DELETE FROM {schema}.metadata_outbox WHERE version_id=ANY(@ids)", connection);
                retire.Parameters.AddWithValue("ids", ids);
                await retire.ExecuteNonQueryAsync();
            }
        }
        return checks;

        void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Recent inventory contract failed: " + name); checks.Add(name); }
        async Task Invalid(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception exception) when (exception is RepositoryIntegrityException or InvalidDataException) { checks.Add(name); return; }
            throw new InvalidOperationException("Recent inventory did not reject: " + name);
        }
        async Task<object?> Scalar(string sql, string id)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            return await command.ExecuteScalarAsync();
        }
        async Task Execute(string sql, string id)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync();
        }
        async Task SetManifest(string id, string json)
        {
            await using var command = new NpgsqlCommand($"UPDATE {schema}.versions SET manifest_json=@json WHERE version_id=@id", connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.Add("json", NpgsqlDbType.Jsonb).Value = json;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task RefuseOlderSchema(NpgsqlDataSource source, VaultBinding binding)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var exists = new NpgsqlCommand("SELECT to_regnamespace('fluxvault') IS NOT NULL", connection);
        if (await exists.ExecuteScalarAsync() is true) throw new InvalidOperationException("Refusal probe cannot own an existing namespace.");
        // This is the fixture's unused legacy namespace, never an installed endpoint.
        foreach (var shape in new[] { (Version: 1, Column: ""), (Version: 2, Column: ", captured_at_ticks text NOT NULL"),
            (Version: 2, Column: ", captured_at_ticks bigint NULL") })
        {
            await using var create = new NpgsqlCommand($"""
                CREATE SCHEMA fluxvault;
                CREATE TABLE fluxvault.schema_version(version integer PRIMARY KEY);
                INSERT INTO fluxvault.schema_version VALUES ({shape.Version});
                CREATE TABLE fluxvault.versions(version_id text PRIMARY KEY, manifest_json jsonb NOT NULL{shape.Column});
                INSERT INTO fluxvault.versions(version_id,manifest_json{(shape.Column.Contains("NOT NULL", StringComparison.Ordinal) ? ",captured_at_ticks" : "")})
                VALUES ('preserved','true'{(shape.Column.Contains("NOT NULL", StringComparison.Ordinal) ? ",'123'" : "")});
                """, connection);
            await create.ExecuteNonQueryAsync();
            try
            {
                var before = await Snapshot();
                await using var legacy = new PostgreSqlRepositoryMetadataStore(binding.MetadataStore);
                try { await legacy.InitializeAsync(); throw new InvalidOperationException("Existing unsupported schema was accepted."); }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.FeatureNotSupported) { }
                if (before != await Snapshot()) throw new InvalidOperationException("Rejected schema was partially changed.");
            }
            finally
            {
                await using var drop = new NpgsqlCommand("DROP SCHEMA fluxvault CASCADE", connection);
                await drop.ExecuteNonQueryAsync();
            }
        }

        async Task<string> Snapshot()
        {
            await using var snapshot = new NpgsqlCommand("""
                SELECT json_build_object(
                    'relations',(SELECT json_agg(relname ORDER BY relname) FROM pg_class WHERE relnamespace='fluxvault'::regnamespace),
                    'columns',(SELECT json_agg(row(attname,atttypid,attnotnull) ORDER BY attnum) FROM pg_attribute WHERE attrelid='fluxvault.versions'::regclass AND attnum>0 AND NOT attisdropped),
                    'versions',(SELECT json_agg(version ORDER BY version) FROM fluxvault.schema_version),
                    'data',(SELECT json_agg(row_to_json(v) ORDER BY version_id) FROM fluxvault.versions v))::text
                """, connection);
            return (string)(await snapshot.ExecuteScalarAsync())!;
        }
    }
}
