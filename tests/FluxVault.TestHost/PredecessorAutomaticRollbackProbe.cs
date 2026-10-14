using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using FluxVault.Testing;

namespace FluxVault.TestHost;

/// <summary>Runs the frozen accepted manual-only service on the already owned private fixture.</summary>
internal static class PredecessorAutomaticRollbackProbe
{
    internal static async Task RunAsync(string bootstrap, string pipe, CancellationToken ct, Func<Task> assertion)
    {
        const string retained = @"C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f\retained-candidates\3da720718ab04b99afdf43455222e0cf";
        var manifestPath = Path.Combine(retained, "candidate.json");
        if (Hash(manifestPath) != "DA9887839FA9B1DE158747E3FF575BE34DFB03C22A66BDB14F4ED62D037E456B")
            throw new InvalidDataException("The accepted 1.0.9 manifest changed.");
        var root = Path.Combine(retained, "compatibility-app");
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)))
        {
            var count = 0;
            foreach (var entry in manifest.RootElement.GetProperty("Payload").EnumerateArray())
            {
                var relative = entry.GetProperty("RelativePath").GetString()!;
                if (!relative.StartsWith("app\\", StringComparison.Ordinal)) continue;
                if (relative.Contains("..", StringComparison.Ordinal) || relative.Contains(':')) throw new InvalidDataException();
                var path = Path.Combine(root, relative[4..]);
                WindowsDatabaseProbeConfiguration.RejectReparseComponents(path);
                if (Hash(path) != entry.GetProperty("Sha256").GetString()) throw new InvalidDataException("The accepted 1.0.9 payload changed.");
                count++;
            }
            if (count != 63) throw new InvalidDataException("The frozen predecessor payload is incomplete.");
        }
        var loader = new PredecessorContext(root);
        object? service = null;
        try
        {
            var windows = loader.LoadFromAssemblyPath(Path.Combine(root, "FluxVault.Windows.dll"));
            var core = loader.LoadFromAssemblyPath(Path.Combine(root, "FluxVault.Core.dll"));
            service = await ResultAsync(windows.GetType("FluxVault.Windows.Security.WindowsSingleVaultService", true)!
                .GetMethod("OpenAsync")!.Invoke(null, [bootstrap, ct])!);
            var factory = windows.GetType("FluxVault.Windows.Security.WindowsFluxVaultPipeServerFactory", true)!
                .GetMethod("ForPrivateFixture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [pipe]);
            var caller = Activator.CreateInstance(windows.GetType("FluxVault.Windows.Security.WindowsFluxVaultCallerContextProvider", true)!);
            var server = core.GetType("FluxVault.Core.Ipc.NamedPipeFluxVaultServer", true)!.GetConstructors().Single()
                .Invoke([service, factory, caller, null, null, null, null]);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = (Task)server.GetType().GetMethod("RunAsync")!.Invoke(server, [stop.Token])!;
            try { await assertion(); }
            finally { await stop.CancelAsync(); await running; }
        }
        finally
        {
            if (service is IAsyncDisposable disposable) await disposable.DisposeAsync();
            loader.Unload();
        }
    }

    private static async Task<object?> ResultAsync(object value)
    { var task = (Task)value; await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
    private static string Hash(string path)
    { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private sealed class PredecessorContext(string root) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name?.StartsWith("FluxVault.", StringComparison.Ordinal) == true
            ? LoadFromAssemblyPath(Path.Combine(root, name.Name + ".dll")) : null;
    }
}
