using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Tests;

public sealed class AutomaticCommitContractTests
{
    [Fact]
    public async Task Reserved_capture_identity_is_published_and_independently_recoverable()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var reserved = Guid.CreateVersion7().ToString("N");
        var request = ReservedRequest("saved automatic bytes", reserved);

        var result = await repository.CommitAsync(request);

        Assert.Equal(reserved, result.Manifest.VersionId);
        var output = Path.Combine(workspace.RootPath, "recovered.txt");
        await repository.RestoreAsync(reserved, output);
        Assert.Equal("saved automatic bytes", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task A_reserved_identity_cannot_overwrite_an_acknowledged_version()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var reserved = Guid.CreateVersion7().ToString("N");
        await repository.CommitAsync(ReservedRequest("previous good save", reserved));

        await Assert.ThrowsAnyAsync<IOException>(() => repository.CommitAsync(ReservedRequest("different save", reserved)));

        var output = Path.Combine(workspace.RootPath, "recovered.txt");
        await repository.RestoreAsync(reserved, output);
        Assert.Equal("previous good save", await File.ReadAllTextAsync(output));
        Assert.Single(await repository.ListVersionsAsync());
    }

    [Fact]
    public async Task Rejected_publication_keeps_previous_good_version_and_publishes_no_reserved_result()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var previous = await repository.CommitAsync(RepositoryIntegrityTests.Request("previous good save"));
        var reserved = Guid.CreateVersion7().ToString("N");
        var request = ReservedRequest("source changed while reading", reserved);
        var checks = 0;
        request = request with { BeforePublication = _ =>
        {
            checks++;
            throw new IOException("The source or creator session changed.");
        } };

        await Assert.ThrowsAnyAsync<IOException>(() => repository.CommitAsync(request));

        Assert.Equal(1, checks);
        Assert.Equal(previous.Manifest.VersionId, Assert.Single(await repository.ListVersionsAsync()).VersionId);
        var output = Path.Combine(workspace.RootPath, "recovered.txt");
        await repository.RestoreAsync(previous.Manifest.VersionId, output);
        Assert.Equal("previous good save", await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-reserved-version")]
    public async Task Invalid_reserved_identity_is_refused_without_publication(string reserved)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.CommitAsync(ReservedRequest("bytes", reserved)));

        Assert.Empty(await repository.ListVersionsAsync());
    }

    private static FileCommitRequest ReservedRequest(string content, string reserved)
    {
        return RepositoryIntegrityTests.Request(content) with { ReservedVersionId = reserved };
    }
}
