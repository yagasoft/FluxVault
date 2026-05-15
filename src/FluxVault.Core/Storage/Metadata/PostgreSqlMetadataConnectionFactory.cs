using FluxVault.Abstractions.Configuration;
using Npgsql;

namespace FluxVault.Core.Storage.Metadata;

public static class PostgreSqlMetadataConnectionFactory
{
    public static string BuildConnectionString(MetadataStoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration.Host,
            Port = configuration.Port,
            Database = configuration.DatabaseName,
            Username = configuration.Username,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = Math.Max(1, configuration.MaxDbWriterConcurrency + configuration.MaxCaptureWorkers),
            Timeout = 15,
            CommandTimeout = 120,
            IncludeErrorDetail = false
        };
        return builder.ConnectionString;
    }

    public static NpgsqlConnection CreateConnection(MetadataStoreConfiguration configuration)
    {
        return new NpgsqlConnection(BuildConnectionString(configuration));
    }
}
