using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Configuration;
using FluxVault.Core.Diagnostics;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Service;

public sealed class FluxVaultProfileRuntime(
    string profileId,
    FluxVaultOperations operations,
    FileSystemProtectionLoop protectionLoop,
    RepositoryMaintenanceLoop maintenanceLoop)
{
    public string ProfileId { get; } = profileId;

    public FluxVaultOperations Operations { get; } = operations;

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            protectionLoop.RunAsync(cancellationToken),
            maintenanceLoop.RunAsync(cancellationToken));
    }
}

public sealed class FluxVaultProfileManager(
    IFluxVaultProfileSetStore profileSetStore,
    Func<FluxVaultProfileConfiguration, FluxVaultProfileRuntime> runtimeFactory,
    DiagnosticsPolicyRuntime? diagnosticsPolicyRuntime = null,
    TelemetryCollector? telemetryCollector = null) : IFluxVaultRequestHandler
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, FluxVaultProfileRuntime> runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RunningProfileRuntime> runningRuntimes = new(StringComparer.OrdinalIgnoreCase);

    public async Task<FluxVaultIpcResponse> HandleAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        return request.Command switch
        {
            FluxVaultIpcCommand.CreateProfile => await CreateProfileAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.RenameProfile => await RenameProfileAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.DuplicateProfile => await DuplicateProfileAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.DeleteProfile => await DeleteProfileAsync(request, cancellationToken).ConfigureAwait(false),
            FluxVaultIpcCommand.SetActiveProfile => await SetActiveProfileAsync(request, cancellationToken).ConfigureAwait(false),
            _ => await RouteProfileRequestAsync(request, cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task RunEnabledProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                diagnosticsPolicyRuntime?.Update(profileSet.ActiveProfile.Configuration.DiagnosticsPolicy);
                telemetryCollector?.RecordLoopState("Profile manager", "Running", "Reconciling enabled profiles");
                await ReconcileRunningProfilesAsync(profileSet, cancellationToken).ConfigureAwait(false);
                await ThrowIfAnyProfileRuntimeStoppedAsync().ConfigureAwait(false);
                telemetryCollector?.RecordLoopState("Profile manager", "Waiting", "Delay");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopAllRunningProfilesAsync().ConfigureAwait(false);
        }
    }

    private async Task<FluxVaultIpcResponse> RouteProfileRequestAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken)
    {
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = ResolveProfile(profileSet, request.ProfileId);
        var runtime = await EnsureRuntimeAsync(profile, cancellationToken).ConfigureAwait(false);
        var response = await runtime.Operations.HandleAsync(request with { ProfileId = null }, cancellationToken).ConfigureAwait(false);
        return response.Status is null
            ? response
            : response with { Status = AddProfileSummaries(response.Status, profileSet, profile.Id) };
    }

    private async Task<FluxVaultIpcResponse> CreateProfileAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken)
    {
        var profileId = Require(request.ProfileId, "profile id");
        var displayName = Require(request.ProfileDisplayName, "profile display name");
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (profileSet.Profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return FluxVaultIpcResponse.Failure($"Profile already exists: {profileId}");
        }

        var configuration = request.Configuration ?? FluxVaultConfiguration.CreateDefault(ProfileProgramDataPath(profileId, profileSet));
        var profile = new FluxVaultProfileConfiguration(profileId, displayName, IsEnabled: true, configuration);
        var updated = profileSet with
        {
            ActiveProfileId = profileId,
            Profiles = profileSet.Profiles.Append(profile).ToArray()
        };
        await profileSetStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await EnsureRuntimeAsync(profile, cancellationToken).ConfigureAwait(false);
        return FluxVaultIpcResponse.WithStatus(AddProfileSummaries(
            await runtimes[profileId].Operations.GetStatusAsync(cancellationToken).ConfigureAwait(false),
            await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false),
            profileId));
    }

    private async Task<FluxVaultIpcResponse> RenameProfileAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken)
    {
        var profileId = Require(request.ProfileId, "profile id");
        var displayName = Require(request.ProfileDisplayName, "profile display name");
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profiles = profileSet.Profiles
            .Select(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)
                ? profile with { DisplayName = displayName }
                : profile)
            .ToArray();
        if (!profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return FluxVaultIpcResponse.Failure($"Profile was not found: {profileId}");
        }

        await profileSetStore.SaveAsync(profileSet with { Profiles = profiles }, cancellationToken).ConfigureAwait(false);
        return await RouteProfileRequestAsync(FluxVaultIpcRequest.GetStatus(profileId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FluxVaultIpcResponse> DuplicateProfileAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken)
    {
        var sourceProfileId = Require(request.SourceProfileId, "source profile id");
        var profileId = Require(request.ProfileId, "profile id");
        var displayName = Require(request.ProfileDisplayName, "profile display name");
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (profileSet.Profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return FluxVaultIpcResponse.Failure($"Profile already exists: {profileId}");
        }

        var sourceProfile = ResolveProfile(profileSet, sourceProfileId);
        var duplicatedConfiguration = sourceProfile.Configuration with
        {
            RepositoryPath = Path.Combine(
                Path.GetDirectoryName(sourceProfile.Configuration.RepositoryPath) ?? sourceProfile.Configuration.RepositoryPath,
                profileId)
        };
        var profile = new FluxVaultProfileConfiguration(profileId, displayName, IsEnabled: true, duplicatedConfiguration);
        var updated = profileSet with
        {
            ActiveProfileId = profileId,
            Profiles = profileSet.Profiles.Append(profile).ToArray()
        };
        await profileSetStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await EnsureRuntimeAsync(profile, cancellationToken).ConfigureAwait(false);
        return await RouteProfileRequestAsync(FluxVaultIpcRequest.GetStatus(profileId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FluxVaultIpcResponse> DeleteProfileAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken)
    {
        var profileId = Require(request.ProfileId, "profile id");
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (profileSet.Profiles.Count <= 1)
        {
            return FluxVaultIpcResponse.Failure("Cannot delete the last FluxVault profile.");
        }

        if (!profileSet.Profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return FluxVaultIpcResponse.Failure($"Profile was not found: {profileId}");
        }

        var profiles = profileSet.Profiles
            .Where(profile => !string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var activeProfileId = string.Equals(profileSet.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase)
            ? profiles.FirstOrDefault(profile => profile.IsEnabled)?.Id ?? profiles[0].Id
            : profileSet.ActiveProfileId;
        await profileSetStore.SaveAsync(profileSet with
        {
            ActiveProfileId = activeProfileId,
            Profiles = profiles
        }, cancellationToken).ConfigureAwait(false);
        return await RouteProfileRequestAsync(FluxVaultIpcRequest.GetStatus(activeProfileId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FluxVaultIpcResponse> SetActiveProfileAsync(
        FluxVaultIpcRequest request,
        CancellationToken cancellationToken)
    {
        var profileId = Require(request.ProfileId, "profile id");
        var profileSet = await profileSetStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _ = ResolveProfile(profileSet, profileId);
        await profileSetStore.SaveAsync(profileSet with { ActiveProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return await RouteProfileRequestAsync(FluxVaultIpcRequest.GetStatus(profileId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FluxVaultProfileRuntime> EnsureRuntimeAsync(
        FluxVaultProfileConfiguration profile,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!runtimes.TryGetValue(profile.Id, out var runtime))
            {
                runtime = runtimeFactory(profile);
                runtimes[profile.Id] = runtime;
            }

            return runtime;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ReconcileRunningProfilesAsync(
        FluxVaultProfileSetConfiguration profileSet,
        CancellationToken cancellationToken)
    {
        var enabledProfiles = profileSet.Profiles
            .Where(profile => profile.IsEnabled)
            .ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var profileId in runningRuntimes.Keys.ToArray())
            {
                if (!enabledProfiles.ContainsKey(profileId))
                {
                    runningRuntimes[profileId].Cancellation.Cancel();
                    runningRuntimes.Remove(profileId);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        foreach (var profile in enabledProfiles.Values)
        {
            var runtime = await EnsureRuntimeAsync(profile, cancellationToken).ConfigureAwait(false);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (runningRuntimes.ContainsKey(profile.Id))
                {
                    continue;
                }

                var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                runningRuntimes[profile.Id] = new RunningProfileRuntime(
                    linkedCancellation,
                    runtime.RunAsync(linkedCancellation.Token));
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task StopAllRunningProfilesAsync()
    {
        RunningProfileRuntime[] running;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            running = runningRuntimes.Values.ToArray();
            runningRuntimes.Clear();
        }
        finally
        {
            gate.Release();
        }

        foreach (var runtime in running)
        {
            await runtime.Cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ThrowIfAnyProfileRuntimeStoppedAsync()
    {
        RunningProfileRuntime[] stopped;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            stopped = runningRuntimes.Values
                .Where(runtime => runtime.Task.IsCompleted && !runtime.Cancellation.IsCancellationRequested)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }

        foreach (var runtime in stopped)
        {
            await runtime.Task.ConfigureAwait(false);
            throw new InvalidOperationException("A FluxVault profile runtime stopped unexpectedly.");
        }
    }

    private static FluxVaultServiceStatus AddProfileSummaries(
        FluxVaultServiceStatus status,
        FluxVaultProfileSetConfiguration profileSet,
        string activeProfileId)
    {
        return status with
        {
            ActiveProfileId = activeProfileId,
            Profiles = profileSet.Profiles
                .Select(profile => new FluxVaultProfileRuntimeStatus(
                    profile.Id,
                    profile.DisplayName,
                    profile.IsEnabled,
                    string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase),
                    profile.Configuration.RepositoryPath,
                    profile.Configuration.WatchedFolders.Count,
                    profile.Configuration.MirrorSet?.Nodes.Count(node => node.IsEnabled) ?? 0))
                .ToArray()
        };
    }

    private static FluxVaultProfileConfiguration ResolveProfile(
        FluxVaultProfileSetConfiguration profileSet,
        string? profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            return profileSet.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Profile was not found: {profileId}");
        }

        return profileSet.ActiveProfile;
    }

    private static string ProfileProgramDataPath(string profileId, FluxVaultProfileSetConfiguration profileSet)
    {
        var activePath = profileSet.ActiveProfile.Configuration.RepositoryPath;
        var root = Path.GetDirectoryName(Path.GetFullPath(activePath)) ?? Path.GetFullPath(activePath);
        return Path.Combine(root, "profiles", profileId);
    }

    private static string Require(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.")
            : value.Trim();
    }

    private sealed record RunningProfileRuntime(CancellationTokenSource Cancellation, Task Task);
}
