using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Data;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using Npgsql;
using NpgsqlTypes;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Security;

namespace FluxVault.Core.Storage.Metadata;

public sealed class PostgreSqlRepositoryMetadataStore : IRepositoryMetadataStore, IAsyncDisposable
{
    // Legacy/direct fixtures retain their lock. Service-managed stores derive a stable key from their immutable vault ID.
    private const long MetadataMutationLockKey = 0x46564D455441;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly MetadataStoreConfiguration configuration;
    private readonly string deviceId;
    private readonly RepositoryIntegrityLimits integrityLimits;
    private readonly VaultBinding? binding;
    private readonly NpgsqlDataSource? windowsDataSource;
    private readonly long mutationLockKey = MetadataMutationLockKey;
    private string QuotedSchema => binding is null ? "fluxvault" : '"' + binding.MetadataNamespace + '"';
    public VaultBinding? Binding => binding;
    internal long MutationLockKey => mutationLockKey;
    private readonly SemaphoreSlim schemaGate = new(1, 1);
    private volatile bool schemaInitialized;
    private volatile string? lastError;

    public PostgreSqlRepositoryMetadataStore(MetadataStoreConfiguration configuration, string? deviceId = null, RepositoryIntegrityLimits? integrityLimits = null)
    {
        this.configuration = configuration.Normalise(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        this.deviceId = string.IsNullOrWhiteSpace(deviceId) ? Environment.MachineName : deviceId.Trim();
        this.integrityLimits = integrityLimits ?? new RepositoryIntegrityLimits();
        this.integrityLimits.Validate();
    }

    public PostgreSqlRepositoryMetadataStore(VaultBinding binding, string? deviceId = null, RepositoryIntegrityLimits? integrityLimits = null)
        : this(binding.MetadataStore, deviceId, integrityLimits)
    {
        binding.Validate();
        if (this.configuration != binding.MetadataStore) throw new ArgumentException("A normalised metadata binding is required.", nameof(binding));
        this.binding = binding;
        mutationLockKey = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(System.Security.Cryptography.SHA256.HashData(binding.Id.Value.ToByteArray()));
        windowsDataSource = PostgreSqlMetadataConnectionFactory.CreateWindowsDataSource(configuration);
    }

    public async Task ProvisionVaultAsync(CancellationToken cancellationToken = default)
    {
        if (binding is null) throw new InvalidOperationException("Explicit vault binding is required for provisioning.");
        await using var connection = await OpenConnectionAsync(cancellationToken, verifyBinding: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireMutationLockAsync(connection, transaction, cancellationToken);
        await using (var exists = CreateCommand("SELECT EXISTS (SELECT FROM pg_namespace WHERE nspname=@schema)", connection, transaction))
        {
            exists.Parameters.AddWithValue("schema", binding.MetadataNamespace);
            if (await exists.ExecuteScalarAsync(cancellationToken) is true)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Existing metadata namespaces are never adopted automatically.");
        }
        await using var create = CreateCommand(PostgreSqlMetadataSchema.ForVault(binding.Id) + $"""

            REVOKE ALL ON SCHEMA {QuotedSchema} FROM PUBLIC;
            CREATE TABLE {QuotedSchema}.vault_binding (
                singleton boolean PRIMARY KEY CHECK(singleton), vault_id uuid NOT NULL,
                primary_root text NOT NULL, endpoint_key text NOT NULL
            );
            INSERT INTO {QuotedSchema}.vault_binding VALUES(true,@id,@root,@endpoint);
            REVOKE ALL ON ALL TABLES IN SCHEMA {QuotedSchema} FROM PUBLIC;
            REVOKE ALL ON ALL SEQUENCES IN SCHEMA {QuotedSchema} FROM PUBLIC;
            ALTER DEFAULT PRIVILEGES IN SCHEMA {QuotedSchema} REVOKE ALL ON TABLES FROM PUBLIC;
            ALTER DEFAULT PRIVILEGES IN SCHEMA {QuotedSchema} REVOKE ALL ON SEQUENCES FROM PUBLIC;
            """, connection, transaction);
        create.Parameters.AddWithValue("id", binding.Id.Value);
        create.Parameters.AddWithValue("root", CanonicalRoot(binding.RepositoryPath));
        create.Parameters.AddWithValue("endpoint", EndpointKey());
        await create.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        schemaInitialized = true;
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

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (binding is null)
            {
                await using var command = CreateCommand(PostgreSqlMetadataSchema.CreateSchemaSql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
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

    public async Task VerifyBindingAsync(CancellationToken cancellationToken = default)
    {
        if (binding is null) throw new InvalidOperationException("Explicit vault binding is required.");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default)
    {
        return RecordVersionsAsync([manifest], cancellationToken);
    }

    public async Task<ChunkDescriptor?> FindChunkDescriptorAsync(string digest, CancellationToken cancellationToken = default)
    {
        VerifiedChunkReader.ValidateHex(digest, 64, "chunk digest");
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand("""
            SELECT DISTINCT logical_length, stored_length, encoding
            FROM fluxvault.version_chunks WHERE digest = @digest LIMIT 2;
            """, connection);
        command.Parameters.AddWithValue("digest", digest.ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (!Enum.TryParse<ChunkEncoding>(reader.GetString(2), out var encoding) || !Enum.IsDefined(encoding))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Acknowledged chunk codec is invalid.");
        var descriptor = new ChunkDescriptor(digest.ToLowerInvariant(), reader.GetInt32(0), reader.GetInt32(1), encoding);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Digest has conflicting acknowledged representations.");
        return descriptor;
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

        // Reject unsupported publication before opening a connection or starting the transaction.
        foreach (var manifest in manifests)
        {
            RequireManifestBinding(manifest);
            RepositoryManifestSize.Validate(manifest, integrityLimits);
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireMutationLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateAcknowledgedDescriptorsAsync(connection, transaction, manifests, cancellationToken).ConfigureAwait(false);
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
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"SELECT {BoundedManifestSql()} FROM fluxvault.versions WHERE version_id = @version_id;", connection);
        command.Parameters.AddWithValue("version_id", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new FileNotFoundException($"Manifest {versionId} was not found.", versionId);
        return ReadBoundedManifest(reader, versionId);
    }

    public async Task<IReadOnlyList<FileVersionManifest>> ListManifestsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"""
            SELECT {BoundedManifestSql()}, version_id
            FROM fluxvault.versions
            ORDER BY captured_at_utc DESC, version_id DESC;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var manifests = new List<FileVersionManifest>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            manifests.Add(ReadBoundedManifest(reader, reader.GetString(1)));
        }

        return manifests;
    }

    public async Task<FileVersionManifest?> FindLatestManifestAsync(
        string sourcePath,
        RepositoryEntryKind entryKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"""
            SELECT {BoundedManifestSql()}, version_id
            FROM fluxvault.versions
            WHERE source_path = @source_path AND entry_kind = @entry_kind
            ORDER BY captured_at_utc DESC, version_id DESC
            LIMIT 1;
            """,
            connection);
        command.Parameters.AddWithValue("source_path", Path.GetFullPath(sourcePath));
        command.Parameters.AddWithValue("entry_kind", entryKind.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadBoundedManifest(reader, reader.GetString(1))
            : null;
    }

    public async Task<FileVersionManifest?> FindLiveFileByContentSignatureAsync(
        string sourcePath,
        string contentSignature,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSignature);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"""
            SELECT {BoundedManifestSql()}, version_id
            FROM fluxvault.versions
            WHERE entry_kind = 'File'
              AND is_deleted = false
              AND source_path <> @source_path
              AND content_signature = @content_signature
            ORDER BY captured_at_utc ASC, version_id ASC
            LIMIT 1;
            """,
            connection);
        command.Parameters.AddWithValue("source_path", Path.GetFullPath(sourcePath));
        command.Parameters.AddWithValue("content_signature", contentSignature);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadBoundedManifest(reader, reader.GetString(1))
            : null;
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var manifests = await ListManifestsAsync(cancellationToken).ConfigureAwait(false);
        return RepositoryMetadataStoreHelpers.ToVersionSummaries(manifests);
    }

    public async Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"""
            SELECT {BoundedManifestSql("v.")}, v.version_id
            FROM fluxvault.current_entries c
            JOIN fluxvault.versions v ON v.version_id = c.version_id
            ORDER BY c.source_path ASC, c.entry_kind DESC;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var manifests = new List<FileVersionManifest>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            manifests.Add(ReadBoundedManifest(reader, reader.GetString(1)));
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
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireMutationLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var affectedDigests = new List<string>();
            await using (var affectedCommand = CreateCommand(
                "SELECT DISTINCT digest FROM fluxvault.version_chunks WHERE version_id = ANY(@version_ids);", connection, transaction))
            {
                affectedCommand.Parameters.AddWithValue("version_ids", ids);
                await using var reader = await affectedCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) affectedDigests.Add(reader.GetString(0));
            }
            await using (var currentCommand = CreateCommand(
                "DELETE FROM fluxvault.current_entries WHERE version_id = ANY(@version_ids);",
                connection,
                transaction))
            {
                currentCommand.Parameters.AddWithValue("version_ids", ids);
                await currentCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var versionCommand = CreateCommand(
                "DELETE FROM fluxvault.versions WHERE version_id = ANY(@version_ids);",
                connection,
                transaction))
            {
                versionCommand.Parameters.AddWithValue("version_ids", ids);
                await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Retire only this deletion's now-unreferenced catalogue rows. Location rows
            // have a non-cascading FK, and shared objects must keep their locations.
            foreach (var table in new[] { "chunk_locations", "chunks" })
            {
                await using var retireCommand = CreateCommand($"""
                    DELETE FROM fluxvault.{table} AS retired
                    WHERE retired.digest = ANY(@digests)
                      AND NOT EXISTS (SELECT 1 FROM fluxvault.version_chunks AS referenced WHERE referenced.digest = retired.digest);
                    """, connection, transaction);
                retireCommand.Parameters.AddWithValue("digests", affectedDigests.ToArray());
                await retireCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            """
            SELECT digest, count(*)::bigint
            FROM fluxvault.version_chunks
            WHERE digest = ANY(@digests)
            GROUP BY digest;
            """,
            connection);
        command.Parameters.AddWithValue("digests", requested.Select(digest => digest.ToLowerInvariant()).ToArray());
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
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(
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
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            "SELECT max(version) FROM fluxvault.schema_version;",
            connection);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null || value is DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<int> ExportOutboxAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        RequireExportRoot(repositoryPath);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = CreateCommand(
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

        return await ExportOutboxRowsAsync(repositoryPath, connection, rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExportOutboxAsync(
        string repositoryPath,
        IReadOnlyCollection<string> versionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        RequireExportRoot(repositoryPath);
        ArgumentNullException.ThrowIfNull(versionIds);
        var ids = versionIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = CreateCommand(
            """
            SELECT outbox_id, device_id, payload_json::text
            FROM fluxvault.metadata_outbox
            WHERE exported_at_utc IS NULL
              AND version_id = ANY(@version_ids)
            ORDER BY outbox_id;
            """,
            connection);
        select.Parameters.AddWithValue("version_ids", ids);
        var rows = new List<OutboxRow>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new OutboxRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return await ExportOutboxRowsAsync(repositoryPath, connection, rows, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExportOutboxRowsAsync(
        string repositoryPath,
        NpgsqlConnection connection,
        IReadOnlyList<OutboxRow> rows,
        CancellationToken cancellationToken)
    {
        var exported = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exportPath = await WriteOutboxFileAsync(repositoryPath, row, cancellationToken).ConfigureAwait(false);
            await using var update = CreateCommand(
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

    private async Task<long> UpsertPathAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataPathRow row,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
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

    private async Task UpsertVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataVersionRow row,
        long pathId,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
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
                manifest_json = EXCLUDED.manifest_json
            WHERE fluxvault.versions.manifest_json = EXCLUDED.manifest_json
            RETURNING 1;
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
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest, "An acknowledged version identity cannot be rewritten.");
    }

    private async Task AcquireMutationLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // A separate READ COMMITTED statement after this wait sees the preceding
        // writer's committed references. No filesystem lease is acquired here.
        await using var command = CreateCommand("SELECT pg_advisory_xact_lock(@key);", connection, transaction);
        command.Parameters.AddWithValue("key", mutationLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateAcknowledgedDescriptorsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyCollection<FileVersionManifest> manifests, CancellationToken cancellationToken)
    {
        var descriptors = new Dictionary<string, ChunkDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in manifests.SelectMany(manifest => manifest.Chunks))
        {
            var descriptor = ChunkDescriptor.FromChunk(chunk);
            if (descriptors.TryGetValue(descriptor.Digest, out var existing) && existing != descriptor)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Incoming versions disagree about a chunk representation.");
            descriptors[descriptor.Digest] = descriptor;
        }
        if (descriptors.Count == 0) return;
        var unique = descriptors.Values.ToArray();
        await using var command = CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM fluxvault.version_chunks AS acknowledged
                JOIN unnest(@digests, @logical_lengths, @stored_lengths, @encodings)
                    AS incoming(digest, logical_length, stored_length, encoding) ON acknowledged.digest = incoming.digest
                WHERE acknowledged.logical_length <> incoming.logical_length
                   OR acknowledged.stored_length <> incoming.stored_length OR acknowledged.encoding <> incoming.encoding);
            """, connection, transaction);
        command.Parameters.AddWithValue("digests", unique.Select(descriptor => descriptor.Digest).ToArray());
        command.Parameters.AddWithValue("logical_lengths", unique.Select(descriptor => descriptor.LogicalLength).ToArray());
        command.Parameters.AddWithValue("stored_lengths", unique.Select(descriptor => descriptor.StoredLength).ToArray());
        command.Parameters.AddWithValue("encodings", unique.Select(descriptor => descriptor.Encoding.ToString()).ToArray());
        if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Incoming chunk representation conflicts with an acknowledged version.");
    }

    private async Task DeleteVersionChildrenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string versionId,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "version_chunks", "lineage_edges", "folder_entries" })
        {
            await using var command = CreateCommand(
                $"DELETE FROM fluxvault.{table} WHERE version_id = @version_id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("version_id", versionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataChunkRow row,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO fluxvault.chunks (digest, stored_length, encoding)
            VALUES (@digest, @stored_length, @encoding)
            ON CONFLICT (digest)
            DO UPDATE SET
                stored_length = fluxvault.chunks.stored_length
            WHERE fluxvault.chunks.stored_length = EXCLUDED.stored_length
                AND fluxvault.chunks.encoding = EXCLUDED.encoding
            RETURNING 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("digest", row.Digest);
        command.Parameters.AddWithValue("stored_length", row.StoredLength);
        command.Parameters.AddWithValue("encoding", row.Encoding.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Chunk catalogue representation is immutable.");
    }

    private async Task InsertVersionChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataVersionChunkRow row,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO fluxvault.version_chunks (
                version_id, chunk_ordinal, digest, logical_offset, logical_length, stored_length, encoding)
            SELECT @version_id, @chunk_ordinal, @digest, @logical_offset, @logical_length, @stored_length, @encoding
            WHERE NOT EXISTS (
                SELECT 1 FROM fluxvault.version_chunks
                WHERE digest = @digest AND (logical_length <> @logical_length OR stored_length <> @stored_length OR encoding <> @encoding))
            ON CONFLICT (version_id, chunk_ordinal) DO NOTHING
            RETURNING 1;
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
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.DescriptorConflict, "Acknowledged chunk descriptor is immutable.");
    }

    private async Task InsertLineageEdgeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataLineageEdgeRow row,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
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

    private async Task InsertFolderEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataFolderEntryRow row,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
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

    private async Task UpsertCurrentEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataVersionRow row,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
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
        await using var command = CreateCommand(
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

    private string BoundedManifestSql(string prefix = "") =>
        $"CASE WHEN octet_length({prefix}manifest_json::text) <= {integrityLimits.MaxManifestBytes} THEN {prefix}manifest_json::text ELSE NULL END";

    private FileVersionManifest ReadBoundedManifest(NpgsqlDataReader reader, string versionId)
    {
        if (reader.IsDBNull(0))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Stored manifest exceeds its supported size limit.");
        var manifest = DeserializeManifest(reader.GetString(0), versionId);
        RequireManifestBinding(manifest);
        return manifest;
    }

    internal static FileVersionManifest DeserializeManifest(string json, string versionId)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<FileVersionManifest>(json, JsonOptions)
                ?? throw new InvalidDataException($"Manifest {versionId} could not be deserialized.");
            VerifiedChunkReader.ValidateHex(versionId, 32, "version id");
            VerifiedChunkReader.ValidateHex(manifest.VersionId, 32, "manifest version id");
            if (!string.Equals(manifest.VersionId, versionId, StringComparison.OrdinalIgnoreCase))
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.InvalidManifest,
                    "Stored manifest identity disagrees with its metadata row.");
            return manifest;
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

    private NpgsqlCommand CreateCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction = null) =>
        new(sql.Replace("fluxvault.", QuotedSchema + ".", StringComparison.Ordinal), connection, transaction);

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken, bool verifyBinding = true)
    {
        var connection = windowsDataSource?.CreateConnection() ?? PostgreSqlMetadataConnectionFactory.CreateConnection(configuration);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (binding is not null)
            {
                await using (var endpoint = new NpgsqlCommand("SELECT current_database(), current_user, session_user, current_setting('fsync'), current_setting('full_page_writes'), current_setting('synchronous_commit')", connection))
                await using (var reader = await endpoint.ExecuteReaderAsync(cancellationToken))
                    if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != configuration.DatabaseName ||
                        reader.GetString(1) != configuration.Username || reader.GetString(2) != configuration.Username ||
                        reader.GetString(3) != "on" || reader.GetString(4) != "on" || reader.GetString(5) != "on")
                        throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Metadata endpoint or durability settings do not match the vault binding.");
                if (verifyBinding) await VerifyBindingAsync(connection, cancellationToken);
            }
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task VerifyBindingAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var schema = new NpgsqlCommand("""
            SELECT r.rolname, NOT EXISTS (SELECT FROM aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) a WHERE a.grantee<>n.nspowner)
            FROM pg_namespace n JOIN pg_roles r ON r.oid=n.nspowner WHERE n.nspname=@schema
            """, connection))
        {
            schema.Parameters.AddWithValue("schema", binding!.MetadataNamespace);
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipUnknown, "The bound metadata namespace is missing; no automatic provisioning occurred.");
            if (reader.GetString(0) != configuration.Username || !reader.GetBoolean(1))
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "The bound metadata namespace has unexpected ownership or access.");
        }
        try
        {
            await using var command = new NpgsqlCommand($"""
                SELECT vault_id, primary_root, endpoint_key, (SELECT max(version) FROM {QuotedSchema}.schema_version)
                FROM {QuotedSchema}.vault_binding WHERE singleton=true
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != binding!.Id.Value ||
                reader.GetString(1) != CanonicalRoot(binding.RepositoryPath) || reader.GetString(2) != EndpointKey() ||
                reader.IsDBNull(3) || reader.GetInt32(3) != PostgreSqlMetadataSchema.CurrentVersion)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Metadata identity, root, endpoint or schema version does not match the vault binding.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipUnknown, "The metadata namespace has no valid vault binding; existing data was preserved.");
        }
    }

    private string EndpointKey() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { configuration.Host, configuration.Port, configuration.DatabaseName, configuration.Username }))));
    private static string CanonicalRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    private void RequireManifestBinding(FileVersionManifest manifest)
    {
        if (binding is not null && manifest.VaultId != binding.Id)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Manifest identity does not match the bound vault.");
    }
    private void RequireExportRoot(string repositoryPath)
    {
        if (binding is not null && CanonicalRoot(repositoryPath) != CanonicalRoot(binding.RepositoryPath))
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Metadata export destination does not match the bound vault root.");
    }

    public async ValueTask DisposeAsync()
    {
        if (windowsDataSource is not null) await windowsDataSource.DisposeAsync();
        schemaGate.Dispose();
    }

    private sealed record OutboxRow(long OutboxId, string DeviceId, string PayloadJson);
}
