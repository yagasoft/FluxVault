using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Windows.Security;

namespace FluxVault.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsPipeBoundaryTests
{
    [Theory]
    [InlineData("FluxVault.Service")]
    [InlineData("FluxVault.Tests.not-a-guid")]
    [InlineData("remote\\FluxVault.Tests.01234567890123456789012345678901")]
    public void Private_fixture_cannot_open_an_installed_or_unbounded_name(string name)
    {
        Assert.Throws<ArgumentException>(() => WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name));
        Assert.Throws<ArgumentException>(() => WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, CurrentSid()));
    }

    [Fact]
    public void First_listener_refuses_an_existing_server()
    {
        var name = NewName();
        using var impostor = new NamedPipeServerStream(name, PipeDirection.InOut, 32,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var factory = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name);
        Assert.Throws<IOException>(() => factory.CreateFirstListener());
    }

    [Fact]
    public async Task Connected_handle_verification_precedes_any_request_bytes()
    {
        var name = NewName();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        // The wrong fixed SID cannot be replaced by the process's actual identity.
        var client = WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectAsync(deadline.Token));
        await accepting;
        var received = await server.ReadAsync(new byte[1], deadline.Token);
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task Client_refuses_a_pipe_that_allows_untrusted_instance_creation()
    {
        var name = NewName();
        var security = new PipeSecurity();
        security.SetOwner(new SecurityIdentifier(CurrentSid()));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(CurrentSid()),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 32,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        var client = WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, CurrentSid());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectAsync(deadline.Token));
        await accepting;
        Assert.Equal(0, await server.ReadAsync(new byte[1], deadline.Token));
    }

    [Fact]
    public async Task Served_anchor_is_reusable_and_retains_the_name_between_requests()
    {
        var name = NewName();
        var factory = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name);
        using var anchor = factory.CreateFirstListener();
        var originalHandle = anchor.SafePipeHandle.DangerousGetHandle();
        var clientFactory = WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, CurrentSid());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 3; index++)
        {
            var accepting = anchor.WaitForConnectionAsync(deadline.Token);
            using var client = await clientFactory.ConnectAsync(deadline.Token);
            await accepting;
            var buffer = new byte[1];
            var receiving = anchor.ReadAsync(buffer, deadline.Token).AsTask();
            await client.WriteAsync(new byte[] { (byte)index }, deadline.Token);
            Assert.Equal(1, await receiving);
            Assert.Equal(index, buffer[0]);
            receiving = client.ReadAsync(buffer, deadline.Token).AsTask();
            await anchor.WriteAsync(new byte[] { 42 }, deadline.Token);
            Assert.Equal(1, await receiving);
            Assert.Equal(42, buffer[0]);
            anchor.Disconnect();
            Assert.Equal(originalHandle, anchor.SafePipeHandle.DangerousGetHandle());
            // Additional listeners may come and go, while the served anchor owns the name.
            using (factory.CreateAdditionalListener()) { }
            Assert.Throws<IOException>(() => WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name).CreateFirstListener());
        }
    }

    [Fact]
    public async Task Captured_caller_token_outlives_the_connection_and_flows_across_await()
    {
        var name = NewName();
        using var server = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name).CreateFirstListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        using var client = await WindowsFluxVaultPipeClientFactory.ForPrivateFixture(name, CurrentSid()).ConnectAsync(deadline.Token);
        await accepting;
        var receiving = server.ReadAsync(new byte[1], deadline.Token).AsTask();
        await client.WriteAsync(new byte[] { 1 }, deadline.Token);
        Assert.Equal(1, await receiving);
        using var caller = new WindowsFluxVaultCallerContextProvider().Capture(server);
        server.Disconnect();
        client.Dispose();
        Assert.Equal(CurrentSid(), caller.UserSid);
        Assert.True(caller.ImpersonationPermitted);
        var actual = await caller.RunAsCallerAsync(async () =>
        {
            await Task.Yield();
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            return identity?.User?.Value;
        });
        Assert.Equal(CurrentSid(), actual);
        caller.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.RunAsCallerAsync(() => Task.FromResult(0)));
    }

    [Fact]
    public async Task Identification_only_client_is_refused_instead_of_using_server_identity()
    {
        var name = NewName();
        using var server = WindowsFluxVaultPipeServerFactory.ForPrivateFixture(name).CreateFirstListener();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(2000, deadline.Token);
        await accepting;
        var receiving = server.ReadAsync(new byte[1], deadline.Token).AsTask();
        await client.WriteAsync(new byte[] { 1 }, deadline.Token);
        Assert.Equal(1, await receiving);
        Assert.Throws<UnauthorizedAccessException>(() => new WindowsFluxVaultCallerContextProvider().Capture(server));
    }

    private static string NewName() => $"FluxVault.Tests.{Guid.NewGuid():N}";
    private static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }
}
