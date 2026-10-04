using FluxVault.Testing;
using FluxVault.Integration.Tests.Fixtures;
using System.Text.Json;
using Npgsql;

namespace FluxVault.Integration.Tests;

public sealed class WindowsDatabaseProbeTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Actual_probe_connection_settings_build_without_a_password_or_password_file(string host)
    {
        var configuration = Configuration();
        var probeId = Guid.NewGuid();
        var builder = WindowsDatabaseProbe.CreateConnectionSettings(configuration, host, "System", probeId);
        await using var dataSource = WindowsDatabaseProbe.CreateDataSource(builder);
        var actual = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.Equal("SSPI", actual.RequireAuth);
        Assert.Null(actual.Password);
        Assert.Null(actual.Passfile);
        Assert.False(actual.Pooling);
        Assert.Equal(host, actual.Host);
        Assert.Equal(configuration.Port, actual.Port);
        Assert.Equal($"FVGate.{probeId:N}.System", actual.ApplicationName);
    }

    [Fact]
    [Trait("Category", "RequiresPostgreSql")]
    public async Task Accepted_connection_with_a_failed_verification_query_is_never_reported_as_denied()
    {
        var cluster = OwnedPostgreSqlCluster.Load();
        await cluster.VerifyAsync("postgres");
        await using var connection = cluster.Connection("postgres");
        await using var dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
        var result = await WindowsDatabaseProbe.ProbeAsync(dataSource, async opened =>
        {
            await using var command = new NpgsqlCommand("SELECT current_setting('fluxvault.absent_verification_marker')", opened);
            await command.ExecuteScalarAsync();
        });
        Assert.True(result.Authenticated);
        Assert.False(result.FixtureVerified);
        Assert.Equal("42704", result.SqlState);
    }

    [Fact]
    [Trait("Category", "RequiresPostgreSql")]
    public async Task Sspi_probe_refuses_real_PostgreSQL_trust_authentication()
    {
        var cluster = OwnedPostgreSqlCluster.Load();
        await cluster.VerifyAsync("postgres"); // Confirms this owned server accepts an unrestricted control connection.
        await using var connection = cluster.Connection("postgres");
        await using var dataSource = WindowsDatabaseProbe.CreateDataSource(new(connection.ConnectionString));
        var verificationCalled = false;
        var result = await WindowsDatabaseProbe.ProbeAsync(dataSource, _ =>
        {
            verificationCalled = true;
            return Task.CompletedTask;
        });
        Assert.False(result.Authenticated);
        Assert.False(result.FixtureVerified);
        Assert.False(verificationCalled);
        Assert.Equal("NpgsqlException", result.ErrorKind);
    }

    [Theory]
    [InlineData(5432)]
    [InlineData(0)]
    [InlineData(65536)]
    public void Probe_refuses_normal_or_invalid_ports_before_connecting(int port)
    {
        var configuration = Configuration() with { Port = port };
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Theory]
    [InlineData("fluxvault_metadata", "fv_gate_service")]
    [InlineData("fv_gate_261003", "fluxvault")]
    [InlineData("postgres", "postgres")]
    public void Probe_refuses_unrelated_database_or_account(string database, string role)
    {
        var configuration = Configuration() with { Database = database, Role = role };
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Theory]
    [InlineData("A", "S-1-5-18")]
    [InlineData("System", "S-1-5-21-11-22-33-1001")]
    [InlineData("B", "invalid")]
    public void Probe_refuses_an_actor_with_the_wrong_kind_of_SID(string actor, string sid)
    {
        var configuration = Configuration();
        configuration.Actors[actor] = sid;
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Probe_refuses_root_rebinding_and_zero_identity()
    {
        var configuration = Configuration();
        Assert.Throws<ArgumentException>(() => (configuration with { Root = Path.Combine(configuration.Root, "child") }).Validate());
        Assert.Throws<ArgumentException>(() => (configuration with { FixtureId = Guid.Empty.ToString("N") }).Validate());
    }

    [Fact]
    public void Probe_refuses_external_configuration_before_opening_it()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"FluxVault.Probe.NotOwned.{Guid.NewGuid():N}.json");
        Assert.Throws<ArgumentException>(() => WindowsDatabaseProbeConfiguration.Read(missing));
    }

    [Fact]
    public async Task Actual_probe_host_rejects_remote_endpoint_and_unowned_configuration_before_network_work()
    {
        using var fixture = new RepositoryProcessFixture();
        foreach (var host in new[] { "192.0.2.9", "127.0.0.1" })
        {
            await using var process = fixture.Start("windows-db-probe", "--configuration", Path.Combine(fixture.Root, "absent.json"),
                "--host", host, "--actor", "System", "--probe-id", Guid.NewGuid().ToString("N"));
            var result = await process.CompleteAsync();
            Assert.Equal(3, result.ExitCode);
            Assert.Empty(result.Output);
            using var error = JsonDocument.Parse(result.Error);
            Assert.Contains(host == "127.0.0.1" ? "owned runtime" : "explicit loopback", error.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public void Probe_accepts_the_explicit_fixture_endpoint_and_keeps_a_finite_timeout()
    {
        var configuration = Configuration();
        configuration.Validate();
        Assert.Throws<ArgumentException>(() => (configuration with { TimeoutSeconds = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (configuration with { TimeoutSeconds = 61 }).Validate());
    }

    internal static WindowsDatabaseProbeConfiguration Configuration()
    {
        var id = Guid.NewGuid().ToString("N");
        return new(id, Path.Combine(WindowsDatabaseProbeConfiguration.AllowedParent, id),
            55439, "fv_gate_261003", "fv_gate_service", 5,
            new(StringComparer.Ordinal) { ["System"] = "S-1-5-18", ["A"] = "S-1-5-21-11-22-33-1001", ["B"] = "S-1-5-21-11-22-33-1002" });
    }
}
