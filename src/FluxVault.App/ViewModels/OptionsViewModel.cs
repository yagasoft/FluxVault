using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;

namespace FluxVault.App.ViewModels;

public sealed partial class OptionsViewModel(IFluxVaultServiceClient client) : ObservableObject
{
    private FluxVaultConfiguration? currentConfiguration;

    [ObservableProperty]
    private bool retentionEnabled;

    [ObservableProperty]
    private int keepAllHours;

    [ObservableProperty]
    private int keepHourlyDays;

    [ObservableProperty]
    private int keepDailyDays;

    [ObservableProperty]
    private int minimumVersionsPerFile;

    [ObservableProperty]
    private string previewText = "Retention preview has not been run.";

    [ObservableProperty]
    private string statusText = "Ready";

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cancellationToken).ConfigureAwait(true);
        if (!response.Success || response.Status is null)
        {
            StatusText = $"Could not load options: {response.ErrorMessage ?? "no status returned"}";
            return;
        }

        currentConfiguration = response.Status.Configuration;
        ApplyPolicy(currentConfiguration.RetentionPolicy);
        StatusText = "Options loaded.";
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (currentConfiguration is null)
        {
            await InitialiseAsync(cancellationToken).ConfigureAwait(true);
        }

        if (currentConfiguration is null)
        {
            return;
        }

        var updated = currentConfiguration with
        {
            RetentionPolicy = BuildPolicy()
        };
        var response = await client.SendAsync(FluxVaultIpcRequest.SaveConfiguration(updated), cancellationToken)
            .ConfigureAwait(true);
        if (response.Success)
        {
            currentConfiguration = updated;
            StatusText = "Options saved.";
        }
        else
        {
            StatusText = $"Options save failed: {response.ErrorMessage}";
        }
    }

    [RelayCommand]
    public async Task PreviewRetentionAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.PreviewRetention(), cancellationToken).ConfigureAwait(true);
        if (!response.Success || response.RetentionPreview is null)
        {
            PreviewText = $"Preview failed: {response.ErrorMessage ?? "no preview returned"}";
            return;
        }

        PreviewText = FormatPreview(response.RetentionPreview);
    }

    [RelayCommand]
    public async Task RunRetentionNowAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAsync(FluxVaultIpcRequest.RunRetentionNow(), cancellationToken).ConfigureAwait(true);
        if (!response.Success || response.RetentionResult is null)
        {
            StatusText = $"Retention failed: {response.ErrorMessage ?? "no retention result returned"}";
            return;
        }

        StatusText = FormatResult(response.RetentionResult);
    }

    private void ApplyPolicy(RetentionPolicy policy)
    {
        RetentionEnabled = policy.IsEnabled;
        KeepAllHours = Math.Max(0, (int)Math.Round(policy.KeepAllFor.TotalHours));
        KeepHourlyDays = Math.Max(0, (int)Math.Round(policy.KeepHourlyFor.TotalDays));
        KeepDailyDays = Math.Max(0, (int)Math.Round(policy.KeepDailyFor.TotalDays));
        MinimumVersionsPerFile = Math.Max(1, policy.MinimumVersionsPerFile);
    }

    private RetentionPolicy BuildPolicy()
    {
        return new RetentionPolicy(
            IsEnabled: RetentionEnabled,
            KeepAllFor: TimeSpan.FromHours(Math.Max(0, KeepAllHours)),
            KeepHourlyFor: TimeSpan.FromDays(Math.Max(0, KeepHourlyDays)),
            KeepDailyFor: TimeSpan.FromDays(Math.Max(0, KeepDailyDays)),
            MinimumVersionsPerFile: Math.Max(1, MinimumVersionsPerFile));
    }

    private static string FormatPreview(RepositoryRetentionPreview preview)
    {
        return $"Preview: keep {preview.KeptVersionCount} version(s), prune {preview.PrunableVersionCount} version(s), reclaim about {FormatBytes(preview.EstimatedReclaimableBytes)}. Repository size {FormatBytes(preview.RepositorySizeBytes)}.";
    }

    private static string FormatResult(RepositoryRetentionResult result)
    {
        return $"Retention kept {result.KeptVersionCount} version(s), pruned {result.PrunedVersionCount}, reclaimed {FormatBytes(result.ReclaimedBytes)}.";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }
}
