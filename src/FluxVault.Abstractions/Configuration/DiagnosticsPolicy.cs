namespace FluxVault.Abstractions.Configuration;

public enum DiagnosticLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    None = 6
}

public sealed record DiagnosticsPolicy(
    bool IsFileLoggingEnabled,
    DiagnosticLogLevel FileLogLevel,
    string LogDirectory,
    int MaxLogFileMegabytes,
    int RetainedLogFileCount,
    TimeSpan TelemetrySampleInterval,
    int RetainedTelemetrySampleCount)
{
    public const int DefaultMaxLogFileMegabytes = 25;
    public const int DefaultRetainedLogFileCount = 8;
    public const int DefaultRetainedTelemetrySampleCount = 4320;
    public static readonly TimeSpan DefaultTelemetrySampleInterval = TimeSpan.FromSeconds(5);

    public static DiagnosticsPolicy CreateDefault(string programDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        return new DiagnosticsPolicy(
            IsFileLoggingEnabled: true,
            FileLogLevel: DiagnosticLogLevel.Warning,
            LogDirectory: Path.Combine(programDataPath, "logs"),
            MaxLogFileMegabytes: DefaultMaxLogFileMegabytes,
            RetainedLogFileCount: DefaultRetainedLogFileCount,
            TelemetrySampleInterval: DefaultTelemetrySampleInterval,
            RetainedTelemetrySampleCount: DefaultRetainedTelemetrySampleCount);
    }

    public DiagnosticsPolicy Normalise(string programDataPath)
    {
        var defaults = CreateDefault(programDataPath);
        return this with
        {
            LogDirectory = string.IsNullOrWhiteSpace(LogDirectory)
                ? defaults.LogDirectory
                : LogDirectory.Trim(),
            MaxLogFileMegabytes = Math.Max(1, MaxLogFileMegabytes),
            RetainedLogFileCount = Math.Max(1, RetainedLogFileCount),
            TelemetrySampleInterval = TelemetrySampleInterval <= TimeSpan.Zero
                ? defaults.TelemetrySampleInterval
                : TelemetrySampleInterval,
            RetainedTelemetrySampleCount = Math.Max(1, RetainedTelemetrySampleCount)
        };
    }
}
