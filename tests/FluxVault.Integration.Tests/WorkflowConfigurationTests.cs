namespace FluxVault.Integration.Tests;

public sealed class WorkflowConfigurationTests
{
    [Fact]
    public void Pull_request_ci_contains_build_test_only()
    {
        var root = FindRepositoryRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains("pull_request:", ci, StringComparison.Ordinal);
        Assert.Contains("build-test:", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("codeql", ci, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("github/codeql-action", ci, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Codeql_workflow_does_not_run_on_pull_requests()
    {
        var root = FindRepositoryRoot();
        var codeqlPath = Path.Combine(root, ".github", "workflows", "codeql.yml");

        Assert.True(File.Exists(codeqlPath));
        var codeql = File.ReadAllText(codeqlPath);
        Assert.DoesNotContain("pull_request:", codeql, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", codeql, StringComparison.Ordinal);
        Assert.Contains("branches: [main]", codeql, StringComparison.Ordinal);
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
