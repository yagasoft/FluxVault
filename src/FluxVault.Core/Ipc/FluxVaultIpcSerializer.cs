using System.Text.Json;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Ipc;

public static class FluxVaultIpcSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SerializeRequest(FluxVaultIpcRequest request)
    {
        return JsonSerializer.Serialize(request, JsonOptions);
    }

    public static FluxVaultIpcRequest DeserializeRequest(string payload)
    {
        return JsonSerializer.Deserialize<FluxVaultIpcRequest>(payload, JsonOptions)
            ?? throw new InvalidDataException("IPC request payload was empty.");
    }

    public static string SerializeResponse(FluxVaultIpcResponse response)
    {
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    public static FluxVaultIpcResponse DeserializeResponse(string payload)
    {
        return JsonSerializer.Deserialize<FluxVaultIpcResponse>(payload, JsonOptions)
            ?? throw new InvalidDataException("IPC response payload was empty.");
    }
}
