namespace FluxVault.Integration.Tests;

public sealed class PackagingTests
{
    [Fact]
    public void Developer_install_scripts_are_present()
    {
        var root = FindRepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, "eng", "install-service.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "eng", "uninstall-service.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "installer", "quickstart.md")));
        Assert.True(File.Exists(Path.Combine(root, "installer", "sample-config.json")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FluxVault.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
