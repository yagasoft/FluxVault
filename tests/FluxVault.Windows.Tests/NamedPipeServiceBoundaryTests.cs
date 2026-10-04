using System.IO.Pipes;
using System.Runtime.Versioning;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Windows.Security;
using System.Security.Principal;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class NamedPipeServiceBoundaryTests
{
    [Theory]
    [InlineData("oversize")]
    [InlineData("depth")]
    [InlineData("claimed-identity")]
    public async Task Invalid_request_is_refused_before_dispatch(string kind)
    {
        var name = $"FluxVault.Tests.{Guid.NewGuid():N}";
        var handler = new ObservedHandler();
        var server = CreateServer(handler, name, kind == "depth" ? new FluxVaultIpcLimits { MaximumJsonDepth = 4 } : null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = server.RunAsync(lifetime.Token);
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, lifetime.Token);
            var payload = kind switch
            {
                "oversize" => "{\"command\":0,\"sourcePath\":\"" + new string('x', 1024 * 1024 + 1) + "\"}",
                "depth" => "{\"command\":0,\"configuration\":{\"watchedFolders\":[{\"includePatterns\":[\"*.docx\"]}]}}",
                _ => "{\"command\":0,\"ownerSid\":\"S-1-5-18\",\"isElevated\":true}"
            };
            using var reader = new StreamReader(client, leaveOpen: true);
            var receiving = reader.ReadLineAsync(lifetime.Token).AsTask();
            try { await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes(payload + "\n"), lifetime.Token); }
            catch (IOException) when (kind == "oversize") { }
            var response = await receiving;
            Assert.Equal(0, handler.Calls);
            if (response is not null) Assert.False(FluxVaultIpcSerializer.DeserializeResponse(response).Success);
        }
        finally
        {
            await lifetime.CancelAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Stop_joins_accepted_work_before_returning()
    {
        var name = $"FluxVault.Tests.{Guid.NewGuid():N}";
        var handler = new ObservedHandler(hold: true);
        var server = CreateServer(handler, name);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = server.RunAsync(lifetime.Token);
        using var identity = WindowsIdentity.GetCurrent();
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, identity.User!.Value));
        var sending = client.SendAsync(FluxVaultIpcRequest.GetStatus(), lifetime.Token);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await lifetime.CancelAsync();
            // This handler intentionally delays observing cancellation until released.
            await Task.Delay(100);
            Assert.False(serving.IsCompleted);
        }
        finally
        {
            handler.Release.TrySetResult();
            await lifetime.CancelAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(2));
            try { await sending; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Response_receipt_keeps_the_caller_alive_until_the_client_has_received_the_frame()
    {
        var name = $"FluxVault.Tests.{Guid.NewGuid():N}";
        var observed = new ObservedCallerProvider();
        var server = new NamedPipeFluxVaultServer(new ObservedHandler(),
            WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name), observed);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = server.RunAsync(lifetime.Token);
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            using var client = await WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, identity.User!.Value).ConnectAsync(lifetime.Token);
            using var reader = new StreamReader(client, leaveOpen: true);
            var receiving = reader.ReadLineAsync(lifetime.Token).AsTask();
            await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes(FluxVaultIpcSerializer.SerializeRequest(FluxVaultIpcRequest.GetStatus()) + "\n"), lifetime.Token);
            Assert.True(FluxVaultIpcSerializer.DeserializeResponse((await receiving)!).Success);
            await Task.Delay(100, lifetime.Token);
            Assert.False(observed.Disposed.Task.IsCompleted);
            await client.WriteAsync(new byte[] { 6 }, lifetime.Token);
            await observed.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
        }
        finally { await lifetime.CancelAsync(); await serving.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Theory]
    [InlineData("no-frame")]
    [InlineData("truncated")]
    [InlineData("no-receipt")]
    public async Task Broken_peer_does_not_release_the_anchor_or_stop_later_requests(string kind)
    {
        var name = $"FluxVault.Tests.{Guid.NewGuid():N}";
        var handler = new ObservedHandler();
        var limits = new FluxVaultIpcLimits { PendingListeners = 1, MaximumConcurrentRequests = 1 };
        var server = new NamedPipeFluxVaultServer(handler,
            WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name), new WindowsFluxVaultCallerContextProvider(), limits);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = server.RunAsync(lifetime.Token);
        using var identity = WindowsIdentity.GetCurrent();
        var factory = WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, identity.User!.Value);
        try
        {
            using (var broken = await factory.ConnectAsync(lifetime.Token))
            {
                if (kind == "truncated") await broken.WriteAsync(new byte[] { (byte)'{' }, lifetime.Token);
                if (kind == "no-receipt")
                {
                    using var reader = new StreamReader(broken, leaveOpen: true);
                    var receiving = reader.ReadLineAsync(lifetime.Token).AsTask();
                    await broken.WriteAsync(System.Text.Encoding.UTF8.GetBytes(FluxVaultIpcSerializer.SerializeRequest(FluxVaultIpcRequest.GetStatus()) + "\n"), lifetime.Token);
                    Assert.True(FluxVaultIpcSerializer.DeserializeResponse((await receiving)!).Success);
                }
            }
            // This factory cannot attach if a previous connection has lost the name anchor.
            var response = await new NamedPipeFluxVaultClient(factory, limits).SendAsync(FluxVaultIpcRequest.GetStatus(), lifetime.Token);
            Assert.True(response.Success);
            Assert.False(serving.IsCompleted);
            Assert.Equal(kind == "no-receipt" ? 2 : 1, handler.Calls);
        }
        finally { await lifetime.CancelAsync(); await serving.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private static NamedPipeFluxVaultServer CreateServer(ObservedHandler handler, string name, FluxVaultIpcLimits? limits = null) =>
        new(handler, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name), new WindowsFluxVaultCallerContextProvider(), limits);

    private sealed class ObservedCallerProvider : IFluxVaultCallerContextProvider
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FluxVaultCallerContext Capture(NamedPipeServerStream connectedPipe) =>
            new ObservedCaller(new WindowsFluxVaultCallerContextProvider().Capture(connectedPipe), Disposed);
    }

    private sealed class ObservedCaller(FluxVaultCallerContext inner, TaskCompletionSource disposed) : FluxVaultCallerContext
    {
        public override string UserSid => inner.UserSid;
        public override IReadOnlySet<string> EnabledGroupSids => inner.EnabledGroupSids;
        public override bool IsElevated => inner.IsElevated;
        public override bool ImpersonationPermitted => inner.ImpersonationPermitted;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => inner.RunAsCallerAsync(action);
        public override void Dispose() { inner.Dispose(); disposed.TrySetResult(); }
    }

    private sealed class ObservedHandler(bool hold = false) : IAuthenticatedFluxVaultRequestHandler
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (hold) await Release.Task;
            return FluxVaultIpcResponse.Ok();
        }
    }
}
