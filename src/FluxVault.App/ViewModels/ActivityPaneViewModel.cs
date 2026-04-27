using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;

namespace FluxVault.App.ViewModels;

public sealed partial class ActivityPaneViewModel(IFluxVaultServiceClient client) : ObservableObject
{
    [ObservableProperty]
    private string statusText = "Activity is loading...";

    public ObservableCollection<ActivityEventRow> Events { get; } = [];

    public ObservableCollection<BlockedFileRow> BlockedFiles { get; } = [];

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var activity = await client.SendAsync(FluxVaultIpcRequest.GetActivity(), cancellationToken).ConfigureAwait(true);
        var blocked = await client.SendAsync(FluxVaultIpcRequest.ListBlockedFiles(), cancellationToken).ConfigureAwait(true);

        Events.Clear();
        if (activity.Success && activity.ActivityEvents is not null)
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

        BlockedFiles.Clear();
        if (blocked.Success && blocked.BlockedFiles is not null)
        {
            foreach (var item in blocked.BlockedFiles)
            {
                BlockedFiles.Add(new BlockedFileRow(item.SourcePath, item.BlockedReason ?? "Blocked"));
            }
        }

        StatusText = blocked.Success
            ? $"{Events.Count} event(s), {BlockedFiles.Count} blocked file(s)"
            : $"Activity loaded; blocked files unavailable ({blocked.ErrorMessage})";
    }

    [RelayCommand]
    public async Task RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.RunBackupNow(), cancellationToken).ConfigureAwait(true);
        StatusText = response.Backup?.Message ?? response.ErrorMessage ?? "Backup request finished.";
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task PauseProtectionAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.SetProtectionPaused(), cancellationToken).ConfigureAwait(true);
        StatusText = response.Success ? "Protection pause state changed." : response.ErrorMessage ?? "Pause is not available.";
    }
}

public sealed record ActivityEventRow(
    string Time,
    FluxVaultActivityKind Kind,
    string Title,
    string Detail,
    string SourcePath);

public sealed record BlockedFileRow(string SourcePath, string Reason);
