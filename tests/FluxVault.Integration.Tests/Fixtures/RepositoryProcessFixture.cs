using System.Diagnostics;
using System.Text;

namespace FluxVault.Integration.Tests.Fixtures;

internal sealed class RepositoryProcessFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "FluxVault.Integrity", Guid.NewGuid().ToString("N"));
    internal string Repository => Path.Combine(Root, "repository");
    internal string Source => Path.Combine(Root, "working", "source.bin");

    internal RepositoryProcessFixture()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "protected content");
    }

    internal FixtureProcess Start(string mode, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(HostPath());
        foreach (var argument in new[] { "--mode", mode, "--scratch", Root }.Concat(args)) start.ArgumentList.Add(argument);
        return new FixtureProcess(Process.Start(start) ?? throw new IOException("Fixture process did not start."));
    }

    private static string HostPath()
    {
        if (Environment.GetEnvironmentVariable("FLUXVAULT_INTEGRITY_HOST") is { Length: > 0 } explicitPath) return explicitPath;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "FluxVault.TestHost", "bin", "Release", "net10.0-windows", "FluxVault.TestHost.dll");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Build FluxVault.TestHost in Release or set FLUXVAULT_INTEGRITY_HOST.");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed record FixtureProcessResult(int ExitCode, string Output, string Error);

internal sealed class FixtureProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly StringBuilder output = new();
    private readonly Task<string> errors;
    private readonly Task outputPump;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource gated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FixtureProcess(Process process)
    {
        this.process = process;
        errors = process.StandardError.ReadToEndAsync();
        outputPump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            if (line == "READY") ready.TrySetResult();
            if (line == "GATE") gated.TrySetResult();
        }
        var error = new IOException($"Fixture exited before readiness: {await errors}");
        ready.TrySetException(error);
        gated.TrySetException(error);
    }

    internal Task WaitForReadyAsync() => ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    internal Task WaitForGateAsync() => gated.Task.WaitAsync(TimeSpan.FromSeconds(20));

    internal async Task<FixtureProcessResult> CompleteAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        await outputPump;
        return new(process.ExitCode, output.ToString(), await errors);
    }

    internal async Task KillAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await outputPump;
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        process.Dispose();
    }
}
