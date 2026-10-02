using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace FluxVault.Testing;

// Shared only by the test host and integration fixtures; never part of the product.
internal sealed record OwnedPostgreSqlCluster(string InstanceId, string DataDirectory, int Port, int ProcessId, DateTimeOffset ProcessStartedUtc)
{
    internal static OwnedPostgreSqlCluster Load()
    {
        var marker = Environment.GetEnvironmentVariable("FLUXVAULT_TEST_PG_MARKER");
        if (string.IsNullOrWhiteSpace(marker))
            throw new InvalidOperationException("PostgreSQL integrity tests require eng/test-repository-integrity.ps1 -PostgreSqlBinPath <existing bin directory>. No test was skipped.");
        marker = Path.GetFullPath(marker);
        var parent = Path.GetDirectoryName(marker)!;
        var allowedParent = Path.Combine(Path.GetTempPath(), "FluxVault.Integrity.PostgreSql");
        if (!string.Equals(Path.GetDirectoryName(parent), Path.GetFullPath(allowedParent), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(parent), "N", out _) || Path.GetFileName(marker) != "owner.json")
            throw new InvalidOperationException("Disposable PostgreSQL ownership marker is outside the generated test root.");
        RejectReparse(marker);
        var owner = JsonSerializer.Deserialize<OwnedPostgreSqlCluster>(File.ReadAllBytes(marker)) ?? throw new InvalidDataException("Missing test-cluster owner.");
        if (!Guid.TryParseExact(owner.InstanceId, "N", out _) || owner.Port <= 1024 || owner.Port > 65535 || owner.Port == 5432 ||
            !string.Equals(Path.GetFullPath(owner.DataDirectory), Path.Combine(parent, "data"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid disposable PostgreSQL identity.");
        RejectReparse(owner.DataDirectory);
        using var process = Process.GetProcessById(owner.ProcessId);
        if (process.HasExited || process.ProcessName != "postgres" ||
            Math.Abs((process.StartTime.ToUniversalTime() - owner.ProcessStartedUtc.UtcDateTime).TotalMilliseconds) > 1 ||
            int.Parse(File.ReadLines(Path.Combine(owner.DataDirectory, "postmaster.pid")).First()) != owner.ProcessId)
            throw new InvalidOperationException("Disposable PostgreSQL process identity changed.");
        return owner;
    }

    internal NpgsqlConnection Connection(string database) => new(new NpgsqlConnectionStringBuilder
    { Host = "127.0.0.1", Port = Port, Database = database, Username = "fv_test", Pooling = false, Timeout = 5, CommandTimeout = 30 }.ConnectionString);

    internal async Task VerifyAsync(string database)
    {
        await using var connection = Connection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_setting('data_directory'), current_setting('fluxvault.test_instance'), current_setting('port'), current_user", connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || !string.Equals(Path.GetFullPath(reader.GetString(0)), Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase) ||
            reader.GetString(1) != InstanceId || int.Parse(reader.GetString(2)) != Port || reader.GetString(3) != "fv_test")
            throw new InvalidOperationException("PostgreSQL server did not confirm the disposable test identity.");
    }

    private static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Test cluster paths cannot contain reparse points.");
    }
}
