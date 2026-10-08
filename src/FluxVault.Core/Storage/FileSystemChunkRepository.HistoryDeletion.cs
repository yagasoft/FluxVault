using System.Security.Cryptography;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Storage;

public sealed partial class FileSystemChunkRepository
{
    public async Task<RepositoryHistoryDeletionPreview> PreviewHistoryDeletionAsync(RepositoryPurgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateHistoryDeletionRequest(request,requireFingerprint:false);
        await using var lease=await AcquireLeaseAsync(MirrorLeaseMode.None,cancellationToken).ConfigureAwait(false);
        var manifests=(await ReadAllManifestsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        ValidateMaintenanceInputs(manifests);
        return BuildHistoryDeletionPreview(request,manifests,lease.Warnings);
    }

    private void ValidateHistoryDeletionRequest(RepositoryPurgeRequest request,bool requireFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.HistoryDeletionReview is not {VaultId.IsValid:true,ConfigurationRevision:>0} review ||
            binding is not null && binding.Id!=review.VaultId || request.Scopes is not {Count:1} ||
            requireFingerprint && (review.Fingerprint is not {Length:64} || !review.Fingerprint.All(Uri.IsHexDigit)))
            throw new ArgumentException("An exact vault, revision, single scope and reviewed fingerprint are required for history deletion.");
        foreach(var scope in request.Scopes.Concat(request.PreserveScopes ?? []))
            if(scope is null || string.IsNullOrWhiteSpace(scope.SourcePath) || !Path.IsPathFullyQualified(scope.SourcePath) || !Enum.IsDefined(scope.Kind))
                throw new ArgumentException("History deletion requires unambiguous absolute scopes.");
    }

    private RepositoryHistoryDeletionPreview BuildHistoryDeletionPreview(RepositoryPurgeRequest request,
        IReadOnlyList<FileVersionManifest> manifests,IReadOnlyList<string> leaseWarnings)
    {
        var scope=NormalisePurgeScopes(request.Scopes).Single();
        var preserved=NormalisePurgeScopes(request.PreserveScopes ?? []);
        var candidates=manifests.Where(manifest=>PurgeScopeMatches(scope,manifest)).OrderBy(manifest=>manifest.VersionId,StringComparer.Ordinal).ToArray();
        var candidateIds=candidates.Select(manifest=>manifest.VersionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var closure=new HashSet<string>(candidateIds,StringComparer.OrdinalIgnoreCase);
        ExpandPurgeReferences(manifests,closure);
        var outside=manifests.Where(manifest=>closure.Contains(manifest.VersionId) && !candidateIds.Contains(manifest.VersionId)).ToArray();
        var protectedOverlap=preserved.Any(kept=>HistoryDeletionScopesOverlap(scope,kept));
        var blocked=outside.Concat(candidates.Where(manifest=>preserved.Any(kept=>PurgeScopeMatches(kept,manifest))))
            .DistinctBy(manifest=>manifest.VersionId,StringComparer.OrdinalIgnoreCase).OrderBy(manifest=>manifest.VersionId,StringComparer.Ordinal).ToArray();
        var complete=candidates.Length<=RepositoryBrowsePolicy.MaximumItemsPerPage && blocked.Length<=RepositoryBrowsePolicy.MaximumItemsPerPage;
        var warnings=new List<string>(leaseWarnings);
        if(protectedOverlap)warnings.Add("This scope overlaps saved protection. Stop protecting the overlapping file or folder and save before deleting its history. Pausing does not remove protection.");
        if(outside.Length>0)warnings.Add($"{outside.Length} retained version(s) outside this scope depend on its history. No outside-scope version may be deleted.");
        if(candidates.Length==0)warnings.Add("No history exists in this scope.");
        if(!complete)warnings.Add($"The complete review exceeds the existing {RepositoryBrowsePolicy.MaximumItemsPerPage}-version browse bound. Choose a smaller scope; deletion is unavailable for this incomplete preview.");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new {request.HistoryDeletionReview!.VaultId,request.HistoryDeletionReview.ConfigurationRevision,
            Scope=new {Path=RepositoryHistoryPaging.CanonicalPath(scope.SourcePath),scope.Kind},
            Preserved=preserved.Select(kept=>new {Path=RepositoryHistoryPaging.CanonicalPath(kept.SourcePath),kept.Kind})
                .OrderBy(kept=>kept.Path,StringComparer.Ordinal).ThenBy(kept=>kept.Kind).ToArray()});
        // Include the complete validated manifest graph, including retained
        // references and chunk identities. An unrelated change is conservatively
        // stale too; never infer permission from dependency expansion.
        foreach(var manifest in manifests.OrderBy(item=>item.VersionId,StringComparer.Ordinal))Append(manifest);
        return new(scope,Convert.ToHexString(hash.GetHashAndReset()),complete && !protectedOverlap && outside.Length==0 && candidates.Length>0,
            complete,candidates.Length,candidates.Sum(manifest=>manifest.EntryKind==RepositoryEntryKind.File ? manifest.LogicalLength : 0),
            candidates.Take(RepositoryBrowsePolicy.MaximumItemsPerPage).Select(Summary).ToArray(),
            blocked.Take(RepositoryBrowsePolicy.MaximumItemsPerPage).Select(Summary).ToArray(),warnings);

        void Append<T>(T value) { hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(value,JsonOptions)); hash.AppendData([0]); }
        RepositoryVersionSummary Summary(FileVersionManifest manifest)=>ToSummary(manifest) with {FolderEntries=null};
    }

    private static bool HistoryDeletionScopesOverlap(RepositoryPurgeScope left,RepositoryPurgeScope right)
    {
        if(left.Kind==RepositoryPurgeScopeKind.File)return ContainsFile(right,left.SourcePath);
        if(right.Kind==RepositoryPurgeScopeKind.File)return ContainsFile(left,right.SourcePath);
        if(left.Kind==RepositoryPurgeScopeKind.RecursiveFolder && PathIsWithinOrEqual(right.SourcePath,left.SourcePath))return true;
        if(right.Kind==RepositoryPurgeScopeKind.RecursiveFolder && PathIsWithinOrEqual(left.SourcePath,right.SourcePath))return true;
        return PathEquals(left.SourcePath,right.SourcePath);

        static bool ContainsFile(RepositoryPurgeScope scope,string file)=>scope.Kind switch
        {
            RepositoryPurgeScopeKind.File=>PathEquals(scope.SourcePath,file),
            RepositoryPurgeScopeKind.ImmediateFiles=>IsDirectChildFile(file,scope.SourcePath),
            RepositoryPurgeScopeKind.RecursiveFolder=>PathIsWithinOrEqual(file,scope.SourcePath),
            _=>false
        };
    }
}

/// <summary>A definite refusal raised under the lease before any history mutation.</summary>
public sealed class RepositoryHistoryDeletionRefusedException(string message) : IOException(message);
