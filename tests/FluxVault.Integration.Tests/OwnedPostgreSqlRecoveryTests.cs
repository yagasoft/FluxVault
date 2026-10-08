using FluxVault.Testing;
using FluxVault.Integration.Tests.Fixtures;
using Npgsql;
using System.Text.Json;

namespace FluxVault.Integration.Tests;

[Trait("Category", "RequiresPostgreSql")]
public sealed class OwnedPostgreSqlRecoveryTests
{
    [Fact]
    public async Task Generated_database_has_a_durable_owned_intent_and_no_public_connection_grant()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var cluster = OwnedPostgreSqlCluster.Load();
        using var intent = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cluster.Root, "integrity", "database-intents", fixture.Database + ".json")));
        Assert.Equal(cluster.InstanceId, intent.RootElement.GetProperty("InstanceId").GetString());
        Assert.Equal(fixture.Database, intent.RootElement.GetProperty("Database").GetString());
        Assert.Equal(OwnedPostgreSqlCluster.ServiceRole, intent.RootElement.GetProperty("Owner").GetString());
        Assert.Equal("Created", intent.RootElement.GetProperty("State").GetString());
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM pg_database d, LATERAL aclexplode(d.datacl) a WHERE d.datname=current_database() AND a.grantee=0"));
    }

    [Fact]
    public async Task Cleanup_refuses_a_changed_comment_and_preserves_the_database_until_identity_is_restored()
    {
        var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        try
        {
            await fixture.ExecuteAsync($"COMMENT ON DATABASE \"{fixture.Database}\" IS 'not-this-fixture'");
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync().AsTask());
            Assert.Contains("owner/comment changed", refused.Message);
            Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM pg_database WHERE datname=current_database()"));
        }
        finally
        {
            var cluster = OwnedPostgreSqlCluster.Load();
            await fixture.ExecuteAsync($"COMMENT ON DATABASE \"{fixture.Database}\" IS '{cluster.InstanceId}:{fixture.Database}'");
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Temporary_createdb_permission_cannot_create_roles_or_assume_the_control_or_bootstrap_role()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        foreach(var sql in new[] { "SET ROLE fv_gate_bootstrap", "SET ROLE fv_gate_trust_control", "CREATE ROLE fv_test_unapproved_role" })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM pg_roles WHERE rolname='fv_test_unapproved_role'"));
    }
}
