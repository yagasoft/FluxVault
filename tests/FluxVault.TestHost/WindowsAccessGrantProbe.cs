using System.IO;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Ipc;
using FluxVault.Testing;
using FluxVault.Windows.Security;

namespace FluxVault.TestHost;

/// <summary>Real elevated-user and ordinary-user tokens in the existing owned fixture.</summary>
internal static class WindowsAccessGrantProbe
{
    internal static async Task<int> RunAsync(string configurationPath, string actor, string phase, string? groupSid)
    {
        var fixture = WindowsDatabaseProbeConfiguration.Read(configurationPath);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("Missing native caller identity.");
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (actor == "Elevated")
        {
            if (!elevated || sid == fixture.Actors["System"] || fixture.Actors.Values.Contains(sid))
                throw new UnauthorizedAccessException("The access probe requires the actual elevated runner user.");
        }
        else if (actor != "B" || sid != fixture.Actors["B"] || elevated)
            throw new UnauthorizedAccessException("The access probe requires ordinary fixture user B.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(
            "FluxVault.Tests." + fixture.FixtureId, fixture.Actors["System"]));
        var id = new VaultId(Guid.ParseExact(fixture.FixtureId,"N"));
        var checks = new List<string>();
        Guid? elevatedOperation = null;
        if (actor == "Elevated")
        {
            if (phase is not ("grant-user" or "grant-group" or "revoke")) throw new ArgumentException("Unknown elevated phase.");
            var status = await client.SendAsync(FluxVaultIpcRequest.GetStatus(),deadline.Token);
            Check(status.Success && status.VaultId == id && status.VaultRevision is > 0 && status.Status is not null,
                "native elevated administrator receives the installed identity");
            IReadOnlyList<VaultAccessGrant> grants = phase switch
            {
                "grant-user" => [new(fixture.Actors["B"],VaultPermission.ReadHistory)],
                "grant-group" => [new(new SecurityIdentifier(groupSid ?? throw new ArgumentException("Missing fixture group.")).Value,VaultPermission.ReadHistory)],
                _ => []
            };
            var request = new FluxVaultIpcRequest(FluxVaultIpcCommand.SetVaultAccess,null,null,null,null,
                VaultId:id,ExpectedVaultRevision:status.VaultRevision,OperationId:Guid.NewGuid(),AccessGrants:grants);
            elevatedOperation = request.OperationId;
            var changed = await client.SendAsync(request,deadline.Token);
            Check(changed.Success && changed.VaultId == id && changed.OperationId == request.OperationId &&
                changed.VaultRevision == status.VaultRevision + 1,"native elevated administrator commits only the explicit access change");
            var replay = await client.SendAsync(request,deadline.Token);
            Check(replay.Success && replay.VaultRevision == changed.VaultRevision && replay.OperationId == changed.OperationId,
                "access change replay retains its exact revision and receipt");
        }
        else if (phase == "denied")
        {
            var status = await client.SendAsync(FluxVaultIpcRequest.GetStatus(),deadline.Token);
            Check(!status.Success && status.ErrorCode == FluxVaultIpcErrorCode.Denied && status.VaultId is null && status.Status is null,
                "ungranted ordinary principal receives neither repository identity nor status");
            var history = await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},deadline.Token);
            Check(!history.Success && history.ErrorCode == FluxVaultIpcErrorCode.Denied && history.Versions is null,
                "an ungranted ordinary principal cannot start history work");
        }
        else if (phase is "user" or "group" or "reopened")
        {
            if (phase is "group" or "reopened")
                Check(new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(groupSid ?? throw new ArgumentException("Missing fixture group."))),
                    "fresh ordinary token contains the explicitly owned fixture group");
            else
                Check(!new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(groupSid ?? throw new ArgumentException("Missing fixture group."))),
                    "direct user-grant phase has no fixture group membership");
            var status = await client.SendAsync(FluxVaultIpcRequest.GetStatus(),deadline.Token);
            Check(status.Success && status.VaultId == id && status.VaultRevision is > 0 && status.Status is not null,
                "explicit read grant permits native ordinary-user status");
            var history = await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id},deadline.Token);
            Check(history.Success && history.Versions is { Count: > 0 },"explicit read grant permits retained history");
            var commands = new[] {FluxVaultIpcCommand.RunBackupNow,FluxVaultIpcCommand.RunRepositoryScrub,
                FluxVaultIpcCommand.RunRetentionNow,FluxVaultIpcCommand.RestoreVersion,FluxVaultIpcCommand.SetVaultAccess};
            foreach (var command in commands)
            {
                var request = new FluxVaultIpcRequest(command,null,null,null,null,VaultId:id,
                    ExpectedVaultRevision:status.VaultRevision,OperationId:Guid.NewGuid(),AccessGrants:[]);
                var refused = await client.SendAsync(request,deadline.Token);
                Check(!refused.Success && refused.ErrorCode == FluxVaultIpcErrorCode.Denied && refused.Backup is null &&
                    refused.RestoreResult is null,"read-only principal cannot execute " + command);
            }
            var otherOperation = Guid.ParseExact(File.ReadAllText(Path.Combine(fixture.Root,"runtime","access-operation-id")).Trim(),"N");
            var receipt = await client.SendAsync(new(FluxVaultIpcCommand.GetOperationStatus,null,null,null,null,
                VaultId:id,OperationId:otherOperation),deadline.Token);
            Check(!receipt.Success && receipt.ErrorCode == FluxVaultIpcErrorCode.Denied && receipt.Status is null && receipt.Backup is null,
                "unrelated operation receipt remains private despite the history grant");
        }
        else throw new ArgumentException("Unknown ordinary phase.");
        Console.WriteLine(JsonSerializer.Serialize(new {Actor=actor,Phase=phase,WindowsSid=sid,NativeElevated=elevated,
            Passed=checks.Count,Checks=checks,OperationId=elevatedOperation,NativeAccessVerified=true}));
        return 0;

        void Check(bool result,string name)
        { if (!result) throw new InvalidOperationException("Native access contract failed: " + name); checks.Add(name); }
    }
}
