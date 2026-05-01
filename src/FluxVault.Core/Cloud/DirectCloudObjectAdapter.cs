using FluxVault.Abstractions.Cloud;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Cloud;

public interface ICloudObjectClient
{
    Task PutAsync(CloudObjectPutRequest request, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    Task<CloudObjectMetadata> GetMetadataAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudObjectMetadata>> ListAsync(string prefix, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class DirectCloudObjectAdapter(
    DirectCloudProvider provider,
    ICloudObjectClient client) : ICloudObjectAdapter
{
    public DirectCloudProvider Provider { get; } = provider;

    public Task PutObjectAsync(CloudObjectPutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        return client.PutAsync(request, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return client.OpenReadAsync(key, cancellationToken);
    }

    public Task<CloudObjectMetadata> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return client.GetMetadataAsync(key, cancellationToken);
    }

    public Task<IReadOnlyList<CloudObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default)
    {
        return client.ListAsync(prefix ?? string.Empty, cancellationToken);
    }

    public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return client.DeleteAsync(key, cancellationToken);
    }
}
