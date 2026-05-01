using FluxVault.Abstractions.Configuration;

namespace FluxVault.Abstractions.Cloud;

public sealed record CloudObjectPutRequest(
    string Key,
    Stream Content,
    string? ContentType = null);

public sealed record CloudObjectMetadata(
    string Key,
    long Length,
    DateTimeOffset? LastModifiedUtc = null);

public interface ICloudObjectAdapter
{
    DirectCloudProvider Provider { get; }

    Task PutObjectAsync(CloudObjectPutRequest request, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    Task<CloudObjectMetadata> GetMetadataAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default);

    Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default);
}

