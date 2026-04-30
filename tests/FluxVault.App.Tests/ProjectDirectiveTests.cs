using System.IO;

namespace FluxVault.App.Tests;

public sealed class ProjectDirectiveTests
{
    [Fact]
    public void Root_agents_file_records_configuration_options_directive()
    {
        var path = Path.Combine(FindRepositoryRoot(), "AGENTS.md");

        var text = File.ReadAllText(path);

        Assert.Contains("parameterise appropriate and reasonable configurations", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Options dialogue", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dormant", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FluxVault.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find FluxVault repository root.");
    }
}
