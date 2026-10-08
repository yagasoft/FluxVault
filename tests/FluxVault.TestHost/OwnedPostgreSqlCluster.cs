using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace FluxVault.Testing;

// Shared only by the test host and integration fixtures; never part of the product.
internal sealed record OwnedPostgreSqlCluster(string InstanceId, string DataDirectory, int Port, int ProcessId, DateTimeOffset ProcessStartedUtc)
{
    internal const string MaintenanceDatabase = "fv_gate_261003";
    internal const string ServiceRole = "fv_gate_service";
    internal const string TrustControl = "fv_gate_trust_control";
    internal string Root => Path.GetDirectoryName(DataDirectory)!;

    internal static OwnedPostgreSqlCluster Load()
    {
        var marker = Environment.GetEnvironmentVariable("FLUXVAULT_TEST_PG_MARKER");
        if (string.IsNullOrWhiteSpace(marker))
            throw new InvalidOperationException("PostgreSQL integrity tests require the authorised eng/test-windows-database-boundary.ps1 -RunIntegrityTests owned fixture. No test was skipped.");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if(identity.User?.Value != "S-1-5-18" || identity.ImpersonationLevel != System.Security.Principal.TokenImpersonationLevel.None)
            throw new UnauthorizedAccessException("The owned PostgreSQL integrity fixture requires its native SYSTEM actor.");
        marker = Path.GetFullPath(marker);
        var runtime = Path.GetDirectoryName(marker)!;
        var parent = Path.GetDirectoryName(runtime)!;
        var allowedParent = WindowsDatabaseProbeConfiguration.AllowedParent;
        if (!string.Equals(Path.GetDirectoryName(parent), Path.GetFullPath(allowedParent), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(parent), "N", out var rootId) || rootId == Guid.Empty ||
            !string.Equals(marker, Path.Combine(parent, "runtime", "integrity-cluster.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable PostgreSQL ownership marker is outside the generated test root.");
        RejectReparse(marker);
        using var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
        if(input.Length > 4096) throw new InvalidDataException("Test-cluster marker exceeds its bound.");
        var owner = JsonSerializer.Deserialize<OwnedPostgreSqlCluster>(input, new JsonSerializerOptions
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 }) ?? throw new InvalidDataException("Missing test-cluster owner.");
        if (owner.InstanceId != rootId.ToString("N") || owner.Port <= 1024 || owner.Port > 65535 || owner.Port == 5432 ||
            !string.Equals(Path.GetFullPath(owner.DataDirectory), Path.Combine(parent, "data"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid disposable PostgreSQL identity.");
        RejectReparse(owner.DataDirectory);
        using var process = Process.GetProcessById(owner.ProcessId);
        if (process.HasExited || !string.Equals(process.MainModule?.FileName, Path.Combine(parent,"postgresql","bin","postgres.exe"), StringComparison.OrdinalIgnoreCase) ||
            Math.Abs((process.StartTime.ToUniversalTime() - owner.ProcessStartedUtc.UtcDateTime).TotalMilliseconds) > 1 ||
            int.Parse(File.ReadLines(Path.Combine(owner.DataDirectory, "postmaster.pid")).First()) != owner.ProcessId)
            throw new InvalidOperationException("Disposable PostgreSQL process identity changed.");
        var postmaster = File.ReadAllLines(Path.Combine(owner.DataDirectory, "postmaster.pid"));
        if(postmaster.Length < 8 || !string.Equals(Path.GetFullPath(postmaster[1]), owner.DataDirectory, StringComparison.OrdinalIgnoreCase) ||
            int.Parse(postmaster[3]) != owner.Port || postmaster[7].Trim() != "ready")
            throw new InvalidOperationException("Disposable PostgreSQL data directory/port readiness changed.");
        return owner;
    }

    internal static void ValidateGeneratedDatabase(string database)
    {
        if(!database.StartsWith("fv_test_", StringComparison.Ordinal) || !Guid.TryParseExact(database[8..],"N",out var id) ||
            id == Guid.Empty || database != "fv_test_" + id.ToString("N"))
            throw new ArgumentException("Only a generated fixture database name is permitted.", nameof(database));
    }

    internal NpgsqlConnection Connection(string database)
    {
        if(database != MaintenanceDatabase) ValidateGeneratedDatabase(database);
        return new(new NpgsqlConnectionStringBuilder
        { Host = "127.0.0.1", Port = Port, Database = database, Username = ServiceRole, RequireAuth="SSPI",
            SearchPath="pg_catalog", Pooling = false, Timeout = 5, CommandTimeout = 30 }.ConnectionString);
    }

    // The sole intentional authentication exception: an empty, CONNECT-only negative control.
    internal NpgsqlConnection TrustControlConnection() => new(new NpgsqlConnectionStringBuilder
    { Host="127.0.0.1", Port=Port, Database=TrustControl, Username=TrustControl, Pooling=false, Timeout=5, CommandTimeout=5 }.ConnectionString);

    internal async Task VerifyAsync(string database)
    {
        await using var connection = Connection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance'), current_setting('port'), current_user, current_database()", connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != InstanceId || int.Parse(reader.GetString(1)) != Port ||
            reader.GetString(2) != ServiceRole || reader.GetString(3) != database)
            throw new InvalidOperationException("PostgreSQL server did not confirm the disposable test identity.");
    }

    private static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Test cluster paths cannot contain reparse points.");
    }
}
