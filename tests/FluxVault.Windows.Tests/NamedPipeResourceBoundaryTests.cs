using System.Collections.Concurrent;
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
public sealed class NamedPipeResourceBoundaryTests
{
    [Fact]
    public async Task Stalled_frames_release_saturated_capacity_without_dispatch_and_valid_work_completes()
    {
        var handler = new Handler();
        await using var fixture = new Fixture(handler);
        using var first = await fixture.ConnectAsync();
        using var second = await fixture.ConnectAsync();
        await WriteAsync(first, "{");
        await WriteAsync(second, "{");

        var response = await fixture.Client.SendAsync(FluxVaultIpcRequest.GetStatus(), fixture.Token)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(response.Success);
        Assert.Equal(1, handler.Calls);
        Assert.Single(fixture.Callers.Captured);
        await fixture.Callers.Captured.Single().Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await AssertTokenRetiredAsync(fixture.Callers.Captured.Single());
    }

    [Fact]
    public async Task Active_requests_cannot_exceed_capacity_and_queued_work_runs_when_one_finishes()
    {
        var handler = new Handler(holdFirstTwo: true);
        await using var fixture = new Fixture(handler);
        var first = fixture.Client.SendAsync(FluxVaultIpcRequest.GetStatus(), fixture.Token);
        var second = fixture.Client.SendAsync(FluxVaultIpcRequest.GetStatus(), fixture.Token);
        Task<FluxVaultIpcResponse>? queued = null;
        try
        {
            await handler.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queued = fixture.Client.SendAsync(FluxVaultIpcRequest.GetStatus(), fixture.Token);
            Assert.NotSame(queued, await Task.WhenAny(queued, Task.Delay(150, fixture.Token)));
            Assert.Equal(2, handler.Calls);
            handler.FirstRelease.TrySetResult();
            Assert.True((await queued.WaitAsync(TimeSpan.FromSeconds(2))).Success);
            Assert.Equal(3, handler.Calls);
            Assert.Equal(2, handler.MaximumActive);
            Assert.False(handler.SecondRelease.Task.IsCompleted);
        }
        finally
        {
            handler.FirstRelease.TrySetResult();
            handler.SecondRelease.TrySetResult();
            // Join every started client even if an earlier one or the server faults.
            try { await fixture.StopAsync(); }
            finally
            {
                var clients = queued is null ? new[] { first, second } : new[] { first, second, queued };
                try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (OperationCanceledException) when (fixture.Token.IsCancellationRequested) { }
                catch (IOException) when (fixture.Token.IsCancellationRequested) { }
            }
        }
    }

