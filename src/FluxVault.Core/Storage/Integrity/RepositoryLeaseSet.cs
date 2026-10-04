using System.Collections.Concurrent;
using FluxVault.Core.Security;

namespace FluxVault.Core.Storage.Integrity;

internal enum MirrorLeaseMode { None, BestEffort, Required }

internal sealed class RepositoryLeaseSet : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> handles = [];
    private readonly List<string> mirrors = [];
    private readonly List<string> warnings = [];
    private SemaphoreSlim? gate;
    internal IReadOnlyList<string> MirrorRoots => mirrors;
    internal IReadOnlyList<string> Warnings => warnings;
    internal string StorageId { get; private set; } = string.Empty;

    internal void LabelMirror(string root, string label)
    {
        for (var i = 0; i < warnings.Count; i++)
            warnings[i] = warnings[i].Replace($"Mirror '{root}' was not accessed: ",
                $"Mirror '{label}' at {root} is unavailable. Last error while acquiring storage: ", StringComparison.OrdinalIgnoreCase);
    }

    internal static async ValueTask<RepositoryLeaseSet> AcquireAsync(string primaryRoot,
        IReadOnlyList<string> mirrorRoots, MirrorLeaseMode mirrorMode, CancellationToken cancellationToken,
        Action<string>? validateVolume = null, VaultBinding? binding = null, bool provision = false)
    {
        var primary = StorageOwnership.Canonical(primaryRoot);
        var roots = new[] { primary }.Concat(mirrorRoots.Select(StorageOwnership.Canonical)).ToArray();
        for (var i = 0; i < roots.Length; i++)
        {
            StorageRootPolicy.ValidatePathShape(roots[i]);
            StorageOwnership.RejectReparseComponents(roots[i]);
            for (var j = 0; j < i; j++)
                if (StorageOwnership.Contains(roots[i], roots[j]) || StorageOwnership.Contains(roots[j], roots[i]))
                    throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch, "Repository and mirror roots must be distinct and non-overlapping.");
        }
        if (binding is not null && provision)
            foreach (var root in roots) StorageOwnership.AssertFreshVaultRoot(root);
        var lease = new RepositoryLeaseSet { gate = Gates.GetOrAdd(primary, _ => new SemaphoreSlim(1, 1)) };
        await lease.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var openedRoots = new List<string>();
            foreach (var root in roots.Where(root => root == primary || mirrorMode != MirrorLeaseMode.None).Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    (validateVolume ?? StorageRootPolicy.Validate)(root);
                    if (binding is not null)
                    {
                        if (provision) StorageOwnership.AssertFreshVaultRoot(root);
                        else if (root != primary && mirrorMode == MirrorLeaseMode.BestEffort && !Directory.Exists(root))
                            throw new DirectoryNotFoundException("The bound mirror is unavailable.");
                        else StorageOwnership.Ensure(root, binding.Id.Value.ToString("N"), root == primary ? "primary" : "mirror", binding);
                    }
                    if (!provision && root != primary && mirrorMode == MirrorLeaseMode.Required && !Directory.Exists(root))
                        throw new DirectoryNotFoundException($"Required mirror '{root}' is unavailable; maintenance was not started.");
                    if (binding is null || provision) Directory.CreateDirectory(root);
                    StorageOwnership.RejectReparseComponents(root);
                    var lockPath = Path.Combine(root, StorageOwnership.LockName);
                    StorageOwnership.RejectReparseComponents(lockPath);
                    lease.handles.Add(new FileStream(lockPath, binding is not null && !provision ? FileMode.Open : FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None));
                    openedRoots.Add(root);
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && (root == primary || mirrorMode == MirrorLeaseMode.Required))
                {
                    throw new RepositoryIntegrityException(RepositoryIntegrityFailure.RepositoryBusy, $"Storage '{root}' is busy. Retry after the other operation finishes.");
                }
                catch (Exception exception) when (root != primary && mirrorMode == MirrorLeaseMode.BestEffort &&
                    exception is IOException or UnauthorizedAccessException && exception is not RepositoryIntegrityException)
                {
                    lease.warnings.Add($"Mirror '{root}' was not accessed: {exception.Message}");
                }
            }
            lease.StorageId = StorageOwnership.Ensure(primary, null, "primary", binding, provision);
            foreach (var root in openedRoots.Where(root => root != primary))
            {
                StorageOwnership.Ensure(root, lease.StorageId, "mirror", binding, provision);
                lease.mirrors.Add(root);
            }
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var handle in handles) handle.Dispose();
        handles.Clear();
        Interlocked.Exchange(ref gate, null)?.Release();
        return ValueTask.CompletedTask;
    }
}
