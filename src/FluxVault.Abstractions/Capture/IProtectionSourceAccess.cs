using FluxVault.Abstractions.Storage;

namespace FluxVault.Abstractions.Capture;

public enum ProtectionSourceAvailability { Present, Missing, Unavailable }

public sealed record ProtectionSourceInspection(ProtectionSourceAvailability Availability,
    RepositoryEntryKind? Kind = null, long? Length = null, DateTimeOffset? LastWriteUtc = null, string? FailureReason = null);

/// <summary>A live candidate name; it is not evidence of existence, absence or capture authority.</summary>
public sealed record ProtectionSourceCandidate(string Path, RepositoryEntryKind Kind);

/// <summary>Source-only access. Authenticated runtime composition must supply its caller-bound implementation.</summary>
public interface IProtectionSourceAccess
{
    ProtectionSourceInspection Inspect(string protectionRoot, string path, RepositoryEntryKind kind,
        CancellationToken cancellationToken = default);
    IEnumerable<ProtectionSourceCandidate> EnumerateDirectory(string protectionRoot, string directory,
        CancellationToken cancellationToken = default);
}
