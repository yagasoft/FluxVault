namespace FluxVault.Core.Storage.Metadata;

public static class PostgreSqlMetadataSchema
{
    public const int CurrentVersion = 1;

    public const string CreateSchemaSql = """
        CREATE SCHEMA IF NOT EXISTS fluxvault;

        CREATE TABLE IF NOT EXISTS fluxvault.schema_version (
            version integer PRIMARY KEY,
            applied_at_utc timestamptz NOT NULL DEFAULT now()
        );

        INSERT INTO fluxvault.schema_version (version)
        VALUES (1)
        ON CONFLICT (version) DO NOTHING;

        CREATE TABLE IF NOT EXISTS fluxvault.paths (
            path_id bigserial PRIMARY KEY,
            source_path text NOT NULL,
            entry_kind text NOT NULL,
            normalised_key text NOT NULL,
            created_at_utc timestamptz NOT NULL DEFAULT now()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_paths_normalised_kind
            ON fluxvault.paths (normalised_key, entry_kind);

        CREATE TABLE IF NOT EXISTS fluxvault.chunks (
            digest text PRIMARY KEY,
            stored_length integer NOT NULL,
            encoding text NOT NULL,
            first_seen_utc timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS fluxvault.versions (
            version_id text PRIMARY KEY,
            path_id bigint NOT NULL REFERENCES fluxvault.paths(path_id),
            source_path text NOT NULL,
            entry_kind text NOT NULL,
            watched_folder_id text NOT NULL,
            captured_at_utc timestamptz NOT NULL,
            consistency text NOT NULL,
            logical_length bigint NOT NULL,
            operation_type text NOT NULL,
            is_deleted boolean NOT NULL,
            content_signature text NULL,
            restored_from_version_id text NULL,
            fork_origin_version_id text NULL,
            inherited_from_version_id text NULL,
            inherited_from_source_path text NULL,
            deleted_from_version_id text NULL,
            source_last_write_utc timestamptz NULL,
            manifest_json jsonb NOT NULL,
            created_at_utc timestamptz NOT NULL DEFAULT now()
        );

        ALTER TABLE fluxvault.versions
            ADD COLUMN IF NOT EXISTS manifest_json jsonb NOT NULL DEFAULT '{}'::jsonb;

        ALTER TABLE fluxvault.versions
            ALTER COLUMN manifest_json DROP DEFAULT;

        CREATE INDEX IF NOT EXISTS ix_versions_source_path_captured
            ON fluxvault.versions (source_path, entry_kind, captured_at_utc DESC, version_id DESC);

        CREATE INDEX IF NOT EXISTS ix_versions_content_signature
            ON fluxvault.versions (content_signature)
            WHERE content_signature IS NOT NULL;

        CREATE TABLE IF NOT EXISTS fluxvault.version_chunks (
            version_id text NOT NULL REFERENCES fluxvault.versions(version_id) ON DELETE CASCADE,
            chunk_ordinal integer NOT NULL,
            digest text NOT NULL REFERENCES fluxvault.chunks(digest),
            logical_offset bigint NOT NULL,
            logical_length integer NOT NULL,
            stored_length integer NOT NULL,
            encoding text NOT NULL,
            PRIMARY KEY (version_id, chunk_ordinal)
        );

        CREATE INDEX IF NOT EXISTS ix_version_chunks_digest
            ON fluxvault.version_chunks (digest);

        CREATE TABLE IF NOT EXISTS fluxvault.lineage_edges (
            version_id text NOT NULL REFERENCES fluxvault.versions(version_id) ON DELETE CASCADE,
            parent_version_id text NOT NULL,
            PRIMARY KEY (version_id, parent_version_id)
        );

        CREATE TABLE IF NOT EXISTS fluxvault.folder_entries (
            version_id text NOT NULL REFERENCES fluxvault.versions(version_id) ON DELETE CASCADE,
            name text NOT NULL,
            source_path text NOT NULL,
            entry_kind text NOT NULL,
            child_version_id text NOT NULL,
            is_deleted boolean NOT NULL,
            logical_length bigint NOT NULL,
            captured_at_utc timestamptz NOT NULL,
            PRIMARY KEY (version_id, name, entry_kind)
        );

        CREATE TABLE IF NOT EXISTS fluxvault.current_entries (
            source_path text NOT NULL,
            entry_kind text NOT NULL,
            version_id text NOT NULL REFERENCES fluxvault.versions(version_id),
            captured_at_utc timestamptz NOT NULL,
            is_deleted boolean NOT NULL,
            PRIMARY KEY (source_path, entry_kind)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_current_entries_path_kind
            ON fluxvault.current_entries (source_path, entry_kind);

        CREATE TABLE IF NOT EXISTS fluxvault.mirror_nodes (
            node_id text PRIMARY KEY,
            label text NOT NULL,
            path text NOT NULL,
            is_enabled boolean NOT NULL,
            priority integer NOT NULL,
            capacity_budget_bytes bigint NULL
        );

        CREATE TABLE IF NOT EXISTS fluxvault.chunk_locations (
            digest text NOT NULL REFERENCES fluxvault.chunks(digest),
            node_id text NOT NULL REFERENCES fluxvault.mirror_nodes(node_id),
            stored_length integer NOT NULL,
            verified_at_utc timestamptz NULL,
            health text NOT NULL,
            PRIMARY KEY (digest, node_id)
        );

        CREATE TABLE IF NOT EXISTS fluxvault.retention_holds (
            hold_id text PRIMARY KEY,
            version_id text NOT NULL REFERENCES fluxvault.versions(version_id) ON DELETE CASCADE,
            reason text NOT NULL,
            expires_at_utc timestamptz NULL
        );

        CREATE TABLE IF NOT EXISTS fluxvault.devices (
            device_id text PRIMARY KEY,
            display_name text NOT NULL,
            trust_state text NOT NULL,
            created_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS fluxvault.peer_operations (
            device_id text NOT NULL,
            sequence_number bigint NOT NULL,
            operation_id text NOT NULL,
            version_id text NULL,
            source_path text NOT NULL,
            content_signature text NULL,
            operation_kind text NOT NULL,
            created_at_utc timestamptz NOT NULL,
            metadata_json jsonb NOT NULL DEFAULT '{}'::jsonb,
            PRIMARY KEY (device_id, sequence_number),
            UNIQUE (device_id, operation_id)
        );

        CREATE TABLE IF NOT EXISTS fluxvault.peer_cursors (
            peer_device_id text PRIMARY KEY,
            last_sequence_number bigint NOT NULL,
            updated_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS fluxvault.sync_mappings (
            mapping_id text PRIMARY KEY,
            source_device_id text NOT NULL,
            source_path text NOT NULL,
            local_path text NULL,
            state text NOT NULL,
            updated_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS fluxvault.sync_hydrations (
            hydration_id text PRIMARY KEY,
            source_device_id text NOT NULL,
            source_operation_id text NOT NULL,
            source_version_id text NOT NULL,
            local_path text NOT NULL,
            state text NOT NULL,
            message text NOT NULL,
            recorded_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS fluxvault.sync_conflicts (
            conflict_id text PRIMARY KEY,
            local_path text NOT NULL,
            source_device_id text NOT NULL,
            source_version_id text NOT NULL,
            source_operation_id text NOT NULL,
            status text NOT NULL,
            action text NULL,
            recorded_at_utc timestamptz NOT NULL,
            resolved_at_utc timestamptz NULL
        );

        CREATE TABLE IF NOT EXISTS fluxvault.capture_queue (
            queue_id bigserial PRIMARY KEY,
            source_path text NOT NULL,
            watched_folder_id text NOT NULL,
            reason text NOT NULL,
            priority integer NOT NULL,
            state text NOT NULL,
            queued_at_utc timestamptz NOT NULL DEFAULT now(),
            leased_until_utc timestamptz NULL
        );

        CREATE INDEX IF NOT EXISTS ix_capture_queue_ready
            ON fluxvault.capture_queue (state, priority DESC, queued_at_utc);

        CREATE TABLE IF NOT EXISTS fluxvault.metadata_outbox (
            outbox_id bigserial PRIMARY KEY,
            device_id text NOT NULL,
            operation_id text NOT NULL,
            version_id text NULL,
            payload_json jsonb NOT NULL,
            created_at_utc timestamptz NOT NULL DEFAULT now(),
            exported_at_utc timestamptz NULL,
            export_path text NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_metadata_outbox_device_operation
            ON fluxvault.metadata_outbox (device_id, operation_id);

        CREATE INDEX IF NOT EXISTS ix_metadata_outbox_export
            ON fluxvault.metadata_outbox (exported_at_utc, created_at_utc);
        """;
}
