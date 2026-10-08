using System.IO;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using Npgsql;

namespace FluxVault.TestHost;

internal static class VaultCurrentEntriesProbe
{
    internal static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource source, VaultBinding binding,
        PostgreSqlRepositoryMetadataStore store, FileVersionManifest template)
    {
        var checks = new List<string>();
        var failures = new List<string>();
        var root = Path.Combine(Path.GetDirectoryName(template.SourcePath)!, "current-projection");
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(20);
        FileVersionManifest Manifest(int id, string name, int ticks = 0) => template with
        { VersionId = id.ToString("x32"), SourcePath = Path.Combine(root, name), CapturedAtUtc = time.AddTicks(ticks),
            Chunks = [], LogicalLength = 0, FolderEntries = null, ParentVersionIds = null };
        var older = Manifest(8001, "precision.dwg");
        var newer = Manifest(8000, "precision.dwg", 1);
        var caseOld = Manifest(8010, "case.dwg");
        var caseNew = Manifest(8011, "case.dwg", 100) with { SourcePath = caseOld.SourcePath.ToUpperInvariant() };
        var tieUpper = Manifest(8020, "tie.dwg") with { VersionId = new string('8', 31) + "A" };
        var tieLower = tieUpper with { VersionId = new string('8', 31) + "a" };
        var tombstone = newer with { VersionId = 8030.ToString("x32"), CapturedAtUtc = time.AddTicks(100), IsDeleted = true };
        var newestLive = newer with { VersionId = 8031.ToString("x32"), CapturedAtUtc = time.AddTicks(200) };
        var file = Manifest(8040, "kind");
        var folder = Manifest(8041, "kind") with { EntryKind = RepositoryEntryKind.Folder, FolderEntries = [] };
        var rollbackFirst = newer with { VersionId = 8050.ToString("x32"), CapturedAtUtc = time.AddTicks(500) };
        var rollbackSecond = Manifest(8051, "rollback.dwg");
        var ids = new[] { older, newer, caseOld, caseNew, tieUpper, tieLower, tombstone, newestLive, file, folder, rollbackFirst, rollbackSecond }
            .Select(m => m.VersionId).ToArray();
        var schema = '"' + binding.MetadataNamespace + '"';
        await using var connection = await source.OpenConnectionAsync();
        try
        {
            await store.RecordVersionsAsync([older, newer, caseOld, caseNew, tieUpper, tieLower, file, folder]);
            Check(Winner(await Current(newer.SourcePath))?.VersionId == newer.VersionId,
                "current winner preserves 100 ns ordering");
            Check((await store.FindLatestManifestAsync(newer.SourcePath, RepositoryEntryKind.File))?.VersionId == newer.VersionId,
                "latest manifest agrees with exact current winner");
            Check(Winner(await Current(caseOld.SourcePath))?.VersionId == caseNew.VersionId,
                "case-equivalent Windows path has one current winner");
            Check((await store.FindLatestManifestAsync(caseOld.SourcePath.ToLowerInvariant(), RepositoryEntryKind.File))?.VersionId == caseNew.VersionId,
                "latest lookup resolves canonical path identity");
            Check(Winner(await Current(tieUpper.SourcePath))?.VersionId == tieLower.VersionId,
                "current winner uses ordinal case-distinct ID ties");
            Check((await Current(file.SourcePath)).Count == 2, "file and folder identities remain distinct");
            await store.RecordVersionsAsync([newer, older, caseOld, tieUpper]);
            Check(Winner(await Current(newer.SourcePath))?.VersionId == newer.VersionId,
                "out-of-order replay cannot replace a newer current entry");
            Check(Winner(await Current(caseOld.SourcePath))?.SourcePath == caseNew.SourcePath,
                "display casing comes from the winning manifest after replay");
            await store.RecordVersionAsync(tombstone);
            await store.DeleteVersionsAsync([older.VersionId]);
            Check(Winner(await Current(newer.SourcePath))?.VersionId == tombstone.VersionId,
                "deleting non-current history preserves its current tombstone");
            await store.RecordVersionAsync(newestLive);
            await store.DeleteVersionsAsync([newestLive.VersionId]);
            Check(Winner(await Current(newer.SourcePath)) is { IsDeleted: true } retained && retained.VersionId == tombstone.VersionId,
                "deletion fallback includes retained tombstones");
            await store.DeleteVersionsAsync([tombstone.VersionId]);
            Check(Winner(await Current(newer.SourcePath))?.VersionId == newer.VersionId,
                "deleting the current version restores retained history");
            await store.RecordVersionAsync(newer);

            var before = await Generation();
            await Sql($"CREATE FUNCTION {schema}.fv_current_probe_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.version_id='{rollbackSecond.VersionId}' THEN RAISE EXCEPTION 'Owned current-projection rollback probe'; END IF; RETURN NEW; END $$; CREATE TRIGGER fv_current_probe_failure BEFORE INSERT OR UPDATE ON {schema}.current_entries FOR EACH ROW EXECUTE FUNCTION {schema}.fv_current_probe_failure();");
            var rolledBack = false;
            try { await store.RecordVersionsAsync([rollbackFirst, rollbackSecond]); }
            catch (PostgresException ex) when (ex.SqlState == "P0001") { rolledBack = true; }
            finally { await DropTrigger(); }
            var rollbackHistory = await store.ListVersionsAsync();
            Check(rolledBack && await Generation() == before && Winner(await Current(newer.SourcePath))?.VersionId == newer.VersionId &&
                rollbackHistory.All(v => v.VersionId != rollbackFirst.VersionId && v.VersionId != rollbackSecond.VersionId),
                "failed batch rolls back current pointer and generation together");

            await store.RecordVersionAsync(newestLive);
            before = await Generation();
            await Sql($"CREATE FUNCTION {schema}.fv_current_probe_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.version_id='{newer.VersionId}' THEN RAISE EXCEPTION 'Owned current-projection deletion rollback probe'; END IF; RETURN NEW; END $$; CREATE TRIGGER fv_current_probe_failure BEFORE INSERT OR UPDATE ON {schema}.current_entries FOR EACH ROW EXECUTE FUNCTION {schema}.fv_current_probe_failure();");
            rolledBack = false;
            try { await store.DeleteVersionsAsync([newestLive.VersionId]); }
            catch (PostgresException ex) when (ex.SqlState == "P0001") { rolledBack = true; }
            finally { await DropTrigger(); }
            rollbackHistory = await store.ListVersionsAsync();
            Check(rolledBack && await Generation() == before && Winner(await Current(newer.SourcePath))?.VersionId == newestLive.VersionId &&
                rollbackHistory.Any(v => v.VersionId == newestLive.VersionId) && rollbackHistory.Any(v => v.VersionId == newer.VersionId),
                "failed deletion rebuild rolls back removed history, current pointer and generation together");
            await store.DeleteVersionsAsync([newestLive.VersionId]);

            await using (var poison = new NpgsqlCommand($"UPDATE {schema}.current_entries SET version_id=@wrong WHERE source_path=@path", connection))
            {
                poison.Parameters.AddWithValue("wrong", caseNew.VersionId); poison.Parameters.AddWithValue("path", newer.SourcePath);
                Check(await poison.ExecuteNonQueryAsync() == 1, "poison fixture affects exactly one current pointer");
            }
            var rejected = false;
            try { await store.ListLatestEntriesAsync(); }
            catch (RepositoryIntegrityException) { rejected = true; }
            finally
            {
                await using var restore = new NpgsqlCommand($"UPDATE {schema}.current_entries SET version_id=@right WHERE source_path=@path", connection);
                restore.Parameters.AddWithValue("right", newer.VersionId); restore.Parameters.AddWithValue("path", newer.SourcePath);
                await restore.ExecuteNonQueryAsync();
            }
            Check(rejected, "wrong-path current pointer fails closed");
            await Sql($"UPDATE {schema}.current_entries SET captured_at_ticks=captured_at_ticks+1 WHERE version_id='{newer.VersionId}'");
            rejected = false;
            try { await store.ListLatestEntriesAsync(); }
            catch (RepositoryIntegrityException) { rejected = true; }
            finally { await Sql($"UPDATE {schema}.current_entries SET captured_at_ticks={newer.CapturedAtUtc.UtcTicks} WHERE version_id='{newer.VersionId}'"); }
            Check(rejected, "wrong-tick current pointer fails closed");
            await RefuseShape("schema v3", $"UPDATE {schema}.schema_version SET version=3", $"UPDATE {schema}.schema_version SET version=4");
            await RefuseShape("nullable current tick key", $"ALTER TABLE {schema}.current_entries ALTER COLUMN captured_at_ticks DROP NOT NULL",
                $"ALTER TABLE {schema}.current_entries ALTER COLUMN captured_at_ticks SET NOT NULL");
            await RefuseShape("missing canonical primary key", $"ALTER TABLE {schema}.current_entries DROP CONSTRAINT current_entries_pkey",
                $"ALTER TABLE {schema}.current_entries ADD CONSTRAINT current_entries_pkey PRIMARY KEY(path_id)");
            await RefuseShape("missing path foreign key", $"ALTER TABLE {schema}.current_entries DROP CONSTRAINT current_entries_path_id_fkey",
                $"ALTER TABLE {schema}.current_entries ADD CONSTRAINT current_entries_path_id_fkey FOREIGN KEY(path_id) REFERENCES {schema}.paths(path_id)");
            await store.DeleteVersionsAsync([tieUpper.VersionId, tieLower.VersionId]);
            Check((await store.ListVersionsAsync()).All(v => v.VersionId != tieUpper.VersionId && v.VersionId != tieLower.VersionId),
                "deletion preserves exact case-distinct identifier selection");
            foreach (var id in ids) await store.DeleteVersionsAsync([id]);
            Check((await store.ListLatestEntriesAsync()).All(v => !v.SourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)),
                "deleting all path history removes its current row");
            if (failures.Count != 0) throw new InvalidOperationException("Current projection regressions: " + string.Join("; ", failures));
            return checks;
        }
        finally
        {
            await DropTrigger();
            try { foreach (var id in ids) await store.DeleteVersionsAsync([id]); }
            finally
            {
                await using var retire = new NpgsqlCommand($"DELETE FROM {schema}.metadata_outbox WHERE version_id=ANY(@ids)", connection);
                retire.Parameters.AddWithValue("ids", ids); await retire.ExecuteNonQueryAsync();
            }
        }
        async Task<IReadOnlyList<RepositoryVersionSummary>> Current(string path) =>
            (await store.ListLatestEntriesAsync()).Where(v => string.Equals(v.SourcePath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        async Task<long> Generation() { await using var command = new NpgsqlCommand($"SELECT generation FROM {schema}.history_state WHERE singleton=true", connection); return (long)(await command.ExecuteScalarAsync())!; }
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
        Task DropTrigger() => Sql($"DROP TRIGGER IF EXISTS fv_current_probe_failure ON {schema}.current_entries; DROP FUNCTION IF EXISTS {schema}.fv_current_probe_failure();");
        async Task RefuseShape(string name, string weaken, string restore)
        {
            await Sql(weaken);
            try
            {
                var snapshot = await Snapshot();
                var cachedRefused = false;
                try { await store.ListLatestEntriesAsync(); }
                catch (RepositoryIntegrityException) { cachedRefused = true; }
                Check(cachedRefused, "cached binding refuses " + name);
                var provisionRefused = false;
                try { await store.ProvisionVaultAsync(); }
                catch (RepositoryIntegrityException ex) when (ex.Code == RepositoryIntegrityFailure.OwnershipMismatch) { provisionRefused = true; }
                var guardRefused = false;
                try { await Sql(PostgreSqlMetadataSchema.ForVault(binding.Id)); }
                catch (PostgresException ex) when (ex.SqlState == "0A000") { guardRefused = true; }
                Check(provisionRefused && guardRefused, "provisioning and schema guard refuse " + name);
                Check(await Snapshot() == snapshot, "refusal preserves data and schema for " + name);
            }
            finally { await Sql(restore); }
        }
        async Task<string> Snapshot()
        {
            await using var command = new NpgsqlCommand($"""
                SELECT jsonb_build_object(
                    'columns',(SELECT jsonb_agg(jsonb_build_array(attname,atttypid,attnotnull) ORDER BY attnum)
                        FROM pg_attribute WHERE attrelid='{schema}.current_entries'::regclass AND attnum>0 AND NOT attisdropped),
                    'constraints',(SELECT jsonb_agg(jsonb_build_array(conname,contype,conkey,confrelid) ORDER BY conname)
                        FROM pg_constraint WHERE conrelid='{schema}.current_entries'::regclass),
                    'current',(SELECT jsonb_agg(to_jsonb(c) ORDER BY path_id) FROM {schema}.current_entries c),
                    'versions',(SELECT string_agg(version_id || ':' || md5(manifest_json::text),',' ORDER BY version_id COLLATE "C") FROM {schema}.versions),
                    'generation',(SELECT generation FROM {schema}.history_state WHERE singleton),
                    'schema',(SELECT max(version) FROM {schema}.schema_version))::text
                """, connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        void Check(bool condition, string name) { if (condition) checks.Add(name); else failures.Add(name); }
        static RepositoryVersionSummary? Winner(IReadOnlyList<RepositoryVersionSummary> rows) => rows.Count == 1 ? rows[0] : null;
    }
}
