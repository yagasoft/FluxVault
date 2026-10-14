using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Service;
using FluxVault.Windows.Security;

namespace FluxVault.TestHost;

// SYSTEM observes a real boot; it never starts a service, signs in, changes
// configuration or writes a source. Recovery runs under the actual creator token.
internal static class InstalledBootObservationProbe
{
    private sealed record Request(string RunId, string FirstVersion, string ExpectedSha256);

    internal static async Task RunAsync(string root, bool snapshot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18") throw new UnauthorizedAccessException("SYSTEM observer required.");
        root = Path.GetFullPath(root);
        if (Path.GetDirectoryName(root) != @"C:\ProgramData\FluxVault.Tests\MVPBoot" ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out _)) throw new InvalidDataException("Unowned boot observer root.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = deadline.Token;
        using var installation = WindowsVaultInstallation.Open(WindowsVaultInstallation.DefaultPath);
        var binding = installation.Configuration.Binding;
        const string creatorSid = "S-1-5-21-136112424-624261118-1239521417-1001";
        if (installation.Configuration.CreatorSid != creatorSid || binding.Id.Value != Guid.Parse("67539278-9292-4870-93cd-a011419228dc"))
            throw new InvalidDataException("Installed creator/vault binding changed.");
        await using var catalogue = new PostgreSqlVaultCatalogue(installation.Configuration.Endpoint);
        var initial = await ServiceDependencyStartup.OpenAsync(token => catalogue.ReadAutomaticStateAsync(binding, token),
            _ => { }, new ServiceStartupPolicy(), ct);
        var sessions = new WindowsAutomaticProtectionSessionProvider();
        if (snapshot)
        {
            await using var signedIn = await sessions.AcquireAsync(creatorSid, ct);
            Write("snapshot.json", new { ReadOnly = true, initial.Policy, initial.PendingCount, initial.UncertainCount,
                CreatorSignedIn = signedIn is not null, ObservedUtc = DateTimeOffset.UtcNow });
            return;
        }
        Request request;
        var (stream, pins) = WindowsVaultStorageGuard.OpenProtectedFile(Path.Combine(root, "capture-request.json"),
            "S-1-5-18", WindowsVaultStorageGuard.ReadTrustedPrincipals());
        using (stream) using (pins)
            request = (await JsonSerializer.DeserializeAsync<Request>(stream, cancellationToken: ct))!;
        if (request.RunId != Path.GetFileName(root) || request.ExpectedSha256.Length != 64 || string.IsNullOrWhiteSpace(request.FirstVersion))
            throw new InvalidDataException("Boot capture request changed.");
        using var bootRecord = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "boot-service-start.json"), ct));
        var bootUtc = bootRecord.RootElement.GetProperty("BootUtc").GetDateTimeOffset();
        var userRoot = Path.Combine(@"C:\Users\os008\FluxVault Staging\NEXT005", request.RunId);
        var source = Path.Combine(userRoot, "working", "saved.txt");
        var observations = new List<object>();
        var beforeSignIn = 0;
        while (true)
        {
            AssertDesktopClosed();
            await using var signedIn = await sessions.AcquireAsync(creatorSid, ct);
            if (signedIn is null)
            {
                var state = await catalogue.ReadAutomaticStateAsync(binding, ct);
                if (state.LastSuccessfulVersionId != request.FirstVersion || state.PendingCount < 1)
                    throw new InvalidDataException("Pending boot work changed before authorised creator access.");
                observations.Add(new { ObservedUtc = DateTimeOffset.UtcNow, CreatorSignedIn = false, state.PendingCount, state.UncertainCount });
                beforeSignIn++;
                await File.WriteAllTextAsync(Path.Combine(root, "before-sign-in.json"), JsonSerializer.Serialize(observations), ct);
                await Task.Delay(1000, ct);
                continue;
            }
            await signedIn.VerifyAsync(ct);
            var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForService());
            Task<FluxVaultIpcResponse> Send(FluxVaultIpcRequest value) => signedIn.Caller.RunAsCallerAsync(() => client.SendAsync(value, ct));
            var signedInAt = DateTimeOffset.UtcNow;
            while (true)
            {
                AssertDesktopClosed();
                FluxVaultIpcResponse state;
                try { state = await Send(FluxVaultIpcRequest.GetStatus()); }
                catch (TimeoutException)
                {
                    // Creator sign-in can precede delayed automatic SCM startup.
                    // Only the read-only status connection is retried; identity
                    // refusals and recovery mutations retain their normal contract.
                    await File.AppendAllTextAsync(Path.Combine(root, "service-start-waits.jsonl"),
                        JsonSerializer.Serialize(new { ObservedUtc = DateTimeOffset.UtcNow, WaitingForService = true }) + Environment.NewLine, ct);
                    await Task.Delay(1000, ct);
                    continue;
                }
                if (!state.Success || state.Status?.AutomaticProtection is null || state.VaultId != binding.Id)
                    throw new InvalidDataException("Installed service status was not verified after sign-in.");
                if (!state.Status.AutomaticProtection.Policy.Enabled || !state.Status.Configuration.IsEnabled ||
                    state.Status.Configuration.WatchedFolders.Count != 1 || state.Status.Configuration.WatchedFolders[0].Path != Path.GetDirectoryName(source))
                    throw new InvalidDataException("Saved enabled protection or owned source scope changed across boot.");
                var history = await Send(FluxVaultIpcRequest.ListVersions() with { VaultId = binding.Id });
                if (!history.Success) throw new InvalidDataException("Boot history query failed.");
                var version = history.Versions?.FirstOrDefault(item => item.SourcePath == source && !item.IsDeleted && item.VersionId != request.FirstVersion);
                if (version is null) { await Task.Delay(500, ct); continue; }
                if (version.CapturedAtUtc < bootUtc) throw new InvalidDataException("The pending version was captured before Windows restarted; repeat the boot gate with retained pending work.");
                var output = Path.Combine(userRoot, "boot-recovered.bin");
                var restored = await Send(FluxVaultIpcRequest.RestoreVersion(version.VersionId, output) with
                    { VaultId = binding.Id, ExpectedVaultRevision = state.VaultRevision, OperationId = Guid.NewGuid() });
                var actual = await signedIn.Caller.RunAsCallerAsync(async () =>
                {
                    await using var input = File.OpenRead(output);
                    return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
                });
                if (!restored.Success || actual != request.ExpectedSha256) throw new InvalidDataException("Boot recovery contents differ.");
                Write("boot-capture-verified.json", new { Passed = beforeSignIn > 0, BootUtc = bootUtc, version.CapturedAtUtc, BeforeSignInObservations = beforeSignIn,
                    BeforeSignInObserved = beforeSignIn > 0, CreatorObservedUtc = signedInAt, signedIn.SessionId, signedIn.AuthenticationId,
                    version.VersionId, ExpectedSha256 = request.ExpectedSha256, ActualSha256 = actual, DesktopClosed = true,
                    NoManualServiceStart = true, NoSourceWriteAfterBoot = true, CompletedUtc = DateTimeOffset.UtcNow });
                if (beforeSignIn == 0) throw new InvalidDataException("Post-boot capture/recovery passed, but the observer missed pre-sign-in waiting; that gate remains open.");
                return;
            }
        }

        void Write(string name, object value)
        {
            using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(output, value); output.Flush(true);
        }
    }

    private static void AssertDesktopClosed()
    {
        var processes = Process.GetProcessesByName("FluxVault.App");
        try { if (processes.Length > 0) throw new InvalidOperationException("Desktop opened during boot acceptance."); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
