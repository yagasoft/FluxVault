using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;

namespace FluxVault.Integration.Tests;

public sealed class LegacyReceiptFingerprintTests
{
    [Fact]
    public void Historical_request_fixture_matches_the_catalogue_codec_including_timestamp_bytes()
    {
        var configuration=FluxVaultConfiguration.CreateDefault(Path.GetFullPath("legacy-receipt-fixture"));
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {MaxDepth=32,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,Converters={new JsonStringEnumConverter()}};
        foreach(var request in new[] {FluxVaultIpcRequest.SetProtectionPaused(),FluxVaultIpcRequest.RunBackupNow(),
            FluxVaultIpcRequest.SaveConfiguration(configuration),
            FluxVaultIpcRequest.SaveConfiguration(configuration,purgeRemovedSelections:true,
                removedSelections:[new(Path.Combine(configuration.RepositoryPath,"retired"),RepositoryPurgeScopeKind.RecursiveFolder)],preservedSelections:[])})
        {
            var bound=request with {VaultId=new VaultId(Guid.Parse("cc4aeb24-1c1c-4455-974e-1bc6db04f589")),ExpectedVaultRevision=17,
                OperationId=Guid.Parse("89227d3f-73f0-4a3e-a7eb-d13743826e2b")};
            var legacyBytes=LegacyOperationReceiptFixture.EncodeLegacyRequest(bound);
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(bound,options),legacyBytes);
            var fingerprint=typeof(PostgreSqlVaultCatalogue).GetMethod("RequestFingerprint",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            Assert.Equal(fingerprint.Invoke(null,[bound]),Convert.ToHexString(SHA256.HashData(legacyBytes)));
        }
    }
}
