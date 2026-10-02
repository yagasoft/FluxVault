namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryRestoreResult(
    string OutputPath, long VerifiedLogicalBytes, int RestoredFileCount, IReadOnlyList<string> Warnings);