    [Fact]
    public async Task Slow_input_has_an_absolute_frame_deadline_and_never_captures_a_caller()
    {
        var handler = new Handler();
        await using var fixture = new Fixture(handler, capacity: 1);
        using var peer = await fixture.ConnectAsync();
        using var dribbleStop = new CancellationTokenSource();
        var sending = DribbleAsync(peer, dribbleStop.Token);
        try
        {
            using var reader = new StreamReader(peer, leaveOpen: true);
            var line = await reader.ReadLineAsync(fixture.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(line);
            Assert.False(FluxVaultIpcSerializer.DeserializeResponse(line).Success);
            Assert.Equal(0, handler.Calls);
            Assert.Empty(fixture.Callers.Captured);
            Assert.True((await fixture.Client.SendAsync(FluxVaultIpcRequest.GetStatus(), fixture.Token)
                .WaitAsync(TimeSpan.FromSeconds(2))).Success);
        }
        finally
        {
            await dribbleStop.CancelAsync();
            await sending.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stalled_response_or_missing_receipt_retires_native_token_and_allows_later_work(bool readFrame)
    {
        var handler = new Handler(largeFirstResponse: !readFrame);
        await using var fixture = new Fixture(handler, capacity: 1);
        using var peer = await fixture.ConnectAsync();
        await WriteAsync(peer, FluxVaultIpcSerializer.SerializeRequest(FluxVaultIpcRequest.GetStatus()) + "\n");
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (readFrame)
        {
            using var reader = new StreamReader(peer, leaveOpen: true);
            Assert.True(FluxVaultIpcSerializer.DeserializeResponse((await reader.ReadLineAsync(fixture.Token))!).Success);
            // Deliberately omit the protocol's response receipt and keep the pipe open.
        }
        var caller = fixture.Callers.Captured.Single();
        await caller.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await AssertTokenRetiredAsync(caller);
        Assert.True((await fixture.Client.SendAsync(FluxVaultIpcRequest.GetStatus(), fixture.Token)
            .WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Shutdown_joins_stalled_native_read_and_write_without_waiting_for_peer_disconnect()
    {
        var handler = new Handler(largeFirstResponse: true);
        // A long finite frame deadline makes shutdown cancellation the tested release.
        await using var fixture = new Fixture(handler, frameTimeout: TimeSpan.FromSeconds(5));
        using var reading = await fixture.ConnectAsync();
        using var writing = await fixture.ConnectAsync();
        await WriteAsync(reading, "{");
        await WriteAsync(writing, FluxVaultIpcSerializer.SerializeRequest(FluxVaultIpcRequest.GetStatus()) + "\n");
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Observe native output before cancelling; the peer consumes no further bytes.
        Assert.Equal(1, await writing.ReadAsync(new byte[1], fixture.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, handler.Calls);
        var caller = fixture.Callers.Captured.Single();
        Assert.True(caller.Disposed.Task.IsCompletedSuccessfully);
        await AssertTokenRetiredAsync(caller);
    }

    private static async Task AssertTokenRetiredAsync(ObservedCaller caller) =>
        await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.RunAsCallerAsync(() => Task.FromResult(true)));

    private static async Task WriteAsync(Stream stream, string text) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));

    private static async Task DribbleAsync(Stream stream, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await stream.WriteAsync(new byte[] { (byte)' ' }, token);
                await Task.Delay(50, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(12));
        private readonly Task serving;
        private readonly IFluxVaultPipeClientFactory factory;
        internal ObservedProvider Callers { get; } = new();
        internal CancellationToken Token => lifetime.Token;
        internal NamedPipeFluxVaultClient Client { get; }

        internal Fixture(Handler handler, int capacity = 2, TimeSpan? frameTimeout = null)
        {
            var name = $"FluxVault.Tests.{Guid.NewGuid():N}";
            using var identity = WindowsIdentity.GetCurrent();
            factory = WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, identity.User!.Value);
            var limits = new FluxVaultIpcLimits
            {
                MaximumConcurrentRequests = capacity, PendingListeners = capacity,
                FrameReadTimeout = frameTimeout ?? TimeSpan.FromMilliseconds(350),
                FrameWriteTimeout = frameTimeout ?? TimeSpan.FromMilliseconds(250)
            };
            Client = new(factory, limits);
            serving = new NamedPipeFluxVaultServer(handler, WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name), Callers, limits)
                .RunAsync(Token);
        }

        internal Task<Stream> ConnectAsync() => factory.ConnectAsync(Token);
        internal async Task StopAsync()
        {
            await lifetime.CancelAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var caller in Callers.Captured)
            {
                Assert.True(caller.Disposed.Task.IsCompletedSuccessfully);
                await AssertTokenRetiredAsync(caller);
            }
        }
        public async ValueTask DisposeAsync() { try { await StopAsync(); } finally { lifetime.Dispose(); } }
    }

    private sealed class ObservedProvider : IFluxVaultCallerContextProvider
    {
        internal ConcurrentBag<ObservedCaller> Captured { get; } = [];
        public FluxVaultCallerContext Capture(NamedPipeServerStream pipe)
        {
            var caller = new ObservedCaller(new WindowsFluxVaultCallerContextProvider().Capture(pipe));
            Captured.Add(caller);
            return caller;
        }
    }

    private sealed class ObservedCaller(FluxVaultCallerContext inner) : FluxVaultCallerContext
    {
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override string UserSid => inner.UserSid;
        public override IReadOnlySet<string> EnabledGroupSids => inner.EnabledGroupSids;
        public override bool IsElevated => inner.IsElevated;
        public override bool ImpersonationPermitted => inner.ImpersonationPermitted;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => inner.RunAsCallerAsync(action);
        public override void Dispose() { inner.Dispose(); Disposed.TrySetResult(); }
    }

    private sealed class Handler(bool holdFirstTwo = false, bool largeFirstResponse = false) : IAuthenticatedFluxVaultRequestHandler
    {
        private int calls;
        private int active;
        private int maximumActive;
        internal int Calls => Volatile.Read(ref calls);
        internal int MaximumActive => Volatile.Read(ref maximumActive);
        internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref calls);
            var count = Interlocked.Increment(ref active);
            int prior;
            do { prior = Volatile.Read(ref maximumActive); }
            while (count > prior && Interlocked.CompareExchange(ref maximumActive, count, prior) != prior);
            try
            {
                // Prove this is the retained native context, before checking its retirement.
                Assert.Equal(caller.UserSid, await caller.RunAsCallerAsync(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    return Task.FromResult(identity.User!.Value);
                }));
                FirstStarted.TrySetResult();
                if (call == 2) TwoStarted.TrySetResult();
                if (holdFirstTwo && call <= 2)
                    await (call == 1 ? FirstRelease : SecondRelease).Task.WaitAsync(token);
                return largeFirstResponse && call == 1
                    ? FluxVaultIpcResponse.Failure(new string('x', 2 * 1024 * 1024))
                    : FluxVaultIpcResponse.Ok();
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
