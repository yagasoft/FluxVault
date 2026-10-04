using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Testing;
using Npgsql;

namespace FluxVault.Integration.Tests.Fixtures;

internal sealed class DisposablePostgreSqlFixture : IAsyncDisposable
{
    private readonly OwnedPostgreSqlCluster cluster;
    internal RepositoryProcessFixture Files { get; } = new();
    internal string Database { get; } = "fv_test_" + Guid.NewGuid().ToString("N");
    internal MetadataStoreConfiguration Configuration => MetadataStoreConfiguration.CreateDefault(Files.Root) with
    { Host = "127.0.0.1", Port = cluster.Port, DatabaseName = Database, Username = OwnedPostgreSqlCluster.ServiceRole, ServiceName = "fixture-only" };
    internal PostgreSqlRepositoryMetadataStore Store => new(Configuration, "fixture-device");
    internal string[] HostArguments => ["--db-port", cluster.Port.ToString(), "--database", Database];
    private bool created;

    private DisposablePostgreSqlFixture(OwnedPostgreSqlCluster cluster) => this.cluster = cluster;

    internal static async Task<DisposablePostgreSqlFixture> CreateAsync()
    {
        var cluster = OwnedPostgreSqlCluster.Load();
        await cluster.VerifyAsync(OwnedPostgreSqlCluster.MaintenanceDatabase);
        var fixture = new DisposablePostgreSqlFixture(cluster);
        try
        {
            OwnedPostgreSqlCluster.ValidateGeneratedDatabase(fixture.Database);
            fixture.RecordIntent("Intent"); // Durable intent precedes CREATE, including an interrupted pre-comment window.
            await using var connection = cluster.Connection(OwnedPostgreSqlCluster.MaintenanceDatabase);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{fixture.Database}\"", connection);
            await command.ExecuteNonQueryAsync();
            fixture.created = true;
            command.CommandText = $"REVOKE ALL ON DATABASE \"{fixture.Database}\" FROM PUBLIC; COMMENT ON DATABASE \"{fixture.Database}\" IS '{cluster.InstanceId}:{fixture.Database}'";
            await command.ExecuteNonQueryAsync();
            fixture.RecordIntent("Created");
            await cluster.VerifyAsync(fixture.Database);
            await fixture.Store.InitializeAsync();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal NpgsqlConnection Connection() => cluster.Connection(Database);
    internal async Task<long> ScalarAsync(string sql)
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    internal async Task ExecuteAsync(string sql)
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
        if (created)
        {
            var owner = OwnedPostgreSqlCluster.Load();
            if (owner != cluster) throw new InvalidOperationException("Test cluster owner changed before database cleanup.");
            OwnedPostgreSqlCluster.ValidateGeneratedDatabase(Database);
            await owner.VerifyAsync(OwnedPostgreSqlCluster.MaintenanceDatabase);
            await using var connection = owner.Connection(OwnedPostgreSqlCluster.MaintenanceDatabase);
            await connection.OpenAsync();
            await using var identity = new NpgsqlCommand("SELECT shobj_description(d.oid, 'pg_database'), r.rolname FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba WHERE d.datname = @name", connection);
            identity.Parameters.AddWithValue("name", Database);
            await using (var reader = await identity.ExecuteReaderAsync())
                if (!await reader.ReadAsync() || reader.IsDBNull(0) || reader.GetString(0) != $"{owner.InstanceId}:{Database}" || reader.GetString(1) != OwnedPostgreSqlCluster.ServiceRole)
                    throw new InvalidOperationException("Database cleanup refused: generated database owner/comment changed. Verified whole-cluster teardown is required.");
            // Only this generated and identity-confirmed database; no normal endpoint is accepted.
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{Database}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
            created = false;
            RecordIntent("Removed");
        }
        }
        finally { Files.Dispose(); }
    }

    private void RecordIntent(string state)
    {
        var directory = Path.Combine(cluster.Root, "integrity", "database-intents");
        WindowsDatabaseProbeConfiguration.RejectReparseComponents(directory);
        if (!Directory.Exists(directory)) throw new InvalidOperationException("The protected database intent directory is missing.");
        var path = Path.Combine(directory, Database + ".json");
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { cluster.InstanceId, Database, Owner = OwnedPostgreSqlCluster.ServiceRole, State = state });
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite: state != "Intent");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
