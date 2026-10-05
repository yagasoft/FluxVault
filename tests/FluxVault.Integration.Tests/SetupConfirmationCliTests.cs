using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Cli;
using FluxVault.Core.Ipc;

namespace FluxVault.Integration.Tests;

public sealed class SetupConfirmationCliTests
{
    private static readonly Guid Instance = Guid.Parse("7871ff7f-8d1b-404d-b207-71f2e742364f");

    public static IEnumerable<object[]> InvalidArguments()
    {
        yield return [new[] { "setup-confirm" }];
        yield return [new[] { "setup-confirm", "--instance" }];
        foreach (var id in new[] { "invalid", Guid.Empty.ToString("N"), Instance.ToString("D"), Instance.ToString("N").ToUpperInvariant() })
            yield return [new[] { "setup-confirm", "--instance", id }];
        foreach (var option in new[] { "owner", "repository", "pipe", "config", "profile" })
            yield return [new[] { "setup-confirm", "--instance", Instance.ToString("N"), "--" + option, "untrusted" }];
        foreach (var seconds in new[] { "0", "601", "-1", "1.5", "999999999999", "+1" })
            yield return [new[] { "setup-confirm", "--instance", Instance.ToString("N"), "--timeout-seconds", seconds }];
        yield return [new[] { "setup-confirm", "--instance", Instance.ToString("N"), "--instance", Instance.ToString("N") }];
        yield return [new[] { "setup-confirm", "--instance", Instance.ToString("N"), "--timeout-seconds", "1", "--timeout-seconds", "2" }];
        yield return [new[] { "setup-confirm", "--instance", Instance.ToString("N"), "unexpected" }];
    }

    [Theory, MemberData(nameof(InvalidArguments))]
    public async Task Invalid_intent_never_opens_a_client(string[] arguments)
    {
        var opens = 0;
        var result = await Run(arguments, () => { opens++; throw new InvalidOperationException("Must not open."); });
        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.StandardError);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task Confirmation_dispatches_only_the_exact_correlated_handshake_and_reports_the_confirmed_identity()
    {
        var vault = VaultId.New();
        var client = new Client((request, token) => Task.FromResult(Success(vault)));
        var result = await Run(Arguments(), () => client);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains(vault.ToString(), result.StandardOutput);
        Assert.Contains(Instance.ToString("N"), result.StandardOutput);
        Assert.Equal(FluxVaultIpcRequest.GetStatus() with { OperationId = Instance }, Assert.Single(client.Requests));
        Assert.True(client.CancellationToken.CanBeCanceled);
    }

    [Theory]
    [InlineData("uncorrelated")]
    [InlineData("missing-operation")]
    [InlineData("missing-vault")]
    [InlineData("empty-vault")]
    [InlineData("missing-revision")]
    [InlineData("wrong-revision")]
    [InlineData("denied")]
    [InlineData("contradictory-error")]
    [InlineData("contradictory-message")]
    public async Task An_incomplete_or_failed_reply_never_confirms_setup_or_retries(string kind)
    {
        var response = kind switch
        {
            "uncorrelated" => Success(VaultId.New()) with { OperationId = Guid.NewGuid() },
            "missing-operation" => Success(VaultId.New()) with { OperationId = null },
            "missing-vault" => Success(VaultId.New()) with { VaultId = null },
            "empty-vault" => Success(VaultId.New()) with { VaultId = default(VaultId) },
            "missing-revision" => Success(VaultId.New()) with { VaultRevision = null },
            "wrong-revision" => Success(VaultId.New()) with { VaultRevision = 2 },
            "contradictory-error" => Success(VaultId.New()) with { ErrorCode = FluxVaultIpcErrorCode.Unavailable },
            "contradictory-message" => Success(VaultId.New()) with { ErrorMessage = "Failed" },
            _ => FluxVaultIpcResponse.Failure("Private diagnostic") with { ErrorCode = FluxVaultIpcErrorCode.Denied }
        };
        var client = new Client((request, token) => Task.FromResult(response));
        var result = await Run(Arguments(), () => client);
        AssertUnconfirmed(result);
        Assert.Single(client.Requests);
        Assert.DoesNotContain("Private diagnostic", result.StandardError);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("denied")]
    [InlineData("json")]
    public async Task Transport_or_peer_verification_failure_is_uncertain_and_never_retried(string kind)
    {
        Exception failure = kind switch
        {
            "io" => new IOException("Private path"),
            "timeout" => new TimeoutException("Private path"),
            "denied" => new UnauthorizedAccessException("Private SID"),
            _ => new System.Text.Json.JsonException("Private payload")
        };
        var client = new Client((request, token) => Task.FromException<FluxVaultIpcResponse>(failure));
        AssertUnconfirmed(await Run(Arguments(), () => client));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Pre_cancelled_confirmation_never_opens_a_client()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var opens = 0;
        AssertUnconfirmed(await Run(Arguments(), () => { opens++; return new Client((r, t) => Task.FromResult(Success(VaultId.New()))); }, cancellation.Token));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task Cancellation_after_dispatch_joins_the_request_and_does_not_retry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        var client = new Client(async (request, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return Success(VaultId.New()); }
            finally { exited = true; }
        });
        using var cancellation = new CancellationTokenSource();
        var running = Run(Arguments(), () => client, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        AssertUnconfirmed(await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(exited);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Explicit_deadline_bounds_a_live_request_and_joins_it()
    {
        var exited = false;
        var client = new Client(async (request, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return Success(VaultId.New()); }
            finally { exited = true; }
        });
        var result = await Run([.. Arguments(), "--timeout-seconds", "1"], () => client).WaitAsync(TimeSpan.FromSeconds(5));
        AssertUnconfirmed(result);
        Assert.True(exited);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task A_complete_response_remains_authoritative_if_cancellation_arrives_after_completion()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Client((request, token) => { cancellation.Cancel(); return Task.FromResult(Success(VaultId.New())); });
        var result = await Run(Arguments(), () => client, cancellation.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(client.Requests);
    }

    private static string[] Arguments() => ["setup-confirm", "--instance", Instance.ToString("N")];
    private static FluxVaultIpcResponse Success(VaultId vault) => FluxVaultIpcResponse.Ok() with { OperationId = Instance, VaultId = vault, VaultRevision = 1 };
    private static void AssertUnconfirmed(CliResult result)
    {
        Assert.Equal(3, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("not confirmed", result.StandardError);
        Assert.Contains("bootstrap", result.StandardError);
        Assert.Contains("authorised status", result.StandardError);
        Assert.Contains("Do not retry", result.StandardError);
    }
    private static async Task<CliResult> Run(string[] arguments, Func<IFluxVaultServiceClient> factory, CancellationToken cancellationToken = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await FluxVaultCli.RunAsync(arguments, output, error, factory, cancellationToken);
        return new(exit, output.ToString(), error.ToString());
    }
    private sealed class Client(Func<FluxVaultIpcRequest, CancellationToken, Task<FluxVaultIpcResponse>> send) : IFluxVaultServiceClient
    {
        public List<FluxVaultIpcRequest> Requests { get; } = [];
        public CancellationToken CancellationToken { get; private set; }
        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            CancellationToken = cancellationToken;
            return send(request, cancellationToken);
        }
    }
}
