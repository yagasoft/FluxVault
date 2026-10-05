using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace FluxVault.Testing;

internal sealed record WindowsDatabaseProbeConfiguration(string FixtureId, string Root, int Port,
    string Database, string Role, int TimeoutSeconds, Dictionary<string, string> Actors, bool RunCatalogueTests = false, bool RunMetadataTests = false,
    bool RunSingleVaultTests = false, bool RunPackagedIdentityTests = false, bool RunRestartTests = false,
    bool RunInterruptedEffectTests = false)
{
    internal static string AllowedParent => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FluxVault.Tests", "NEXT002");

    internal void Validate()
    {
        if (RunInterruptedEffectTests && (!RunSingleVaultTests || RunPackagedIdentityTests || RunRestartTests))
            throw new ArgumentException("Interrupted-effect proof requires an exclusive single-vault fixture.");
        if (RunRestartTests && (!RunSingleVaultTests || RunPackagedIdentityTests))
            throw new ArgumentException("Restart proof requires an exclusive single-vault fixture.");
        if (!Guid.TryParseExact(FixtureId, "N", out var id) || id == Guid.Empty ||
            !Path.IsPathFullyQualified(Root) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)), Path.Combine(AllowedParent, FixtureId), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Probe requires the explicit ProgramData GUID fixture root.");
        if (Port is <= 1024 or > 65535 or 5432 || Database != "fv_gate_261003" || Role != "fv_gate_service" || TimeoutSeconds is < 1 or > 60)
            throw new ArgumentException("Probe requires the bounded disposable database endpoint.");
        if (Actors is null || !Actors.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["System", "A", "B"]) || Actors["System"] != "S-1-5-18")
            throw new ArgumentException("Probe requires exact Windows fixture actors.");
        foreach (var actor in new[] { "A", "B" })
        {
            var sid = Actors[actor];
            if (sid is null || !Regex.IsMatch(sid, @"^S-1-5-21-\d+-\d+-\d+-\d+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new ArgumentException("Probe user must have an ordinary account SID.");
            _ = new SecurityIdentifier(sid);
        }
        if (Actors["A"] == Actors["B"]) throw new ArgumentException("Probe users must have distinct SIDs.");
    }

    internal static void RejectReparseComponents(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Probe path cannot contain reparse points.");
    }

    internal static WindowsDatabaseProbeConfiguration Read(string path)
    {
        path = Path.GetFullPath(path);
        var runtime = Path.GetDirectoryName(path);
        var root = runtime is null ? null : Path.GetDirectoryName(runtime);
        var id = root is null ? null : Path.GetFileName(root);
        if (root is null || !Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty ||
            !string.Equals(Path.GetDirectoryName(root), AllowedParent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(path, Path.Combine(root, "runtime", "database-probe.json"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Probe configuration is outside the owned runtime.");
        RejectReparseComponents(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 16384) throw new ArgumentException("Probe configuration exceeds its bound.");
        var configuration = JsonSerializer.Deserialize<WindowsDatabaseProbeConfiguration>(input,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Probe configuration is missing.");
        configuration.Validate();
        if (!string.Equals(path, Path.Combine(configuration.Root, "runtime", "database-probe.json"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Probe configuration is outside the owned runtime.");
        return configuration;
    }
}

internal static class WindowsDatabaseProbe
{
    internal static async Task<int> RunAsync(string configurationPath, string host, string actor, Guid probeId,
        Func<NpgsqlDataSource, WindowsDatabaseProbeConfiguration, Task<object>>? catalogueProbe = null)
    {
        if (host is not ("127.0.0.1" or "::1") || probeId == Guid.Empty)
            throw new ArgumentException("Probe requires an explicit loopback and operation identity.");
        var configuration = WindowsDatabaseProbeConfiguration.Read(configurationPath);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (!configuration.Actors.TryGetValue(actor, out var expectedSid) || sid != expectedSid)
            throw new ArgumentException("The probe process has the wrong Windows identity.");
        var builder = CreateConnectionSettings(configuration, host, actor, probeId);
        await using var dataSource = CreateDataSource(builder);
        var result = await ProbeAsync(dataSource, async connection =>
        {
            await using var command = new NpgsqlCommand("SELECT current_setting('fluxvault.test_instance'), inet_server_port(), current_database(), current_user", connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.GetString(0) != configuration.FixtureId || reader.GetInt32(1) != configuration.Port ||
                reader.GetString(2) != configuration.Database || reader.GetString(3) != configuration.Role)
                throw new InvalidOperationException("Probe server identity did not match the generated cluster.");
        });
        var catalogue = result.Authenticated && result.FixtureVerified && actor == "System" && configuration.RunCatalogueTests && catalogueProbe is not null
            ? await catalogueProbe(dataSource, configuration) : null;
        Console.WriteLine(JsonSerializer.Serialize(new { configuration.FixtureId, ProbeId = probeId.ToString("N"), Actor = actor,
            WindowsSid = sid, Host = host, result.Authenticated, result.FixtureVerified, result.SqlState, result.ErrorKind, Catalogue = catalogue }));
        return 0;
    }

    internal static NpgsqlConnectionStringBuilder CreateConnectionSettings(WindowsDatabaseProbeConfiguration configuration,
        string host, string actor, Guid probeId) => new()
    {
        Host = host, Port = configuration.Port, Database = configuration.Database, Username = configuration.Role,
        Pooling = false, Timeout = configuration.TimeoutSeconds, CommandTimeout = configuration.TimeoutSeconds,
        ApplicationName = $"FVGate.{probeId:N}.{actor}", IncludeErrorDetail = false
    };

    internal static NpgsqlDataSource CreateDataSource(NpgsqlConnectionStringBuilder builder)
    {
        builder.RequireAuth = "SSPI";
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        // A throwing provider prevents password/environment/passfile fallback. Npgsql forbids an explicit passfile with it.
        dataSourceBuilder.UsePasswordProvider(_ => throw new NotSupportedException("Password authentication is forbidden in this SSPI probe."),
            (_, _) => ValueTask.FromException<string>(new NotSupportedException("Password authentication is forbidden in this SSPI probe.")));
        return dataSourceBuilder.Build();
    }

    internal static async Task<WindowsDatabaseProbeResult> ProbeAsync(NpgsqlDataSource dataSource, Func<NpgsqlConnection, Task> verify)
    {
        string? sqlState = null;
        string? errorKind = null;
        var authenticated = false;
        var fixtureVerified = false;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            authenticated = true;
            await verify(connection);
            fixtureVerified = true;
        }
        catch (PostgresException exception) { sqlState = exception.SqlState; errorKind = "PostgreSQL"; }
        catch (Exception exception) when (exception is NpgsqlException or IOException or TimeoutException or NotSupportedException or InvalidOperationException)
        { errorKind = exception.GetType().Name; }
        return new(authenticated, fixtureVerified, sqlState, errorKind);
    }
}

internal sealed record WindowsDatabaseProbeResult(bool Authenticated, bool FixtureVerified, string? SqlState, string? ErrorKind);
