namespace FluxVault.Abstractions.Configuration;

public enum MetadataStoreProvider
{
    PostgreSql = 0
}

public sealed record MetadataStoreConfiguration(
    MetadataStoreProvider Provider,
    string Host,
    int Port,
    string DatabaseName,
    string Username,
    string ServiceName,
    string BackupDirectory,
    int BackupRetentionDays,
    int MaxCaptureWorkers,
    int MaxDbWriterConcurrency,
    TimeSpan ExportLagWarningThreshold)
{
    public static MetadataStoreConfiguration CreateDefault(string programDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        return new MetadataStoreConfiguration(
            Provider: MetadataStoreProvider.PostgreSql,
            Host: "localhost",
            Port: 5432,
            DatabaseName: "fluxvault_metadata",
            Username: "fluxvault",
            ServiceName: "postgresql-x64",
            BackupDirectory: Path.Combine(programDataPath, "db-backups"),
            BackupRetentionDays: 30,
            MaxCaptureWorkers: 4,
            MaxDbWriterConcurrency: 8,
            ExportLagWarningThreshold: TimeSpan.FromMinutes(15));
    }

    public MetadataStoreConfiguration Normalise(string programDataPath)
    {
        var defaults = CreateDefault(programDataPath);
        return this with
        {
            Host = string.IsNullOrWhiteSpace(Host) ? defaults.Host : Host.Trim(),
            Port = Port <= 0 ? defaults.Port : Port,
            DatabaseName = string.IsNullOrWhiteSpace(DatabaseName) ? defaults.DatabaseName : DatabaseName.Trim(),
            Username = string.IsNullOrWhiteSpace(Username) ? defaults.Username : Username.Trim(),
            ServiceName = string.IsNullOrWhiteSpace(ServiceName) ? defaults.ServiceName : ServiceName.Trim(),
            BackupDirectory = string.IsNullOrWhiteSpace(BackupDirectory)
                ? defaults.BackupDirectory
                : Path.GetFullPath(BackupDirectory),
            BackupRetentionDays = BackupRetentionDays,
            MaxCaptureWorkers = MaxCaptureWorkers,
            MaxDbWriterConcurrency = MaxDbWriterConcurrency,
            ExportLagWarningThreshold = ExportLagWarningThreshold <= TimeSpan.Zero
                ? defaults.ExportLagWarningThreshold
                : ExportLagWarningThreshold
        };
    }
}
