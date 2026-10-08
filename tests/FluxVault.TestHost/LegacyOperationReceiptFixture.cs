using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using Npgsql;

/// <summary>Seeds a pre-NEXT-004 receipt in the already verified, owned fixture database.</summary>
internal static class LegacyOperationReceiptFixture
{
    internal static async Task<FluxVaultIpcResponse> RecordAsync(NpgsqlConnection connection,
        FluxVaultIpcRequest request, string actorSid, long revision, VaultPermission permissions)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters={new JsonStringEnumConverter()} };
        var fingerprint=Convert.ToHexString(SHA256.HashData(EncodeLegacyRequest(request)));
        var response = FluxVaultIpcResponse.Ok() with { VaultId=request.VaultId, VaultRevision=revision, OperationId=request.OperationId };
        await using var command = new NpgsqlCommand("""
            INSERT INTO fv_control.operations(operation_id,vault_id,actor_sid,command,fingerprint,required_permissions,state,revision,response,completed_at_utc)
            VALUES(@operation,@vault,@actor,@command,@fingerprint,@permissions,1,@revision,@response,now())
            """,connection);
        command.Parameters.AddWithValue("operation",request.OperationId!.Value);
        command.Parameters.AddWithValue("vault",request.VaultId!.Value.Value);
        command.Parameters.AddWithValue("actor",actorSid);
        command.Parameters.AddWithValue("command",(int)request.Command);
        command.Parameters.AddWithValue("fingerprint",fingerprint);
        command.Parameters.AddWithValue("permissions",(int)permissions);
        command.Parameters.AddWithValue("revision",revision);
        command.Parameters.AddWithValue("response",JsonSerializer.Serialize(response,options));
        await command.ExecuteNonQueryAsync();
        return response;
    }

    internal static byte[] EncodeLegacyRequest(FluxVaultIpcRequest request)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters={new JsonStringEnumConverter()} };
        // This fixed field list is the 1.0.7 request shape. Do not derive it from
        // the new request type: additive null fields would change old hashes.
        string[] fields = ["command","configuration","versionId","outputPath","exportPath","mirrorNodeId",
            "conflictId","conflictAction","sourcePath","isDirectory","destinationMode","overwriteConfirmed",
            "profileId","profileDisplayName","sourceProfileId","statusDetailLevel","purgeRemovedSelections",
            "removedSelections","preservedSelections","vaultId","expectedVaultRevision","operationId",
            "accessGrants","historyQuery","snapshotQuery","currentEntriesQuery","destinationPath"];
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request,options));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                if (fields.Contains(property.Name,StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    // DateTime converters write '+' directly; rebuilding a
                    // JsonElement string escapes it and changes the old hash.
                    writer.WriteRawValue(property.Value.GetRawText());
                }
            writer.WriteEndObject();
        }
        return bytes.ToArray();
    }
}
