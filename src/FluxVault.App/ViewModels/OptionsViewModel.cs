using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;

namespace FluxVault.App.ViewModels;

public sealed partial class OptionsViewModel : ObservableObject
{
    private readonly IFluxVaultServiceClient client;
    private readonly IExplorerContextMenuService explorerContextMenuService;
    private FluxVaultConfiguration? currentConfiguration;

    public OptionsViewModel(IFluxVaultServiceClient client)
        : this(client, new WindowsExplorerContextMenuService())
    {
    }

    public OptionsViewModel(
        IFluxVaultServiceClient client,
        IExplorerContextMenuService explorerContextMenuService)
    {
        this.client = client;
        this.explorerContextMenuService = explorerContextMenuService;
        RefreshExplorerContextMenuStatus();
    }

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
    private int watcherPollSeconds;

    [ObservableProperty]
    private int periodicReconciliationMinutes;

    [ObservableProperty]
    private int fastDebounceSeconds;

    [ObservableProperty]
    private int balancedDebounceSeconds;

    [ObservableProperty]
    private int quietDebounceSeconds;

    [ObservableProperty]
    private int fastMaxHotFileDelaySeconds;

    [ObservableProperty]
    private int balancedMaxHotFileDelayMinutes;

    [ObservableProperty]
    private int quietMaxHotFileDelayMinutes;

    [ObservableProperty]
    private int minimumSameFileCaptureIntervalSeconds;

    [ObservableProperty]
    private int maximumConcurrentCaptures;

    [ObservableProperty]
    private CodecProfile codecProfile;

    [ObservableProperty]
    private CompressionPreference defaultCodec;

    [ObservableProperty]
    private CompressionPreference hotFileCodec;

    [ObservableProperty]
    private int codecLevel;

    [ObservableProperty]
    private int codecMinimumKb;

    [ObservableProperty]
    private string previewText = "Retention preview has not been run.";

    [ObservableProperty]
    private string statusText = "Ready";

    [ObservableProperty]
    private string explorerContextMenuStatus = "Explorer context menu status is checking.";

    [ObservableProperty]
    private bool isExplorerContextMenuRegistered;

    [ObservableProperty]
    private string newExclusionLabel = string.Empty;

    [ObservableProperty]
    private string newExclusionDescription = string.Empty;

    [ObservableProperty]
    private string newExclusionPattern = string.Empty;

    [ObservableProperty]
    private ProtectionExclusionTarget newExclusionTarget = ProtectionExclusionTarget.Both;

    [ObservableProperty]
    private ProtectionExclusionRule? selectedExclusionRule;

    public IReadOnlyList<CodecProfile> CodecProfiles { get; } = Enum.GetValues<CodecProfile>();

    public IReadOnlyList<CompressionPreference> Codecs { get; } = Enum.GetValues<CompressionPreference>();

    public IReadOnlyList<ProtectionExclusionTarget> ExclusionTargets { get; } = Enum.GetValues<ProtectionExclusionTarget>();

