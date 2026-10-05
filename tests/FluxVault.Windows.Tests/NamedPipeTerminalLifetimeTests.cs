using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class NamedPipeTerminalLifetimeTests
{
    [Fact]
    public async Task Terminal_handler_keeps_anchor_and_native_caller_until_response_receipt_then_joins()
    {
        await using var fixture = new Fixture(TimeSpan.FromSeconds(3));
        using var peer = await fixture.ConnectAsync();
        await SendAsync(peer, fixture.Handler.Handshake, fixture.Token);
        using var reader = new StreamReader(peer, leaveOpen: true);
        Assert.True(FluxVaultIpcSerializer.DeserializeResponse((await reader.ReadLineAsync(fixture.Token))!).Success);
        Assert.False(fixture.Serving.IsCompleted);
        Assert.False(fixture.Callers.Disposed.Task.IsCompleted);
        Assert.Throws<IOException>(() => WindowsFluxVaultPipeServerFactory.ForPrivateFixture(fixture.Name).CreateFirstListener());
        await peer.WriteAsync(new byte[] { 6 }, fixture.Token);
        await fixture.Serving.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.AssertRetiredAsync();
        using var replacement = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(fixture.Name).CreateFirstListener();
    }

    [Fact]
    public async Task Missing_receipt_releases_terminal_host_at_the_bounded_deadline_without_peer_disconnect()
    {
        await using var fixture = new Fixture(TimeSpan.FromMilliseconds(300));
        using var peer = await fixture.ConnectAsync();
        await SendAsync(peer, fixture.Handler.Handshake, fixture.Token);
        using var reader = new StreamReader(peer, leaveOpen: true);
        Assert.True(FluxVaultIpcSerializer.DeserializeResponse((await reader.ReadLineAsync(fixture.Token))!).Success);
        await fixture.Serving.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.AssertRetiredAsync();
        using var replacement = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(fixture.Name).CreateFirstListener();
    }

    [Fact]
    public async Task Denied_or_malformed_requests_do_not_end_the_host_before_the_authorised_attempt()
    {
        await using var fixture = new Fixture(TimeSpan.FromMilliseconds(300));
        using (var peer = await fixture.ConnectAsync())
        {
            await peer.WriteAsync(Encoding.UTF8.GetBytes("{invalid}\n"), fixture.Token);
            using var reader = new StreamReader(peer, leaveOpen: true);
            Assert.False(FluxVaultIpcSerializer.DeserializeResponse((await reader.ReadLineAsync(fixture.Token))!).Success);
            await peer.WriteAsync(new byte[] { 6 }, fixture.Token);
        }
        Assert.False((await fixture.Client.SendAsync(FluxVaultIpcRequest.GetStatus(), fixture.Token)).Success);
        Assert.False(fixture.Serving.IsCompleted);
        Assert.False(fixture.Handler.Terminal);
        Assert.True((await fixture.Client.SendAsync(fixture.Handler.Handshake, fixture.Token)).Success);
        await fixture.Serving.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.AssertRetiredAsync();
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(1, 2)]
    public void Terminal_transport_requires_serial_admission_and_dispatch(int pending, int concurrency)
    {
        var name = $"FluxVault.Tests.{Guid.NewGuid():N}";
        Assert.Throws<ArgumentException>(() => new NamedPipeFluxVaultServer(new Handler(),
            WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name), new WindowsFluxVaultCallerContextProvider(),
            new() { PendingListeners = pending, MaximumConcurrentRequests = concurrency }, stopAfterConnection: () => true));
    }

    private static async Task SendAsync(Stream peer, FluxVaultIpcRequest request, CancellationToken token) =>
        await peer.WriteAsync(Encoding.UTF8.GetBytes(FluxVaultIpcSerializer.SerializeRequest(request) + "\n"), token);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        private readonly IFluxVaultPipeClientFactory clientFactory;
        internal string Name { get; } = $"FluxVault.Tests.{Guid.NewGuid():N}";
        internal Handler Handler { get; } = new();
        internal Provider Callers { get; } = new();
        internal IFluxVaultPipeServerFactory ServerFactory { get; }
        internal NamedPipeFluxVaultClient Client { get; }
        internal Task Serving { get; }
        internal CancellationToken Token => lifetime.Token;

        internal Fixture(TimeSpan receiptDeadline)
        {
            using var identity = WindowsIdentity.GetCurrent();
            clientFactory = WindowsFluxVaultPipeClientFactory.ForPrivateFixture(Name, identity.User!.Value);
            var limits = new FluxVaultIpcLimits { PendingListeners = 1, MaximumConcurrentRequests = 1,
                FrameReadTimeout = TimeSpan.FromSeconds(1), FrameWriteTimeout = receiptDeadline };
            ServerFactory = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(Name);
            Client = new(clientFactory, limits);
            Serving = new NamedPipeFluxVaultServer(Handler, ServerFactory, Callers, limits,
                stopAfterConnection: () => Handler.Terminal).RunAsync(Token);
        }
        internal Task<Stream> ConnectAsync() => clientFactory.ConnectAsync(Token);
        internal async Task AssertRetiredAsync()
        {
            Assert.True(Callers.Disposed.Task.IsCompletedSuccessfully);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Callers.Last!.RunAsCallerAsync(() => Task.FromResult(true)));
        }
        public async ValueTask DisposeAsync()
        {
            try { await lifetime.CancelAsync(); await Serving.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { lifetime.Dispose(); }
        }
    }

    private sealed class Handler : IAuthenticatedFluxVaultRequestHandler
    {
        internal FluxVaultIpcRequest Handshake { get; } = FluxVaultIpcRequest.GetStatus() with { OperationId = Guid.NewGuid() };
        internal bool Terminal { get; private set; }
        internal int Calls { get; private set; }
        public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token)
        {
            Calls++;
            Assert.Equal(caller.UserSid, await caller.RunAsCallerAsync(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                return Task.FromResult(identity.User!.Value);
            }));
            if (request != Handshake) throw new UnauthorizedAccessException();
            Terminal = true;
            return FluxVaultIpcResponse.Ok();
        }
    }

    private sealed class Provider : IFluxVaultCallerContextProvider
    {
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Caller? Last { get; private set; }
        public FluxVaultCallerContext Capture(NamedPipeServerStream pipe) =>
            Last = new(new WindowsFluxVaultCallerContextProvider().Capture(pipe), Disposed);
    }
    private sealed class Caller(FluxVaultCallerContext inner, TaskCompletionSource disposed) : FluxVaultCallerContext
    {
        public override string UserSid => inner.UserSid;
        public override IReadOnlySet<string> EnabledGroupSids => inner.EnabledGroupSids;
        public override bool IsElevated => inner.IsElevated;
        public override bool ImpersonationPermitted => inner.ImpersonationPermitted;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => inner.RunAsCallerAsync(action);
        public override void Dispose() { inner.Dispose(); disposed.TrySetResult(); }
    }
}
