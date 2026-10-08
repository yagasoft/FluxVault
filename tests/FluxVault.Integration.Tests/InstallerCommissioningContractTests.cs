using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FluxVault.Integration.Tests;

public sealed class InstallerCommissioningContractTests
{
    [Fact]
    public async Task Compiled_msi_leaves_runtime_stopped_and_creates_no_vault_state_before_commissioning()
    {
        var root = FindRoot();
        var workspace = Path.Combine(Path.GetTempPath(), "FluxVaultInstallerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var publish = Path.Combine(workspace, "publish");
            foreach (var product in new[] { "app", "service", "cli" })
            {
                var directory = Path.Combine(publish, product);
                Directory.CreateDirectory(directory);
                // These inert files are only cab inputs. No installer or product executable runs.
                File.WriteAllText(Path.Combine(directory, $"FluxVault.{(product == "app" ? "App" : product == "service" ? "Service" : "Cli")}.exe"), "table-verification payload");
                File.WriteAllText(Path.Combine(directory, "dependency.dll"), "inert harvest input");
            }
            var output = Path.Combine(workspace, "compiled");
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (var argument in new[] { "build", Path.Combine(root, "installer/wix/FluxVault.Installer/FluxVault.Installer.wixproj"),
                         "-c", "Release", "--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                         "-p:PublishRoot=" + publish, "-p:ProductVersion=1.0.5.0", "-p:ReleasePackageRoot=" + output,
                         "-p:OutputPath=" + output + Path.DirectorySeparatorChar,
                         "-p:BaseIntermediateOutputPath=" + Path.Combine(workspace, "obj") + Path.DirectorySeparatorChar })
                start.ArgumentList.Add(argument);
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var process = Process.Start(start) ?? throw new IOException("Installer compiler did not launch.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(process.ExitCode == 0, await stdout + await stderr);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Task.WhenAll(stdout, stderr);
            }
            using var database = new MsiDatabase(Path.Combine(output, "FluxVault.Installer.msi"));
            AssertContract(database);
            var candidate = Environment.GetEnvironmentVariable("FLUXVAULT_CANDIDATE_MSI");
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                using var actual = new MsiDatabase(candidate);
                AssertContract(actual);
            }
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    private static void AssertContract(MsiDatabase database)
    {
        var failures = new List<string>();
        var service = Assert.Single(database.Query("SELECT `Name`, `StartType`, `StartName` FROM `ServiceInstall`"));
        Assert.Equal("FluxVaultService", service[0]);
        if (service[1] != "3") failures.Add("ServiceInstall must use SERVICE_DEMAND_START (3), found " + service[1]);
        Assert.True(service[2] is "LocalSystem" or ""); // MSI's empty account is LocalSystem.
        var control = Assert.Single(database.Query("SELECT `Name`, `Event`, `Wait` FROM `ServiceControl`"));
        Assert.Equal("FluxVaultService", control[0]);
        var actions = int.Parse(control[1], System.Globalization.CultureInfo.InvariantCulture);
        if ((actions & 0x1) != 0) failures.Add("ServiceControl must not start on install.");
        if (actions != 0xA2) failures.Add("ServiceControl must only stop install/uninstall and remove uninstall.");
        Assert.Equal("1", control[2]);
        var directories = database.Query("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`");
        if (directories.Any(row => row[0] is "CommonAppDataFolder" or "PROGRAMDATAFOLDER"))
            failures.Add("Directory must not declare the provisioning-owned ProgramData root.");
        var tables = database.Query("SELECT `Name` FROM `_Tables`").Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
        if (tables.Contains("MsiServiceConfig")) failures.Add("MSI must not configure delayed-auto before provisioning.");
        if (tables.Contains("Wix4ServiceConfig")) failures.Add("MSI must not configure restart actions before provisioning.");
        if (tables.Contains("CreateFolder"))
            if (database.Query("SELECT `Directory_` FROM `CreateFolder`").Any(row => row[0] == "PROGRAMDATAFOLDER"))
                failures.Add("CreateFolder must not create provisioning-owned state.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "FluxVault.slnx"))) return path.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class MsiDatabase : IDisposable
    {
        private readonly uint handle;
        public MsiDatabase(string path) => Check(MsiOpenDatabase(path, IntPtr.Zero, out handle)); // Read-only.
        public List<string[]> Query(string sql)
        {
            Check(MsiDatabaseOpenView(handle, sql, out var view));
            try
            {
                Check(MsiViewExecute(view, 0));
                var rows = new List<string[]>();
                uint status;
                while ((status = MsiViewFetch(view, out var record)) == 0)
                {
                    try
                    {
                        var values = new string[MsiRecordGetFieldCount(record)];
                        for (uint field = 1; field <= values.Length; field++)
                        {
                            var buffer = new StringBuilder(1024);
                            uint length = 1024;
                            Check(MsiRecordGetString(record, field, buffer, ref length));
                            values[field - 1] = buffer.ToString();
                        }
                        rows.Add(values);
                    }
                    finally { Check(MsiCloseHandle(record)); }
                }
                if (status != 259) Check(status); // ERROR_NO_MORE_ITEMS
                return rows;
            }
            finally { Check(MsiCloseHandle(view)); }
        }
        public void Dispose() => Check(MsiCloseHandle(handle));
        private static void Check(uint status) { if (status != 0) throw new IOException($"MSI inspection failed ({status})."); }
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
        [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
        [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
        [DllImport("msi.dll")] private static extern uint MsiRecordGetFieldCount(uint record);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint length);
        [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
    }
}
