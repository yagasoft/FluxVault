using System.Text;
using FluxVault.Abstractions.Cloud;
using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Cloud;

namespace FluxVault.Core.Tests;

public sealed class DirectCloudAdapterTests
{
    [Fact]
    public void Sdk_catalog_declares_all_required_cloud_provider_bindings()
    {
        var descriptors = DirectCloudAdapterCatalog.Descriptors;

        Assert.Contains(descriptors, descriptor => descriptor.Provider == DirectCloudProvider.AzureBlob
            && descriptor.PackageId == "Azure.Storage.Blobs"
            && descriptor.SdkClientType.FullName == "Azure.Storage.Blobs.BlobContainerClient");
        Assert.Contains(descriptors, descriptor => descriptor.Provider == DirectCloudProvider.S3Compatible
            && descriptor.PackageId == "AWSSDK.S3"
            && descriptor.SdkClientType.FullName == "Amazon.S3.IAmazonS3");
        Assert.Contains(descriptors, descriptor => descriptor.Provider == DirectCloudProvider.Dropbox
            && descriptor.PackageId == "Dropbox.Api"
            && descriptor.SdkClientType.FullName == "Dropbox.Api.DropboxClient");
        Assert.Contains(descriptors, descriptor => descriptor.Provider == DirectCloudProvider.GoogleDrive
            && descriptor.PackageId == "Google.Apis.Drive.v3"
            && descriptor.SdkClientType.FullName == "Google.Apis.Drive.v3.DriveService");
        Assert.Contains(descriptors, descriptor => descriptor.Provider == DirectCloudProvider.OneDrive
            && descriptor.PackageId == "Microsoft.Graph"
            && descriptor.SdkClientType.FullName == "Microsoft.Graph.GraphServiceClient");
    }

    [Fact]
    public async Task Direct_cloud_object_adapter_uses_fake_client_without_live_provider_calls()
    {
        var client = new FakeCloudObjectClient();
        var adapter = new DirectCloudObjectAdapter(DirectCloudProvider.Dropbox, client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello cloud"));

        await adapter.PutObjectAsync(new CloudObjectPutRequest("chunks/aa/object.bin", content, "application/octet-stream"));
        var metadata = await adapter.GetMetadataAsync("chunks/aa/object.bin");
        await using var read = await adapter.OpenReadAsync("chunks/aa/object.bin");
        var listed = await adapter.ListObjectsAsync("chunks/");
        await adapter.DeleteObjectAsync("chunks/aa/object.bin");

        using var reader = new StreamReader(read, Encoding.UTF8);
        Assert.Equal(DirectCloudProvider.Dropbox, adapter.Provider);
        Assert.Equal("hello cloud", await reader.ReadToEndAsync());
        Assert.Equal("chunks/aa/object.bin", metadata.Key);
        Assert.Equal(11, metadata.Length);
        Assert.Equal("chunks/aa/object.bin", Assert.Single(listed).Key);
        Assert.Equal(
            ["put:chunks/aa/object.bin", "metadata:chunks/aa/object.bin", "read:chunks/aa/object.bin", "list:chunks/", "delete:chunks/aa/object.bin"],
            client.Calls);
        Assert.False(client.UsedLiveProvider);
    }

    private sealed class FakeCloudObjectClient : ICloudObjectClient
    {
        private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public bool UsedLiveProvider { get; private set; }

        public Task PutAsync(CloudObjectPutRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add($"put:{request.Key}");
            using var memory = new MemoryStream();
            request.Content.CopyTo(memory);
            objects[request.Key] = memory.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls.Add($"read:{key}");
            return Task.FromResult<Stream>(new MemoryStream(objects[key], writable: false));
        }

        public Task<CloudObjectMetadata> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls.Add($"metadata:{key}");
            return Task.FromResult(new CloudObjectMetadata(key, objects[key].LongLength, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<CloudObjectMetadata>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            Calls.Add($"list:{prefix}");
            return Task.FromResult<IReadOnlyList<CloudObjectMetadata>>(
                objects
                    .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(pair => new CloudObjectMetadata(pair.Key, pair.Value.LongLength, DateTimeOffset.UtcNow))
                    .ToArray());
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls.Add($"delete:{key}");
            objects.Remove(key);
            return Task.CompletedTask;
        }
    }
}
