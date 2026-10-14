using System.Net.Sockets;
using FluxVault.Core.Security;
using FluxVault.Service;
using Npgsql;

namespace FluxVault.Integration.Tests;

public sealed class ServiceDependencyStartupTests
{
    private static readonly ServiceStartupPolicy Fast = new() { RetryDelay = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task Delayed_database_readiness_recovers_after_more_than_the_SCM_attempt_limit()
    {
        var attempts = 0;
        var warnings = new List<Exception>();
        var expected = new object();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var actual = await ServiceDependencyStartup.OpenAsync(_ =>
        {
            if (++attempts <= 4) throw new NpgsqlException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
            return Task.FromResult(expected);
        }, warnings.Add, Fast, timeout.Token);
        Assert.Same(expected, actual);
        Assert.Equal(5, attempts);
        Assert.Equal(4, warnings.Count);
    }

    [Fact]
    public async Task Cancellation_interrupts_dependency_wait_without_another_attempt()
    {
        using var stop = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var opening = ServiceDependencyStartup.OpenAsync<object>(_ =>
        {
            attempts++;
            throw new NpgsqlException("Not ready", new SocketException((int)SocketError.ConnectionRefused));
        }, _ => waiting.TrySetResult(), new() { RetryDelay = TimeSpan.FromSeconds(30) }, stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(opening.IsCompleted);
        }
        finally { await stop.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("57P03")]
    [InlineData("57P01")]
    [InlineData("57P02")]
    [InlineData("53300")]
    public async Task Temporary_database_startup_or_connection_capacity_refusal_can_recover(string code)
    {
        var attempts = 0;
        var result = await ServiceDependencyStartup.OpenAsync(_ =>
        {
            if (++attempts == 1) throw new PostgresException("Temporarily unavailable", "FATAL", "FATAL", code);
            return Task.FromResult(1);
        }, _ => { }, Fast, CancellationToken.None);
        Assert.Equal(1, result);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("28P01")]
    [InlineData("28000")]
    [InlineData("42501")]
    [InlineData("3D000")]
    [InlineData("XX001")]
    [InlineData("42P01")]
    public async Task Authentication_schema_and_integrity_refusals_are_not_retried(string code)
    {
        var attempts = 0;
        var failure = new PostgresException("Refused", "FATAL", "FATAL", code);
        var actual = await Assert.ThrowsAsync<PostgresException>(() => ServiceDependencyStartup.OpenAsync<object>(_ =>
        { attempts++; throw failure; }, _ => throw new InvalidOperationException("Unsafe retry"), Fast, CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Identity_and_ownership_refusals_are_not_retried()
    {
        foreach (var failure in new Exception[] { new UnauthorizedAccessException(), new InvalidDataException(),
                     new VaultCatalogueException(VaultCatalogueFailure.IdentityMismatch), new AutomaticProtectionUnavailableException("Another owner") })
        {
            var attempts = 0;
            var actual = await Record.ExceptionAsync(() => ServiceDependencyStartup.OpenAsync<object>(_ =>
            { attempts++; throw failure; }, _ => throw new InvalidOperationException("Unsafe retry"), Fast, CancellationToken.None));
            Assert.Same(failure, actual);
            Assert.Equal(1, attempts);
        }
    }
}
