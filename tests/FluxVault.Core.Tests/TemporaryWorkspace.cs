namespace FluxVault.Core.Tests;

internal sealed class TemporaryWorkspace : IDisposable
{
    private TemporaryWorkspace(string rootPath)
    {
        RootPath = rootPath;
        RepositoryPath = Path.Combine(rootPath, "repository");
    }

    public string RootPath { get; }

    public string RepositoryPath { get; }

    public static TemporaryWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxVault.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TemporaryWorkspace(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
