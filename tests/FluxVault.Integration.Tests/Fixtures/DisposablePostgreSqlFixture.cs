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
    { Host = "127.0.0.1", Port = cluster.Port, DatabaseName = Database, Username = "fv_test", ServiceName = "fixture-only" };
    internal PostgreSqlRepositoryMetadataStore Store => new(Configuration, "fixture-device");
    internal string[] HostArguments => ["--db-port", cluster.Port.ToString(), "--database", Database];
    private bool created;

    private DisposablePostgreSqlFixture(OwnedPostgreSqlCluster cluster) => this.cluster = cluster;

    internal static async Task<DisposablePostgreSqlFixture> CreateAsync()
    {
        var cluster = OwnedPostgreSqlCluster.Load();
        await cluster.VerifyAsync("postgres");
        var fixture = new DisposablePostgreSqlFixture(cluster);
        try
        {
            await using var connection = cluster.Connection("postgres");
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{fixture.Database}\"", connection);
            await command.ExecuteNonQueryAsync();
            fixture.created = true;
            command.CommandText = $"COMMENT ON DATABASE \"{fixture.Database}\" IS '{cluster.InstanceId}:{fixture.Database}'";
            await command.ExecuteNonQueryAsync();
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
        if (created)
        {
            var owner = OwnedPostgreSqlCluster.Load();
            if (owner != cluster) throw new InvalidOperationException("Test cluster owner changed before database cleanup.");
            await owner.VerifyAsync("postgres");
            await using var connection = owner.Connection("postgres");
            await connection.OpenAsync();
            await using var identity = new NpgsqlCommand("SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = @name", connection);
            identity.Parameters.AddWithValue("name", Database);
            if ((string?)await identity.ExecuteScalarAsync() != $"{owner.InstanceId}:{Database}")
                throw new InvalidOperationException("Database cleanup refused: the generated database ownership comment changed.");
            // Only this generated and identity-confirmed database; no normal endpoint is accepted.
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{Database}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
            created = false;
        }
        Files.Dispose();
    }
}
