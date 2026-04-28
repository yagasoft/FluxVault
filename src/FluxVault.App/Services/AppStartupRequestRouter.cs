using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.App.Services;

public enum AppStartupRequestAction
{
    Activate = 0,
    ShowVersions = 1,
    AddToFluxVault = 2,
    RemoveFromFluxVault = 3
}

public sealed record AppStartupRequest
{
    [JsonConstructor]
    public AppStartupRequest(AppStartupRequestAction action, string? path)
    {
        Action = action;
        Path = path;
    }

    public AppStartupRequest(string? restorePath)
        : this(string.IsNullOrWhiteSpace(restorePath)
            ? AppStartupRequestAction.Activate
            : AppStartupRequestAction.ShowVersions, restorePath)
    {
    }

    public AppStartupRequestAction Action { get; init; }

    public string? Path { get; init; }

    public string? RestorePath => Action == AppStartupRequestAction.ShowVersions ? Path : null;

    public static AppStartupRequest Parse(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var action = args[index].ToLowerInvariant() switch
            {
                "--restore-path" => AppStartupRequestAction.ShowVersions,
                "--show-versions" => AppStartupRequestAction.ShowVersions,
                "--add-path" => AppStartupRequestAction.AddToFluxVault,
                "--remove-path" => AppStartupRequestAction.RemoveFromFluxVault,
                _ => AppStartupRequestAction.Activate
            };

            if (action != AppStartupRequestAction.Activate
                && index + 1 < args.Count
                && !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return new AppStartupRequest(action, args[index + 1]);
            }
        }

        return new AppStartupRequest(AppStartupRequestAction.Activate, path: null);
    }
}

public interface IAppStartupRequestRouter : IAsyncDisposable
{
    bool IsPrimaryInstance { get; }

    Task StartListeningAsync(Func<AppStartupRequest, Task> handler, CancellationToken cancellationToken = default);

    Task<bool> TryForwardToExistingInstanceAsync(AppStartupRequest request, CancellationToken cancellationToken = default);
}

public sealed class AppStartupRequestRouter : IAppStartupRequestRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Mutex instanceMutex;
    private readonly string pipeName;
    private CancellationTokenSource? listenerCancellation;
    private Task? listenerTask;
    private bool disposed;

    public AppStartupRequestRouter(string instanceKey = "FluxVault.App")
    {
        pipeName = $"FluxVault.App.Startup.{instanceKey}";
        instanceMutex = new Mutex(initiallyOwned: true, $"Local\\FluxVault.App.{instanceKey}", out var isPrimaryInstance);
        IsPrimaryInstance = isPrimaryInstance;
    }

    public bool IsPrimaryInstance { get; }

    public Task StartListeningAsync(Func<AppStartupRequest, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsPrimaryInstance)
        {
            throw new InvalidOperationException("Only the primary instance can listen for startup requests.");
        }

        if (listenerTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listenerTask = Task.Run(() => ListenAsync(handler, listenerCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<bool> TryForwardToExistingInstanceAsync(
        AppStartupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (IsPrimaryInstance)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(pipe, request, JsonOptions, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        listenerCancellation?.Cancel();
        if (listenerTask is not null)
        {
            try
            {
                await listenerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
            {
            }
        }

        listenerCancellation?.Dispose();
        if (IsPrimaryInstance)
        {
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        instanceMutex.Dispose();
    }

    private async Task ListenAsync(Func<AppStartupRequest, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var request = await JsonSerializer.DeserializeAsync<AppStartupRequest>(
                        pipe,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? new AppStartupRequest(AppStartupRequestAction.Activate, path: null);
                await handler(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }
    }
}
