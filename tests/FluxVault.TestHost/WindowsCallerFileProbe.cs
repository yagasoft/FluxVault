using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.IO;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Testing;
using FluxVault.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.TestHost;

internal static class WindowsCallerFileProbe
{
    internal static async Task<int> RunAsync(string configurationPath, string actor)
    {
        var config = WindowsDatabaseProbeConfiguration.Read(configurationPath);
        using var identity = WindowsIdentity.GetCurrent();
        if (config.Actors.GetValueOrDefault(actor) != identity.User?.Value) throw new UnauthorizedAccessException("Native file actor identity mismatch.");
        var pipe = "FluxVault.Tests." + config.FixtureId;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output = Path.Combine(config.Root, "output-A");
        if (actor == "System")
        {
            var handler = new Handler(config, output);
            var server = new NamedPipeFluxVaultServer(handler, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(pipe), new WindowsFluxVaultCallerContextProvider());
            var serving = server.RunAsync(deadline.Token);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(config.Root, "runtime", "caller-files-ready.json"), JsonSerializer.Serialize(new { Pipe = pipe }), deadline.Token);
                while (!File.Exists(Path.Combine(config.Root, "runtime", "caller-files-stop"))) await Task.Delay(50, deadline.Token);
            }
            finally { await deadline.CancelAsync(); await serving; }
            if (!handler.Sids.SequenceEqual(new[] { config.Actors["A"], config.Actors["B"] }) || handler.Checks.Count < 8)
                throw new InvalidOperationException("Native caller contracts were incomplete.");
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = handler.Checks.Count, handler.Checks, NativeCallerTokens = true, AuthenticatedSids = handler.Sids }));
            return 0;
        }
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(pipe, config.Actors["System"]));
        if (actor == "B")
        {
            var denied = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), deadline.Token);
            if (denied.Success || denied.ErrorCode != FluxVaultIpcErrorCode.Denied) throw new InvalidOperationException("Ungrant B was not denied.");
            Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Denied = true })); return 0;
        }
        var source = Path.Combine(output, "working"); Directory.CreateDirectory(source);
        var destination = Path.Combine(output, "destination"); Directory.CreateDirectory(destination);
        var parentAcl = new DirectoryInfo(destination).GetAccessControl();
        parentAcl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new DirectoryInfo(destination).SetAccessControl(parentAcl);
        await File.WriteAllTextAsync(Path.Combine(source, "document.txt"), "professional working bytes", deadline.Token);
        Directory.CreateDirectory(Path.Combine(source, "nested", "deeper"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "deeper", "drawing.txt"), "nested drawing bytes", deadline.Token);
        var original = Path.Combine(output, "original.txt"); await File.WriteAllTextAsync(original, "preserve hard-linked original", deadline.Token);
        if (!CreateHardLink(Path.Combine(destination, "recovered.txt"), original, IntPtr.Zero)) throw new IOException("Hard-link fixture failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        // This DELETE_CHILD authority predates staging, exercising the parent-right bypass explicitly.
        using var deleteChild = CreateFile(destination, 0x100040, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (deleteChild.IsInvalid) throw new IOException("Parent DELETE_CHILD fixture failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var responseTask = client.SendAsync(FluxVaultIpcRequest.GetStatus(), deadline.Token);
        var phase = Path.Combine(output, "caller-stage.json");
        while (!File.Exists(phase) && !responseTask.IsCompleted) await Task.Delay(20, deadline.Token);
        var attacks = new List<string>();
        if (File.Exists(phase))
        {
            var stage = JsonSerializer.Deserialize<Stage>(await File.ReadAllTextAsync(phase, deadline.Token))!;
            if (Path.GetDirectoryName(stage.Path) != destination || !Path.GetFileName(stage.Path).StartsWith(".FluxVault-recovery-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected native staging scope.");
            try
            {
                Denied(() => Directory.Move(stage.Path, stage.Path + "-moved"), "parent DELETE_CHILD rename", attacks);
                Denied(() => { using var handle = CreateFile(stage.Path, 0x10000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); if (!handle.IsInvalid) throw new InvalidOperationException("Attacker acquired DELETE."); ThrowLastError(); }, "parent DELETE_CHILD open", attacks);
                Denied(() => Directory.CreateDirectory(Path.Combine(stage.Path, "unexpected")), "unexpected child create", attacks);
                Denied(() => File.WriteAllText(Path.Combine(stage.Path, "document.txt"), "attacker bytes"), "staged data write", attacks);
                Denied(() => File.SetAttributes(stage.Path, FileAttributes.Hidden), "root attributes", attacks);
                Denied(() => { var acl = new DirectoryInfo(stage.Path).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow)); new DirectoryInfo(stage.Path).SetAccessControl(acl); }, "owner DACL mutation", attacks);
                Denied(() => { using var handle = CreateFile(stage.Path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); if (!handle.IsInvalid) throw new InvalidOperationException("Attacker acquired reparse-write authority."); ThrowLastError(); }, "in-place reparse authority", attacks);
                await File.WriteAllTextAsync(Path.Combine(output, "caller-attacks.json"), JsonSerializer.Serialize(new { Attacks = attacks, Error = (string?)null }), deadline.Token);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "caller-attacks.json"), JsonSerializer.Serialize(new { Attacks = attacks, Error = exception.Message }), deadline.Token);
                throw;
            }
        }
        var response = await responseTask;
        if (!response.Success) throw new InvalidOperationException("Native caller recovery failed: " + response.ErrorMessage);
        var restored = Path.Combine(destination, "recovered-folder", "document.txt");
        if (await File.ReadAllTextAsync(restored, deadline.Token) != "professional working bytes" || await File.ReadAllTextAsync(original, deadline.Token) != "preserve hard-linked original")
            throw new InvalidOperationException("Recovered bytes or original hard link changed.");
        await File.AppendAllTextAsync(restored, " edited", deadline.Token);
        var nested = Path.Combine(destination, "recovered-folder", "nested", "deeper", "drawing.txt");
        if (await File.ReadAllTextAsync(nested, deadline.Token) != "nested drawing bytes") throw new InvalidOperationException("Nested recovery content changed.");
        await File.AppendAllTextAsync(nested, " edited", deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { Actor = actor, Passed = attacks.Count + 2, Attacks = attacks, CallerCanEditPublished = true, HardLinkedOriginalPreserved = true }));
        return 0;
    }

    private sealed record Stage(string Path);
    private sealed class Handler(WindowsDatabaseProbeConfiguration config, string output) : IAuthenticatedFluxVaultRequestHandler
    {
        internal List<string> Checks { get; } = [];
        internal List<string> Sids { get; } = [];
        public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token)
        {
            try { return await HandleCoreAsync(caller, request, token); }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.Combine(config.Root, "runtime", "caller-server-error.json"), JsonSerializer.Serialize(new
                { CallerSid = caller.UserSid, Checks, Error = exception.ToString() }));
                throw;
            }
        }
        private async Task<FluxVaultIpcResponse> HandleCoreAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token)
        {
            Sids.Add(caller.UserSid);
            if (caller.UserSid != config.Actors["A"]) { Checks.Add("native B denied before source/output work"); return FluxVaultIpcResponse.Failure("Access denied.") with { ErrorCode = FluxVaultIpcErrorCode.Denied }; }
            if (!caller.ImpersonationPermitted) throw new InvalidOperationException("Native caller impersonation was unavailable.");
            var sourceRoot = Path.Combine(output, "working"); var sourcePath = Path.Combine(sourceRoot, "document.txt");
            var destination = Path.Combine(output, "destination");
            var repo = new FileSystemChunkRepository(Path.Combine(config.Root, "catalogue", "caller-repository"), new FastCdcChunker(new(128, 256, 512)), new Blake3ContentHasher(), new ZstdChunkCodec());
            await using var source = await new WindowsCallerFileAccess().OpenSourceAsync(caller, sourceRoot, sourcePath, token);
            var capture = await repo.CommitAsync(new("native", sourcePath, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort,
                FluxVault.Abstractions.Policies.CompressionPreference.Off, 1, source, WatchedFolderPath: sourceRoot), token);
            Checks.Add("native A source handle captured verified repository bytes");
            var nestedSource = Path.Combine(sourceRoot, "nested", "deeper", "drawing.txt");
            await using (var nested = await new WindowsCallerFileAccess().OpenSourceAsync(caller, sourceRoot, nestedSource, token))
                await repo.CommitAsync(new("native", nestedSource, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort,
                    FluxVault.Abstractions.Policies.CompressionPreference.Off, 1, nested, WatchedFolderPath: sourceRoot), token);
            var recoveredFile = await WindowsCallerRecoveryTarget.CreateAsync(caller, Path.Combine(destination, "recovered.txt"), token);
            var fileResult = await repo.RestoreAsync(capture.Manifest.VersionId, recoveredFile, token);
            if (fileResult.Warnings.Count != 0) throw new InvalidOperationException(string.Join(";", fileResult.Warnings));
            Checks.Add("native A file entry recovery published with permission handover");
            var folder = (await repo.ListVersionsAsync(token)).Where(item => item.EntryKind == RepositoryEntryKind.Folder && item.SourcePath == sourceRoot)
                .OrderByDescending(item => item.CapturedAtUtc).First();
            var target = await WindowsCallerRecoveryTarget.CreateAsync(caller, Path.Combine(destination, "recovered-folder"), token);
            target.BeforePublication = async stage =>
            {
                await File.WriteAllTextAsync(Path.Combine(output, "caller-stage.json"), JsonSerializer.Serialize(new Stage(stage)), token);
                var resultPath = Path.Combine(output, "caller-attacks.json");
                while (!File.Exists(resultPath)) await Task.Delay(20, token);
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, token));
                if (result.RootElement.GetProperty("Error").ValueKind != JsonValueKind.Null || result.RootElement.GetProperty("Attacks").GetArrayLength() != 7)
                    throw new InvalidOperationException("Native attacker checks failed.");
                if (!Directory.EnumerateFileSystemEntries(stage).Select(Path.GetFileName).Order().SequenceEqual(new[] { "document.txt", "nested" })) throw new InvalidOperationException("Unexpected staged descendant.");
                Checks.Add("native A parent-delete/attribute/DACL/reparse/add-child/data attacks refused");
            };
            var restored = await repo.RestoreAsync(folder.VersionId, target, token);
            if (restored.Warnings.Count != 0) throw new InvalidOperationException(string.Join(";", restored.Warnings));
            Checks.Add("protected folder closes child handles then publishes under native A");
            using var cancelled = new CancellationTokenSource();
            var cancelledPath = Path.Combine(destination, "cancelled.txt");
            var cancelledTarget = await WindowsCallerRecoveryTarget.CreateAsync(caller, cancelledPath, token);
            cancelledTarget.BeforePublication = _ => { cancelled.Cancel(); return Task.CompletedTask; };
            try { await repo.RestoreAsync(capture.Manifest.VersionId, cancelledTarget, cancelled.Token); throw new InvalidOperationException("Cancelled recovery published."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            if (File.Exists(cancelledPath) || Directory.EnumerateFileSystemEntries(destination, ".FluxVault-recovery-*.tmp").Any()) throw new InvalidOperationException("Cancelled staging remains.");
            Checks.Add("native A cancellation removes exact protected staging and joins handles");
            var latePath = Path.Combine(destination, "late-user-file.txt");
            var lateTarget = await WindowsCallerRecoveryTarget.CreateAsync(caller, latePath, token);
            lateTarget.BeforePublication = _ => caller.RunAsCallerAsync(async () =>
            { await File.WriteAllTextAsync(latePath, "late user work", token); return true; });
            try { await repo.RestoreAsync(capture.Manifest.VersionId, lateTarget, token); throw new InvalidOperationException("Late user output was overwritten."); }
            catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 80 or 183 }) { }
            if (await File.ReadAllTextAsync(latePath, token) != "late user work" || Directory.EnumerateFileSystemEntries(destination, ".FluxVault-recovery-*.tmp").Any())
                throw new InvalidOperationException("Late destination rejection lost user bytes or staging remains.");
            Checks.Add("native A late destination refuses publication and preserves existing bytes");
            var small = await WindowsCallerRecoveryTarget.CreateAsync(caller, Path.Combine(destination, "many-small-files"), token);
            await using (small)
            {
                await small.PrepareAsync(RepositoryEntryKind.Folder, token);
                var weak = await SmallFiles(small, token);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                var retained = weak.Count(item => item.TryGetTarget(out _));
                if (retained > 2) throw new InvalidOperationException($"Disposed recovery stream buffers accumulated: {retained}/100 remain reachable.");
                var warnings = await small.PublishAsync(token);
                if (warnings.Count != 0) throw new InvalidOperationException(string.Join(";", warnings));
            }
            Checks.Add("native many-small-files recovery releases disposed streams and buffers");
            return FluxVaultIpcResponse.WithRestore(restored);
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference<Stream>>> SmallFiles(IRepositoryRestoreTarget target, CancellationToken token)
    {
        var weak = new List<WeakReference<Stream>>();
        for (var index = 0; index < 100; index++)
        {
            await using var stream = await target.CreateFileAsync($"file-{index:D3}.txt", token);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("small verified fixture file"), token);
            await target.FlushFileAsync(stream, token);
            weak.Add(new(stream));
        }
        return weak;
    }
    private static void Denied(Action action, string check, List<string> checks)
    {
        try { action(); } catch (UnauthorizedAccessException) { checks.Add(check); return; }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 5 or 32 || exception.InnerException is Win32Exception { NativeErrorCode: 5 or 32 }) { checks.Add(check); return; }
        throw new InvalidOperationException("Native mutation was not denied: " + check);
    }
    private static void ThrowLastError() => throw new IOException("Native denied open.", new Win32Exception(Marshal.GetLastPInvokeError()));
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes, uint mode, uint flags, IntPtr template);
}
