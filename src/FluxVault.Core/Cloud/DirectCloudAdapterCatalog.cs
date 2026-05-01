using Amazon.S3;
using Azure.Storage.Blobs;
using Dropbox.Api;
using FluxVault.Abstractions.Configuration;
using Google.Apis.Drive.v3;
using Microsoft.Graph;

namespace FluxVault.Core.Cloud;

public sealed record DirectCloudAdapterDescriptor(
    DirectCloudProvider Provider,
    string PackageId,
    Type SdkClientType);

public static class DirectCloudAdapterCatalog
{
    public static IReadOnlyList<DirectCloudAdapterDescriptor> Descriptors { get; } =
    [
        new DirectCloudAdapterDescriptor(
            DirectCloudProvider.AzureBlob,
            "Azure.Storage.Blobs",
            typeof(BlobContainerClient)),
        new DirectCloudAdapterDescriptor(
            DirectCloudProvider.S3Compatible,
            "AWSSDK.S3",
            typeof(IAmazonS3)),
        new DirectCloudAdapterDescriptor(
            DirectCloudProvider.Dropbox,
            "Dropbox.Api",
            typeof(DropboxClient)),
        new DirectCloudAdapterDescriptor(
            DirectCloudProvider.GoogleDrive,
            "Google.Apis.Drive.v3",
            typeof(DriveService)),
        new DirectCloudAdapterDescriptor(
            DirectCloudProvider.OneDrive,
            "Microsoft.Graph",
            typeof(GraphServiceClient))
    ];
}
