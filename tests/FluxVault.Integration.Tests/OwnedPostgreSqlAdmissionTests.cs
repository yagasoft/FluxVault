using FluxVault.Testing;
using Npgsql;

namespace FluxVault.Integration.Tests;

public sealed class OwnedPostgreSqlAdmissionTests
{
    private static OwnedPostgreSqlCluster Cluster() => new(Guid.NewGuid().ToString("N"),
        Path.Combine(Path.GetTempPath(), "unused-owned-cluster"), 55439, 1, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("postgres")]
    [InlineData("template1")]
    [InlineData("fv_test_bad")]
    [InlineData("fv_test_00000000000000000000000000000000")]
    [InlineData("fv_test_abcd\";DROP DATABASE other;--")]
    public void Owned_connections_refuse_unrelated_or_invalid_database_names(string database)
    {
        Assert.Throws<ArgumentException>(() => Cluster().Connection(database));
    }

    [Fact]
    public void Owned_repository_connections_require_the_fixed_SSPI_account()
    {
        var database = "fv_test_" + Guid.NewGuid().ToString("N");
        using var connection = Cluster().Connection(database);
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal("fv_gate_service", settings.Username);
        Assert.Equal("SSPI", settings.RequireAuth);
        Assert.Equal(database, settings.Database);
        Assert.False(settings.Pooling);
        Assert.Null(settings.Password);
        Assert.Null(settings.Passfile);
    }
}
