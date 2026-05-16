using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Diagnostics;

public sealed class RollingJsonFileLoggerProvider(DiagnosticsPolicyRuntime policyRuntime) : ILoggerProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();
    private string? currentFilePath;
    private long droppedMessageCount;
    private int fileSequence;

    public ILogger CreateLogger(string categoryName)
    {
        return new RollingJsonFileLogger(categoryName, this);
    }

    public void Dispose()
    {
    }

    public LogRuntimeStatus GetStatus()
    {
        var policy = policyRuntime.Current;
        lock (gate)
        {
            var retained = Directory.Exists(policy.LogDirectory)
                ? Directory.GetFiles(policy.LogDirectory, "fluxvault-service-*.jsonl").Length
                : 0;
            var currentBytes = currentFilePath is not null && File.Exists(currentFilePath)
                ? new FileInfo(currentFilePath).Length
                : 0;
            return new LogRuntimeStatus(
                policy.IsFileLoggingEnabled,
                policy.LogDirectory,
                policy.FileLogLevel.ToString(),
                currentFilePath,
                currentBytes,
                retained,
                droppedMessageCount);
        }
    }

    private bool IsEnabled(LogLevel logLevel)
    {
        var policy = policyRuntime.Current;
        return policy.IsFileLoggingEnabled
            && logLevel != LogLevel.None
            && logLevel >= ToLogLevel(policy.FileLogLevel);
    }

    private void Write<TState>(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var policy = policyRuntime.Current;
        try
        {
            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            lock (gate)
            {
                Directory.CreateDirectory(policy.LogDirectory);
                var path = EnsureCurrentFile(policy);
                var entry = new LogEntry(
                    DateTimeOffset.UtcNow,
                    logLevel.ToString(),
                    categoryName,
                    eventId.Id,
                    eventId.Name,
                    message,
                    exception?.GetType().FullName,
                    exception?.Message);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, Encoding.UTF8);
                RetainFiles(policy);
            }
        }
        catch
        {
            lock (gate)
            {
                droppedMessageCount++;
            }
        }
    }

    private string EnsureCurrentFile(DiagnosticsPolicy policy)
    {
        var maxBytes = Math.Max(1, policy.MaxLogFileMegabytes) * 1024L * 1024L;
        if (currentFilePath is not null
            && IsInDirectory(currentFilePath, policy.LogDirectory)
            && File.Exists(currentFilePath)
            && new FileInfo(currentFilePath).Length < maxBytes)
        {
            return currentFilePath;
        }

        currentFilePath = Path.Combine(
            policy.LogDirectory,
            $"fluxvault-service-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{++fileSequence:0000}.jsonl");
        return currentFilePath;
    }

    private static void RetainFiles(DiagnosticsPolicy policy)
    {
        if (!Directory.Exists(policy.LogDirectory))
        {
            return;
        }

        var retained = Math.Max(1, policy.RetainedLogFileCount);
        foreach (var file in Directory.GetFiles(policy.LogDirectory, "fluxvault-service-*.jsonl")
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(retained))
        {
            file.Delete();
        }
    }

    private static bool IsInDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        return string.Equals(
            Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static LogLevel ToLogLevel(DiagnosticLogLevel level)
    {
        return level switch
        {
            DiagnosticLogLevel.Trace => LogLevel.Trace,
            DiagnosticLogLevel.Debug => LogLevel.Debug,
            DiagnosticLogLevel.Information => LogLevel.Information,
            DiagnosticLogLevel.Warning => LogLevel.Warning,
            DiagnosticLogLevel.Error => LogLevel.Error,
            DiagnosticLogLevel.Critical => LogLevel.Critical,
            _ => LogLevel.None
        };
    }

    private sealed record LogEntry(
        DateTimeOffset TimestampUtc,
        string Level,
        string Category,
        int EventId,
        string? EventName,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage);

    private sealed class RollingJsonFileLogger(string categoryName, RollingJsonFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return provider.IsEnabled(logLevel);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            provider.Write(categoryName, logLevel, eventId, state, exception, formatter);
        }
    }
}
