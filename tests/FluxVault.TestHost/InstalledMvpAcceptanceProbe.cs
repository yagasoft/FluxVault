using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;
using FluxVault.Windows.Security;

namespace FluxVault.TestHost;

// Explicitly owned installed-workload acceptance. Never opens the original sample paths.
// All private content, source mappings, configuration and history remain outside Git.
internal static class InstalledMvpAcceptanceProbe
{
    private sealed record Sample(string Name, string Folder, long Length, string Sha256);

    internal static async Task RunAsync(string root, string phase)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-21-136112424-624261118-1239521417-1001")
            throw new UnauthorizedAccessException("The installed creator is required.");
        root = FluxVault.Testing.MvpStagingPaths.ResolveRoot(root);
        if (phase is not ("roundtrip" or "sustain" or "verify-retained" or "restore-baseline" or "snapshot")) throw new ArgumentException("Unsupported MVP phase.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(phase == "sustain" ? 75 : 20));
        var ct = deadline.Token;
        var client = new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForService());
        var initial = await SendAsync(FluxVaultIpcRequest.GetStatus());
        if (initial.VaultId?.Value != Guid.Parse("67539278-9292-4870-93cd-a011419228dc") || initial.Status?.AutomaticProtection is null)
            throw new InvalidDataException("Installed single vault identity was not confirmed.");
        var id = initial.VaultId!.Value;
        var baselinePath = Path.Combine(root, "baseline.private.json");
        if (phase == "snapshot") { WriteNew("snapshot-" + Guid.NewGuid().ToString("N") + ".private.json", initial); return; }
        if (phase == "restore-baseline") { await RestoreBaselineAsync(); return; }
        AssertDesktopClosed();
        if (phase == "verify-retained")
        {
            using var prior = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "roundtrip.json"), ct));
            var verified = new List<object>();
            foreach (var item in prior.RootElement.GetProperty("IndependentRecovery").EnumerateArray())
            {
                var name = item.GetProperty("Name").GetString()!;
                if (Path.GetFileName(name) != name || !name.StartsWith("sample-", StringComparison.Ordinal)) throw new InvalidDataException("Unowned retained sample.");
                var versionId = item.GetProperty("VersionId").GetString()!;
                var expected = item.GetProperty("ExpectedSha256").GetString()!;
                var output = Path.Combine(root, "recovery", "retained-" + Guid.NewGuid().ToString("N") + "-" + name);
                await MutateAsync(FluxVaultIpcRequest.RestoreVersion(versionId, output));
                var actual = await HashAsync(output);
                if (actual != expected) throw new InvalidDataException("Retained recovery differs after the installation transition.");
                verified.Add(new { VersionId = versionId, ExpectedSha256 = expected, ActualSha256 = actual });
            }
            var final = await SendAsync(FluxVaultIpcRequest.GetStatus());
            if (JsonSerializer.Serialize(final.Status!.Configuration) != JsonSerializer.Serialize(initial.Status.Configuration) ||
                final.Status.AutomaticProtection!.Policy.Enabled != initial.Status.AutomaticProtection.Policy.Enabled)
                throw new InvalidDataException("Read/recovery changed saved protection controls.");
            WriteNew("retained-verified-" + Guid.NewGuid().ToString("N") + ".json", new { Passed = true, Versions = verified,
                ConfigurationPreserved = true, CompletedUtc = DateTimeOffset.UtcNow });
            return;
        }
        if (initial.Status.AutomaticProtection.Policy.Enabled || initial.Status.AutomaticProtection.PendingCount != 0 ||
            initial.Status.AutomaticProtection.UncertainCount != 0)
            throw new InvalidOperationException("The accepted automatic-off, settled baseline is required.");
        var samples = JsonSerializer.Deserialize<Sample[]>(await File.ReadAllBytesAsync(Path.Combine(root, "corpus.json"), ct))!;
        if (samples.Length is < 1 or > 100) throw new InvalidDataException("The representative corpus is missing or oversized.");
        foreach (var sample in samples)
        {
            if (Path.GetFileName(sample.Name) != sample.Name || !sample.Name.StartsWith("sample-", StringComparison.Ordinal) ||
                sample.Folder is not ("documents" or "data" or "media")) throw new InvalidDataException("Unowned sample path.");
            if (await HashAsync(Path.Combine(root, "corpus", sample.Name)) != sample.Sha256) throw new InvalidDataException("Corpus digest changed.");
        }
        if (phase == "sustain")
        {
            var baseline = JsonSerializer.Deserialize<FluxVaultIpcResponse>(await File.ReadAllBytesAsync(baselinePath, ct))!;
            if (JsonSerializer.Serialize(initial.Status.Configuration) != JsonSerializer.Serialize(baseline.Status!.Configuration))
                throw new InvalidOperationException("The prior corpus run's original configuration must still be current.");
        }
        else
        {
            WriteNew("baseline.private.json", initial);
            WriteNew("history-before.private.json", await SendAsync(FluxVaultIpcRequest.ListVersions() with { VaultId = id }));
        }
        var folders = samples.Select(sample => sample.Folder).Distinct().ToArray();
        foreach (var folder in folders) Directory.CreateDirectory(Path.Combine(root, "working", folder));
        Directory.CreateDirectory(Path.Combine(root, "recovery"));
        try
        {
            var configuration = initial.Status.Configuration with
            {
                WatchedFolders = folders.Select(folder => new WatchedFolderConfiguration("mvp-" + folder,
                    Path.Combine(root, "working", folder), true, ["*"], [], CompressionPreference.Off, ResourceProfile.Balanced, true)).ToArray(),
                SelectionRules = []
            };
            await MutateAsync(FluxVaultIpcRequest.SaveConfiguration(configuration));
            var options = new OptionsViewModel(client);
            await options.InitialiseAsync(ct);
            options.AutomaticProtectionEnabled = true;
            await options.ApplyAutomaticProtectionAsync(ct);
            if (options.HasUnconfirmedSave || !(await SendAsync(FluxVaultIpcRequest.GetStatus())).Status!.AutomaticProtection!.Policy.Enabled)
                throw new InvalidOperationException("Creator enable through the actual Options model was not confirmed.");
            WriteNew(phase + "-enabled.private.json", await SendAsync(FluxVaultIpcRequest.GetStatus()));
            if (phase == "sustain")
            {
                var groups = samples.Where(sample => Path.GetExtension(sample.Name) is ".docx" or ".xlsx" or ".pptx" or ".csv" or ".pdf" or ".vsdx")
                    .GroupBy(sample => Path.GetExtension(sample.Name)).Select(group => group.OrderBy(sample => sample.Length).ToArray()).Where(group => group.Length == 2).ToArray();
                var observations = new List<object>();
                var timer = Stopwatch.StartNew();
                var iteration = 0;
                while (timer.Elapsed < TimeSpan.FromHours(1))
                {
                    var group = groups[iteration % groups.Length];
                    var target = group[0];
                    var expected = group[((iteration / groups.Length) + 1) % 2];
                    var previous = (await HistoryAsync(Source(target))).Versions.First(version => !version.IsDeleted).VersionId;
                    var control = Path.Combine(root, "foreground-control.tmp");
                    var sampleInput = Path.Combine(root, "corpus", expected.Name);
                    var save = Stopwatch.StartNew();
                    File.Copy(sampleInput, control, true);
                    var controlMs = save.Elapsed.TotalMilliseconds;
                    save.Restart();
                    var temporary = Source(target) + ".saving";
                    File.Copy(sampleInput, temporary, true);
                    File.Move(temporary, Source(target), true);
                    var protectedMs = save.Elapsed.TotalMilliseconds;
                    RepositoryVersionSummary version;
                    while (true)
                    {
                        version = (await HistoryAsync(Source(target))).Versions.First(item => !item.IsDeleted);
                        if (version.VersionId != previous) break;
                        await Task.Delay(1000, ct);
                    }
                    var output = Path.Combine(root, "recovery", "sustained-" + iteration.ToString("D3") + Path.GetExtension(target.Name));
                    await MutateAsync(FluxVaultIpcRequest.RestoreVersion(version.VersionId, output));
                    var actual = await HashAsync(output);
                    if (actual != expected.Sha256) throw new InvalidDataException("Sustained save recovery digest differs.");
                    var historyTimer = Stopwatch.StartNew();
                    await HistoryAsync(Source(target));
                    var state = (await SendAsync(FluxVaultIpcRequest.GetStatus())).Status!.AutomaticProtection!;
                    observations.Add(new { Iteration = iteration, target.Name, expected.Length, expected.Sha256, ActualSha256 = actual,
                        version.VersionId, ControlSaveMs = controlMs, ProtectedSaveMs = protectedMs, SaveToVerifiedRecoveryMs = save.Elapsed.TotalMilliseconds,
                        HistoryAndStatusMs = historyTimer.Elapsed.TotalMilliseconds, state.PendingCount, state.UncertainCount, State = state.State.ToString() });
                    WriteProgress(new { Phase = "sustain", VerifiedSaves = ++iteration, ElapsedSeconds = timer.Elapsed.TotalSeconds, state.PendingCount, state.UncertainCount });
                    File.WriteAllText(Path.Combine(root, "sustain-observations.private.json"), JsonSerializer.Serialize(observations));
                    AssertDesktopClosed();
                    var due = TimeSpan.FromSeconds(iteration * 30) - timer.Elapsed;
                    if (due > TimeSpan.Zero) await Task.Delay(due, ct);
                }
                WriteNew("sustain.json", new { CompletedUtc = DateTimeOffset.UtcNow, ElapsedSeconds = timer.Elapsed.TotalSeconds,
                    VerifiedSaves = iteration, Observations = observations, DesktopClosed = true });
                return;
            }
            var writes = new List<object>();
            var started = Stopwatch.StartNew();
            foreach (var sample in samples)
            {
                var timer = Stopwatch.StartNew();
                // CreateNew prevents accidental overwrite, even inside this owned run.
                await CopyNewAsync(Path.Combine(root, "corpus", sample.Name), Source(sample));
                writes.Add(new { sample.Name, sample.Length, SaveMs = timer.Elapsed.TotalMilliseconds });
            }
            WriteNew("source-saves.json", writes);
            var recovered = new List<object>();
            foreach (var sample in samples)
            {
                var version = await WaitForVersionAsync(sample);
                var output = Path.Combine(root, "recovery", sample.Name);
                await MutateAsync(FluxVaultIpcRequest.RestoreVersion(version.VersionId, output));
                var actual = await HashAsync(output);
                if (actual != sample.Sha256 || await HashAsync(Source(sample)) != sample.Sha256)
                    throw new InvalidDataException("Independent recovered/source content does not match the copied sample.");
                recovered.Add(new { sample.Name, sample.Length, version.VersionId, ExpectedSha256 = sample.Sha256, ActualSha256 = actual,
                    VerifiedAtMs = started.Elapsed.TotalMilliseconds });
                WriteProgress(new { Phase = "roundtrip", Recovered = recovered.Count, Total = samples.Length, ElapsedSeconds = started.Elapsed.TotalSeconds });
            }
            AssertDesktopClosed();
            var timings = new List<double>();
            for (var index = 0; index < 30; index++)
            {
                var timer = Stopwatch.StartNew();
                await HistoryAsync(Source(samples[index % samples.Length]));
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }
            WriteNew("roundtrip.json", new { CompletedUtc = DateTimeOffset.UtcNow, Samples = samples.Length, Bytes = samples.Sum(sample => sample.Length),
                MaximumBytes = samples.Max(sample => sample.Length), ElapsedSeconds = started.Elapsed.TotalSeconds, DesktopClosed = true,
                IndependentRecovery = recovered, HistorySamplesMs = timings, FinalStatus = await SendAsync(FluxVaultIpcRequest.GetStatus()) });
        }
        finally { await RestoreBaselineAsync(); }
        Console.WriteLine(JsonSerializer.Serialize(new { Phase = phase, Passed = true, Root = root }));

        string Source(Sample sample) => Path.Combine(root, "working", sample.Folder, sample.Name);
        async Task<string> HashAsync(string path)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        }
        async Task CopyNewAsync(string source, string target)
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            await input.CopyToAsync(output, ct); output.Flush(true);
        }
        async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request)
        {
            var response = await client.SendAsync(request, ct);
            if (!response.Success) throw new InvalidOperationException(response.ErrorCode + ": " + response.ErrorMessage);
            return response;
        }
        async Task<FluxVaultIpcResponse> MutateAsync(FluxVaultIpcRequest request)
        {
            var current = await SendAsync(FluxVaultIpcRequest.GetStatus());
            return await SendAsync(request with { VaultId = id, ExpectedVaultRevision = current.VaultRevision, OperationId = Guid.NewGuid() });
        }
        async Task<RepositoryHistoryPage> HistoryAsync(string source) =>
            (await SendAsync(new(FluxVaultIpcCommand.ListHistoryPage, null, null, null, null,
                VaultId: id, HistoryQuery: new(id, source, PageSize: 100)))).HistoryPage!;
        async Task<RepositoryVersionSummary> WaitForVersionAsync(Sample sample)
        {
            while (true)
            {
                var found = (await HistoryAsync(Source(sample))).Versions.FirstOrDefault(version => !version.IsDeleted);
                if (found is not null) return found;
                var status = (await SendAsync(FluxVaultIpcRequest.GetStatus())).Status!.AutomaticProtection!;
                WriteProgress(new { Phase = "capture", WaitingFor = sample.Name, State = status.State.ToString(), status.PendingCount, status.Reason });
                if (status.State is AutomaticProtectionState.Failed or AutomaticProtectionState.OutcomeUncertain)
                    throw new InvalidOperationException("Automatic capture requires attention: " + status.Reason);
                await Task.Delay(1000, ct);
            }
        }
        async Task RestoreBaselineAsync()
        {
            // A fresh bounded cleanup token allows restoration after a workload timeout.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var original = JsonSerializer.Deserialize<FluxVaultIpcResponse>(await File.ReadAllBytesAsync(baselinePath, cleanup.Token))!;
            foreach (var request in new[] { FluxVaultIpcRequest.SetAutomaticProtection(false), FluxVaultIpcRequest.SaveConfiguration(original.Status!.Configuration) })
            {
                var current = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cleanup.Token);
                if (!current.Success || current.VaultId != id) throw new InvalidOperationException("Cleanup vault identity is unavailable.");
                var result = await client.SendAsync(request with { VaultId = id, ExpectedVaultRevision = current.VaultRevision, OperationId = Guid.NewGuid() }, cleanup.Token);
                if (!result.Success) throw new InvalidOperationException("Cleanup configuration outcome is unconfirmed: " + result.ErrorMessage);
            }
            var restored = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cleanup.Token);
            if (!restored.Success || restored.Status!.AutomaticProtection!.Policy.Enabled ||
                JsonSerializer.Serialize(restored.Status.Configuration) != JsonSerializer.Serialize(original.Status!.Configuration))
                throw new InvalidDataException("Baseline configuration was not restored exactly.");
            var before = JsonSerializer.Deserialize<FluxVaultIpcResponse>(await File.ReadAllBytesAsync(Path.Combine(root, "history-before.private.json"), cleanup.Token))!;
            foreach (var version in before.Versions!)
            {
                var retained = await client.SendAsync(FluxVaultIpcRequest.InspectVersion(version.VersionId) with { VaultId = id }, cleanup.Token);
                if (!retained.Success) throw new InvalidDataException("An existing version is no longer readable.");
            }
            WriteNew("baseline-restored-" + Guid.NewGuid().ToString("N") + ".private.json", new { OriginalConfigurationPreserved = true,
                ExistingVersionsPreserved = before.Versions.Count, AutomaticOff = true, Restored = restored });
        }
        void WriteNew(string name, object value)
        {
            using var file = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, value); file.Flush(true);
        }
        void WriteProgress(object value) => File.WriteAllText(Path.Combine(root, "progress.json"), JsonSerializer.Serialize(value));
    }

    private static void AssertDesktopClosed()
    {
        var apps = Process.GetProcessesByName("FluxVault.App");
        try { if (apps.Length != 0) throw new InvalidOperationException("The desktop must remain closed for this acceptance."); }
        finally { foreach (var process in apps) process.Dispose(); }
    }
}