    public ObservableCollection<ProtectionExclusionRule> ExclusionRules { get; } = [];

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        RefreshExplorerContextMenuStatus();
        var response = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cancellationToken).ConfigureAwait(true);
        if (!response.Success || response.Status is null)
        {
            StatusText = $"Could not load options: {response.ErrorMessage ?? "no status returned"}";
            return;
        }

        currentConfiguration = response.Status.Configuration;
        ApplyPolicy(currentConfiguration.RetentionPolicy);
        ApplyCadence(currentConfiguration.CaptureCadencePolicy);
        ApplyCodec(currentConfiguration.CodecPolicy);
        ApplyExclusions(currentConfiguration.ExclusionRules ?? []);
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
            RetentionPolicy = BuildPolicy(),
            CaptureCadencePolicy = BuildCadence(),
            CodecPolicy = BuildCodec(),
            ExclusionRules = []
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

    [RelayCommand]
    public void RegisterExplorerContextMenu()
    {
        ApplyExplorerContextMenuStatus(explorerContextMenuService.Register());
    }

    [RelayCommand]
    public void UnregisterExplorerContextMenu()
    {
        ApplyExplorerContextMenuStatus(explorerContextMenuService.Unregister());
    }

    [RelayCommand]
    public void AddExclusionRule()
    {
        var rule = new ProtectionExclusionRule(
            Id: $"exclude-{Guid.NewGuid():N}",
            Pattern: NewExclusionPattern.Trim(),
            Target: NewExclusionTarget,
            IsEnabled: true,
            Label: string.IsNullOrWhiteSpace(NewExclusionLabel) ? null : NewExclusionLabel.Trim(),
            Description: string.IsNullOrWhiteSpace(NewExclusionDescription) ? null : NewExclusionDescription.Trim());
        var validation = ProtectionExclusionRuleValidator.Validate([rule]);
        if (!validation.IsValid)
        {
            StatusText = string.Join(" ", validation.Errors);
            return;
        }

        ExclusionRules.Add(rule);
        NewExclusionLabel = string.Empty;
        NewExclusionDescription = string.Empty;
        NewExclusionPattern = string.Empty;
        NewExclusionTarget = ProtectionExclusionTarget.Both;
        StatusText = "Exclusion rule added. Save options to apply it.";
    }

    [RelayCommand]
    public void RemoveSelectedExclusionRule()
    {
        if (SelectedExclusionRule is null)
        {
            return;
        }

        ExclusionRules.Remove(SelectedExclusionRule);
        SelectedExclusionRule = null;
        StatusText = "Exclusion rule removed. Save options to apply it.";
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

    private void ApplyCadence(CaptureCadencePolicy policy)
    {
        WatcherPollSeconds = Math.Max(1, (int)Math.Round(policy.WatcherPollInterval.TotalSeconds));
        PeriodicReconciliationMinutes = Math.Max(1, (int)Math.Round(policy.PeriodicReconciliationInterval.TotalMinutes));
        FastDebounceSeconds = Math.Max(0, (int)Math.Round(policy.FastDebounce.TotalSeconds));
        BalancedDebounceSeconds = Math.Max(0, (int)Math.Round(policy.BalancedDebounce.TotalSeconds));
        QuietDebounceSeconds = Math.Max(0, (int)Math.Round(policy.QuietDebounce.TotalSeconds));
        FastMaxHotFileDelaySeconds = Math.Max(1, (int)Math.Round(policy.FastMaxHotFileDelay.TotalSeconds));
        BalancedMaxHotFileDelayMinutes = Math.Max(1, (int)Math.Round(policy.BalancedMaxHotFileDelay.TotalMinutes));
        QuietMaxHotFileDelayMinutes = Math.Max(1, (int)Math.Round(policy.QuietMaxHotFileDelay.TotalMinutes));
        MinimumSameFileCaptureIntervalSeconds = Math.Max(0, (int)Math.Round(policy.MinimumSameFileCaptureInterval.TotalSeconds));
        MaximumConcurrentCaptures = Math.Max(1, policy.MaximumConcurrentCaptures);
    }

    private CaptureCadencePolicy BuildCadence()
    {
        return new CaptureCadencePolicy(
            WatcherPollInterval: TimeSpan.FromSeconds(Math.Max(1, WatcherPollSeconds)),
            PeriodicReconciliationInterval: TimeSpan.FromMinutes(Math.Max(1, PeriodicReconciliationMinutes)),
            FastDebounce: TimeSpan.FromSeconds(Math.Max(0, FastDebounceSeconds)),
            BalancedDebounce: TimeSpan.FromSeconds(Math.Max(0, BalancedDebounceSeconds)),
            QuietDebounce: TimeSpan.FromSeconds(Math.Max(0, QuietDebounceSeconds)),
            FastMaxHotFileDelay: TimeSpan.FromSeconds(Math.Max(1, FastMaxHotFileDelaySeconds)),
            BalancedMaxHotFileDelay: TimeSpan.FromMinutes(Math.Max(1, BalancedMaxHotFileDelayMinutes)),
            QuietMaxHotFileDelay: TimeSpan.FromMinutes(Math.Max(1, QuietMaxHotFileDelayMinutes)),
            MinimumSameFileCaptureInterval: TimeSpan.FromSeconds(Math.Max(0, MinimumSameFileCaptureIntervalSeconds)),
            MaximumConcurrentCaptures: Math.Max(1, MaximumConcurrentCaptures));
    }

    private void ApplyCodec(CodecPolicy policy)
    {
        CodecProfile = policy.Profile;
        DefaultCodec = policy.Codec;
        HotFileCodec = policy.HotFileOverride;
        CodecLevel = Math.Max(1, policy.Level);
        CodecMinimumKb = Math.Max(0, (int)Math.Round(policy.MinimumBytes / 1024d));
    }

    private CodecPolicy BuildCodec()
    {
        var defaults = currentConfiguration?.CodecPolicy ?? CodecPolicy.CreateDefault();
        return defaults with
        {
            Codec = DefaultCodec,
            Profile = CodecProfile,
            Level = Math.Clamp(CodecLevel, 1, 22),
            MinimumBytes = Math.Max(0, CodecMinimumKb) * 1024L,
            HotFileOverride = HotFileCodec
        };
    }

    private void ApplyExclusions(IReadOnlyList<ProtectionExclusionRule> rules)
    {
        ExclusionRules.Clear();
        foreach (var rule in rules)
        {
            ExclusionRules.Add(rule);
        }
    }

    private void RefreshExplorerContextMenuStatus()
    {
        ApplyExplorerContextMenuStatus(explorerContextMenuService.GetStatus());
    }

    private void ApplyExplorerContextMenuStatus(ExplorerContextMenuStatus status)
    {
        IsExplorerContextMenuRegistered = status.IsRegistered;
        ExplorerContextMenuStatus = status.Message;
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
