using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using Npgsql;
using NpgsqlTypes;

namespace FluxVault.Core.Storage.Metadata;

public sealed class PostgreSqlRepositoryMetadataStore : IRepositoryMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly MetadataStoreConfiguration configuration;
    private readonly string deviceId;
    private readonly SemaphoreSlim schemaGate = new(1, 1);
    private volatile bool schemaInitialized;
    private volatile string? lastError;

    public PostgreSqlRepositoryMetadataStore(MetadataStoreConfiguration configuration, string? deviceId = null)
    {
        this.configuration = configuration.Normalise(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        this.deviceId = string.IsNullOrWhiteSpace(deviceId) ? Environment.MachineName : deviceId.Trim();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (schemaInitialized)
        {
            return;
        }

        await schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (schemaInitialized)
            {
                return;
            }

            await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(PostgreSqlMetadataSchema.CreateSchemaSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            schemaInitialized = true;
            lastError = null;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or IOException or InvalidOperationException)
        {
            lastError = exception.Message;
            throw;
        }
        finally
        {
            schemaGate.Release();
        }
    }

    public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default)
    {
        return RecordVersionsAsync([manifest], cancellationToken);
    }

    public async Task RecordVersionsAsync(
        IReadOnlyCollection<FileVersionManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        if (manifests.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RecordManifestAsync(connection, transaction, manifest, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lastError = null;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or IOException or InvalidOperationException)
        {
            lastError = exception.Message;
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<FileVersionManifest> ReadManifestAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT manifest_json::text FROM fluxvault.versions WHERE version_id = @version_id;",
            connection);
        command.Parameters.AddWithValue("version_id", versionId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            throw new FileNotFoundException($"Manifest {versionId} was not found.", versionId);
        }

        return DeserializeManifest(json, versionId);
    }

    public async Task<IReadOnlyList<FileVersionManifest>> ListManifestsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT manifest_json::text, version_id
            FROM fluxvault.versions
            ORDER BY captured_at_utc DESC, version_id DESC;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var manifests = new List<FileVersionManifest>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            manifests.Add(DeserializeManifest(reader.GetString(0), reader.GetString(1)));
        }

        return manifests;
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var manifests = await ListManifestsAsync(cancellationToken).ConfigureAwait(false);
        return RepositoryMetadataStoreHelpers.ToVersionSummaries(manifests);
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT v.manifest_json::text, v.version_id
            FROM fluxvault.current_entries c
            JOIN fluxvault.versions v ON v.version_id = c.version_id
            ORDER BY c.source_path ASC, c.entry_kind DESC;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var manifests = new List<FileVersionManifest>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            manifests.Add(DeserializeManifest(reader.GetString(0), reader.GetString(1)));
        }

        return manifests.Select(RepositoryMetadataStoreHelpers.ToSummary).ToArray();
    }

    public async Task DeleteVersionsAsync(IReadOnlyCollection<string> versionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionIds);
        var ids = versionIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var currentCommand = new NpgsqlCommand(
                "DELETE FROM fluxvault.current_entries WHERE version_id = ANY(@version_ids);",
                connection,
                transaction))
            {
                currentCommand.Parameters.AddWithValue("version_ids", ids);
                await currentCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var versionCommand = new NpgsqlCommand(
                "DELETE FROM fluxvault.versions WHERE version_id = ANY(@version_ids);",
                connection,
                transaction))
            {
                versionCommand.Parameters.AddWithValue("version_ids", ids);
                await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lastError = null;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            lastError = exception.Message;
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> CountChunkReferencesAsync(
        IReadOnlyCollection<string> digests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        var requested = digests.Where(digest => !string.IsNullOrWhiteSpace(digest)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var counts = requested.ToDictionary(digest => digest, _ => 0L, StringComparer.OrdinalIgnoreCase);
        if (requested.Length == 0)
        {
            return counts;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT digest, count(*)::bigint
            FROM fluxvault.version_chunks
            WHERE digest = ANY(@digests)
            GROUP BY digest;
            """,
            connection);
        command.Parameters.AddWithValue("digests", requested);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    public async Task<MetadataStoreRuntimeStatus> GetRuntimeStatusAsync(
        TimeSpan exportLagWarningThreshold,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)::integer, min(created_at_utc)
                FROM fluxvault.metadata_outbox
                WHERE exported_at_utc IS NULL;
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var pending = 0;
            DateTimeOffset? oldest = null;
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pending = reader.GetInt32(0);
                oldest = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            }

            TimeSpan? age = oldest is null ? null : DateTimeOffset.UtcNow - oldest.Value;
            lastError = null;
            return new MetadataStoreRuntimeStatus(
                Provider: configuration.Provider,
                Endpoint: BuildEndpoint(configuration),
                SchemaInitialized: schemaInitialized,
                LastError: null,
                PendingOutboxCount: pending,
                OldestUnexportedUtc: oldest,
                OldestUnexportedAge: age,
                IsExportLagExceeded: age > exportLagWarningThreshold);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or IOException or InvalidOperationException)
        {
            lastError = exception.Message;
            return new MetadataStoreRuntimeStatus(
                Provider: configuration.Provider,
                Endpoint: BuildEndpoint(configuration),
                SchemaInitialized: false,
                LastError: exception.Message,
                PendingOutboxCount: 0,
                OldestUnexportedUtc: null,
                OldestUnexportedAge: null,
                IsExportLagExceeded: false);
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT max(version) FROM fluxvault.schema_version;",
            connection);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null || value is DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<int> ExportOutboxAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var select = new NpgsqlCommand(
            """
            SELECT outbox_id, device_id, payload_json::text
            FROM fluxvault.metadata_outbox
            WHERE exported_at_utc IS NULL
            ORDER BY outbox_id
            LIMIT 1000;
            """,
            connection);
        var rows = new List<OutboxRow>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new OutboxRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var exported = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exportPath = await WriteOutboxFileAsync(repositoryPath, row, cancellationToken).ConfigureAwait(false);
            await using var update = new NpgsqlCommand(
                """
                UPDATE fluxvault.metadata_outbox
                SET exported_at_utc = now(), export_path = @export_path
                WHERE outbox_id = @outbox_id AND exported_at_utc IS NULL;
                """,
                connection);
            update.Parameters.AddWithValue("outbox_id", row.OutboxId);
            update.Parameters.AddWithValue("export_path", exportPath);
            exported += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return exported;
    }

    private async Task RecordManifestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FileVersionManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var projection = MetadataManifestProjection.FromManifest(manifest);
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        var pathId = await UpsertPathAsync(connection, transaction, projection.Path, cancellationToken).ConfigureAwait(false);

        await UpsertVersionAsync(connection, transaction, projection.Version, pathId, manifestJson, cancellationToken).ConfigureAwait(false);
        await DeleteVersionChildrenAsync(connection, transaction, manifest.VersionId, cancellationToken).ConfigureAwait(false);
        foreach (var chunk in projection.Chunks)
        {
            await UpsertChunkAsync(connection, transaction, chunk, cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in projection.VersionChunks)
        {
            await InsertVersionChunkAsync(connection, transaction, chunk, cancellationToken).ConfigureAwait(false);
        }

        foreach (var edge in projection.LineageEdges)
        {
            await InsertLineageEdgeAsync(connection, transaction, edge, cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in projection.FolderEntries)
        {
            await InsertFolderEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        }

        await UpsertCurrentEntryAsync(connection, transaction, projection.Version, cancellationToken).ConfigureAwait(false);
        await InsertOutboxRowAsync(connection, transaction, manifest, manifestJson, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> UpsertPathAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataPathRow row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.paths (source_path, entry_kind, normalised_key)
            VALUES (@source_path, @entry_kind, @normalised_key)
            ON CONFLICT (normalised_key, entry_kind)
            DO UPDATE SET source_path = EXCLUDED.source_path
            RETURNING path_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("source_path", row.SourcePath);
        command.Parameters.AddWithValue("entry_kind", row.EntryKind.ToString());
        command.Parameters.AddWithValue("normalised_key", RepositoryMetadataStoreHelpers.ToEntryKey(row.SourcePath, row.EntryKind));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL did not return a path id."));
    }

    private static async Task UpsertVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataVersionRow row,
        long pathId,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.versions (
                version_id, path_id, source_path, entry_kind, watched_folder_id, captured_at_utc,
                consistency, logical_length, operation_type, is_deleted, content_signature,
                restored_from_version_id, fork_origin_version_id, inherited_from_version_id,
                inherited_from_source_path, deleted_from_version_id, source_last_write_utc,
                manifest_json)
            VALUES (
                @version_id, @path_id, @source_path, @entry_kind, @watched_folder_id, @captured_at_utc,
                @consistency, @logical_length, @operation_type, @is_deleted, @content_signature,
                @restored_from_version_id, @fork_origin_version_id, @inherited_from_version_id,
                @inherited_from_source_path, @deleted_from_version_id, @source_last_write_utc,
                @manifest_json)
            ON CONFLICT (version_id)
            DO UPDATE SET
                path_id = EXCLUDED.path_id,
                source_path = EXCLUDED.source_path,
                entry_kind = EXCLUDED.entry_kind,
                watched_folder_id = EXCLUDED.watched_folder_id,
                captured_at_utc = EXCLUDED.captured_at_utc,
                consistency = EXCLUDED.consistency,
                logical_length = EXCLUDED.logical_length,
                operation_type = EXCLUDED.operation_type,
                is_deleted = EXCLUDED.is_deleted,
                content_signature = EXCLUDED.content_signature,
                restored_from_version_id = EXCLUDED.restored_from_version_id,
                fork_origin_version_id = EXCLUDED.fork_origin_version_id,
                inherited_from_version_id = EXCLUDED.inherited_from_version_id,
                inherited_from_source_path = EXCLUDED.inherited_from_source_path,
                deleted_from_version_id = EXCLUDED.deleted_from_version_id,
                source_last_write_utc = EXCLUDED.source_last_write_utc,
                manifest_json = EXCLUDED.manifest_json;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("version_id", row.VersionId);
        command.Parameters.AddWithValue("path_id", pathId);
        command.Parameters.AddWithValue("source_path", row.SourcePath);
        command.Parameters.AddWithValue("entry_kind", row.EntryKind.ToString());
        command.Parameters.AddWithValue("watched_folder_id", row.WatchedFolderId);
        command.Parameters.AddWithValue("captured_at_utc", row.CapturedAtUtc);
        command.Parameters.AddWithValue("consistency", row.Consistency.ToString());
        command.Parameters.AddWithValue("logical_length", row.LogicalLength);
        command.Parameters.AddWithValue("operation_type", row.OperationType.ToString());
        command.Parameters.AddWithValue("is_deleted", row.IsDeleted);
        AddNullable(command, "content_signature", row.ContentSignature);
        AddNullable(command, "restored_from_version_id", row.RestoredFromVersionId);
        AddNullable(command, "fork_origin_version_id", row.ForkOriginVersionId);
        AddNullable(command, "inherited_from_version_id", row.InheritedFromVersionId);
        AddNullable(command, "inherited_from_source_path", row.InheritedFromSourcePath);
        AddNullable(command, "deleted_from_version_id", row.DeletedFromVersionId);
        AddNullable(command, "source_last_write_utc", row.SourceLastWriteUtc);
        command.Parameters.Add("manifest_json", NpgsqlDbType.Jsonb).Value = manifestJson;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteVersionChildrenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string versionId,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "version_chunks", "lineage_edges", "folder_entries" })
        {
            await using var command = new NpgsqlCommand(
                $"DELETE FROM fluxvault.{table} WHERE version_id = @version_id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("version_id", versionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataChunkRow row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.chunks (digest, stored_length, encoding)
            VALUES (@digest, @stored_length, @encoding)
            ON CONFLICT (digest)
            DO UPDATE SET
                stored_length = GREATEST(fluxvault.chunks.stored_length, EXCLUDED.stored_length),
                encoding = EXCLUDED.encoding;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("digest", row.Digest);
        command.Parameters.AddWithValue("stored_length", row.StoredLength);
        command.Parameters.AddWithValue("encoding", row.Encoding.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertVersionChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataVersionChunkRow row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.version_chunks (
                version_id, chunk_ordinal, digest, logical_offset, logical_length, stored_length, encoding)
            VALUES (
                @version_id, @chunk_ordinal, @digest, @logical_offset, @logical_length, @stored_length, @encoding)
            ON CONFLICT (version_id, chunk_ordinal) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("version_id", row.VersionId);
        command.Parameters.AddWithValue("chunk_ordinal", row.ChunkOrdinal);
        command.Parameters.AddWithValue("digest", row.Digest);
        command.Parameters.AddWithValue("logical_offset", row.Offset);
        command.Parameters.AddWithValue("logical_length", row.Length);
        command.Parameters.AddWithValue("stored_length", row.StoredLength);
        command.Parameters.AddWithValue("encoding", row.Encoding.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertLineageEdgeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataLineageEdgeRow row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.lineage_edges (version_id, parent_version_id)
            VALUES (@version_id, @parent_version_id)
            ON CONFLICT (version_id, parent_version_id) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("version_id", row.VersionId);
        command.Parameters.AddWithValue("parent_version_id", row.ParentVersionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFolderEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataFolderEntryRow row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.folder_entries (
                version_id, name, source_path, entry_kind, child_version_id, is_deleted, logical_length, captured_at_utc)
            VALUES (
                @version_id, @name, @source_path, @entry_kind, @child_version_id, @is_deleted, @logical_length, @captured_at_utc)
            ON CONFLICT (version_id, name, entry_kind)
            DO UPDATE SET
                source_path = EXCLUDED.source_path,
                child_version_id = EXCLUDED.child_version_id,
                is_deleted = EXCLUDED.is_deleted,
                logical_length = EXCLUDED.logical_length,
                captured_at_utc = EXCLUDED.captured_at_utc;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("version_id", row.VersionId);
        command.Parameters.AddWithValue("name", row.Name);
        command.Parameters.AddWithValue("source_path", row.SourcePath);
        command.Parameters.AddWithValue("entry_kind", row.EntryKind.ToString());
        command.Parameters.AddWithValue("child_version_id", row.ChildVersionId);
        command.Parameters.AddWithValue("is_deleted", row.IsDeleted);
        command.Parameters.AddWithValue("logical_length", row.LogicalLength);
        command.Parameters.AddWithValue("captured_at_utc", row.CapturedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertCurrentEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataVersionRow row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.current_entries (source_path, entry_kind, version_id, captured_at_utc, is_deleted)
            VALUES (@source_path, @entry_kind, @version_id, @captured_at_utc, @is_deleted)
            ON CONFLICT (source_path, entry_kind)
            DO UPDATE SET
                version_id = EXCLUDED.version_id,
                captured_at_utc = EXCLUDED.captured_at_utc,
                is_deleted = EXCLUDED.is_deleted
            WHERE fluxvault.current_entries.captured_at_utc < EXCLUDED.captured_at_utc
               OR (
                    fluxvault.current_entries.captured_at_utc = EXCLUDED.captured_at_utc
                    AND fluxvault.current_entries.version_id < EXCLUDED.version_id
               );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("source_path", row.SourcePath);
        command.Parameters.AddWithValue("entry_kind", row.EntryKind.ToString());
        command.Parameters.AddWithValue("version_id", row.VersionId);
        command.Parameters.AddWithValue("captured_at_utc", row.CapturedAtUtc);
        command.Parameters.AddWithValue("is_deleted", row.IsDeleted);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertOutboxRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FileVersionManifest manifest,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fluxvault.metadata_outbox (device_id, operation_id, version_id, payload_json)
            VALUES (@device_id, @operation_id, @version_id, @payload_json)
            ON CONFLICT (device_id, operation_id) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("device_id", deviceId);
        command.Parameters.AddWithValue("operation_id", manifest.VersionId);
        command.Parameters.AddWithValue("version_id", manifest.VersionId);
        command.Parameters.Add("payload_json", NpgsqlDbType.Jsonb).Value = manifestJson;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddNullable(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static void AddNullable(NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value is null ? DBNull.Value : value.Value;
    }

    private static FileVersionManifest DeserializeManifest(string json, string versionId)
    {
        try
        {
            return JsonSerializer.Deserialize<FileVersionManifest>(json, JsonOptions)
                ?? throw new InvalidDataException($"Manifest {versionId} could not be deserialized.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Manifest {versionId} could not be deserialized.", exception);
        }
    }

    private static string BuildEndpoint(MetadataStoreConfiguration configuration)
    {
        return $"{configuration.Host}:{configuration.Port}/{configuration.DatabaseName}";
    }

    private static async Task<string> WriteOutboxFileAsync(
        string repositoryPath,
        OutboxRow row,
        CancellationToken cancellationToken)
    {
        var devicePath = Path.Combine(repositoryPath, "metadata-journal", SanitizePathSegment(row.DeviceId));
        Directory.CreateDirectory(devicePath);
        var exportPath = Path.Combine(devicePath, $"{row.OutboxId:D20}.fvop");
        if (File.Exists(exportPath))
        {
            return exportPath;
        }

        var temporaryPath = exportPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, row.PayloadJson, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, exportPath);
            return exportPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "device" : sanitized;
    }

    private sealed record OutboxRow(long OutboxId, string DeviceId, string PayloadJson);
}
