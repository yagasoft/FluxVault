using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Tests;

public sealed class RollingJsonFileLoggerProviderTests
{
    [Fact]
    public void Default_policy_writes_warning_but_filters_trace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var policyRuntime = new DiagnosticsPolicyRuntime(workspace.RootPath);
        policyRuntime.Update(DiagnosticsPolicy.CreateDefault(workspace.RootPath));
        using var provider = new RollingJsonFileLoggerProvider(policyRuntime);
        var logger = provider.CreateLogger("FluxVault.Tests");

        logger.LogTrace("trace detail");
        logger.LogWarning("warning detail");

        var logFile = Assert.Single(Directory.GetFiles(Path.Combine(workspace.RootPath, "logs"), "*.jsonl"));
        var text = File.ReadAllText(logFile);
        Assert.DoesNotContain("trace detail", text);
        Assert.Contains("warning detail", text);
        Assert.Contains("\"level\":\"Warning\"", text);
    }

    [Fact]
    public void Trace_policy_writes_trace_entries_as_json_lines()
    {
        using var workspace = TemporaryWorkspace.Create();
        var policyRuntime = new DiagnosticsPolicyRuntime(workspace.RootPath);
        policyRuntime.Update(DiagnosticsPolicy.CreateDefault(workspace.RootPath) with
        {
            FileLogLevel = DiagnosticLogLevel.Trace
        });
        using var provider = new RollingJsonFileLoggerProvider(policyRuntime);
        var logger = provider.CreateLogger("FluxVault.Tests");

        logger.LogTrace("trace detail {Value}", 42);

        var logFile = Assert.Single(Directory.GetFiles(Path.Combine(workspace.RootPath, "logs"), "*.jsonl"));
        var text = File.ReadAllText(logFile);
        Assert.Contains("trace detail 42", text);
        Assert.Contains("\"category\":\"FluxVault.Tests\"", text);
    }

    [Fact]
    public void Rolling_retention_keeps_configured_file_count()
    {
        using var workspace = TemporaryWorkspace.Create();
        var policyRuntime = new DiagnosticsPolicyRuntime(workspace.RootPath);
        policyRuntime.Update(DiagnosticsPolicy.CreateDefault(workspace.RootPath) with
        {
            FileLogLevel = DiagnosticLogLevel.Trace,
            MaxLogFileMegabytes = 1,
            RetainedLogFileCount = 2
        });
        using var provider = new RollingJsonFileLoggerProvider(policyRuntime);
        var logger = provider.CreateLogger("FluxVault.Tests");

        for (var i = 0; i < 30; i++)
        {
            logger.LogWarning("large message {Index} {Payload}", i, new string('x', 120_000));
        }

        Assert.True(Directory.GetFiles(Path.Combine(workspace.RootPath, "logs"), "*.jsonl").Length <= 2);
    }
}
