using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;
using FluxVault.Abstractions.Security;
using System.IO;

namespace FluxVault.App.ViewModels;

public sealed partial class ActivityPaneViewModel(IFluxVaultServiceClient client, MainWindowViewModel? dashboard = null) : ObservableObject
{
    public MainWindowViewModel? Dashboard => dashboard;
    private VaultId? acceptedVaultId;
    [ObservableProperty]
    private string statusText = "Activity is loading...";

    public ObservableCollection<ActivityEventRow> Events { get; } = [];

    public ObservableCollection<BlockedFileRow> BlockedFiles { get; } = [];

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Events.Clear();
        BlockedFiles.Clear();
        try
        {
        var snapshot = await client.SendAsync(FluxVaultIpcRequest.GetStatus(statusDetailLevel: FluxVaultStatusDetailLevel.Fast) with { VaultId = acceptedVaultId }, cancellationToken).ConfigureAwait(true);
        if (!snapshot.Success || snapshot.Status is null || snapshot.VaultId is not { IsValid: true } id || snapshot.VaultRevision is not > 0 ||
            acceptedVaultId is { } accepted && accepted != id)
        { StatusText = $"Activity unavailable ({snapshot.ErrorMessage ?? "vault binding not confirmed"})."; return; }
        acceptedVaultId = id;
        var activity = await client.SendAsync(FluxVaultIpcRequest.GetActivity() with { VaultId = id }, cancellationToken).ConfigureAwait(true);
        var blocked = await client.SendAsync(FluxVaultIpcRequest.ListBlockedFiles() with { VaultId = id }, cancellationToken).ConfigureAwait(true);

        if (activity.Success && activity.VaultId == id && activity.VaultRevision is > 0 && activity.ActivityEvents is not null)
        {
            foreach (var item in activity.ActivityEvents.OrderByDescending(value => value.TimestampUtc))
            {
                Events.Add(new ActivityEventRow(
                    item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                    item.Kind,
                    item.Title,
                    item.Detail,
                    item.SourcePath ?? string.Empty));
            }
        }

        if (blocked.Success && blocked.VaultId == id && blocked.VaultRevision is > 0 && blocked.BlockedFiles is not null)
        {
            foreach (var item in blocked.BlockedFiles)
            {
                BlockedFiles.Add(new BlockedFileRow(item.SourcePath, item.BlockedReason ?? "Blocked"));
            }
        }

        StatusText = blocked.Success && activity.Success && blocked.VaultId == id && activity.VaultId == id
            ? $"{Events.Count} event(s), {BlockedFiles.Count} blocked file(s)"
            : $"Activity or blocked files unavailable ({blocked.ErrorMessage ?? activity.ErrorMessage}).";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        { StatusText = "Activity unavailable; the service acknowledgement was not received."; }
    }

    [RelayCommand]
    public async Task RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        if (dashboard is null) { StatusText = "Open the dashboard to save selections and request a backup."; return; }
        if (!dashboard.RunBackupNowCommand.CanExecute(null)) { StatusText=dashboard.ProtectionControlStatus; return; }
        await dashboard.RunBackupNowCommand.ExecuteAsync(null).ConfigureAwait(true);
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        StatusText = dashboard.ServiceStatus;
    }

}

public sealed record ActivityEventRow(
    string Time,
    FluxVaultActivityKind Kind,
    string Title,
    string Detail,
    string SourcePath);

public sealed record BlockedFileRow(string SourcePath, string Reason);
