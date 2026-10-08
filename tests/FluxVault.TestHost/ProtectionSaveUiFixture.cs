using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;

namespace FluxVault.TestHost;

// Uses the real WPF window and profile store; never contacts the installed service or PostgreSQL.
internal static class ProtectionSaveUiFixture
{
    public static void Show(IReadOnlyDictionary<string, string> options, string scratch)
    {
        var failure = options.GetValueOrDefault("save-failure", "rejected");
        if (failure is not ("rejected" or "cancelled")) throw new ArgumentException("Unsupported save fixture failure.");
        var configPath = Path.Combine(scratch, "configuration.json");
        var store = new FluxVaultProfileConfigurationStore(new FileFluxVaultProfileSetStore(configPath, scratch), "default");
        var configuration = FluxVaultConfiguration.CreateDefault(scratch) with { IsEnabled = false };
        store.SaveAsync(configuration).GetAwaiter().GetResult();
        var projectPath = Path.Combine(scratch, "Working project");
        Directory.CreateDirectory(projectPath);
        File.WriteAllText(Path.Combine(projectPath, "Fixture document.txt"), "Disposable native save validation.");
        var client = new SaveClient(store, scratch, failure);
        var application = new System.Windows.Application();
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromHours(1),
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()), new ServiceController());
        var window = new FluxVault.App.MainWindow(new FileDataGridLayoutStore(Path.Combine(scratch, "ui-layout.json")))
        {
            DataContext = viewModel, Title = $"FluxVault protection-save fixture ({failure})"
        };
        window.Loaded += async (_, _) =>
        {
            await viewModel.RefreshAsync();
            viewModel.FileBrowser.ReplaceSelectionRule(new("fixture-project", projectPath,
                ProtectionSelectionMode.RecursiveFolder, CompressionPreference.Zstd, ResourceProfile.Balanced, true));
            viewModel.SelectedWorkspaceIndex = 1;
        };
        window.Closed += (_, _) => File.WriteAllText(Path.Combine(scratch, "closed-state.json"), JsonSerializer.Serialize(new
        {
            state = viewModel.ProtectionSaveState.ToString(), viewModel.ProtectionSaveMessage,
            pendingChanges = viewModel.FileBrowser.PendingChanges.Count
        }));
        application.Run(window);
    }

    private sealed class SaveClient(IFluxVaultConfigurationStore store, string scratch, string failure) : IFluxVaultServiceClient
    {
        private int saves;
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            File.AppendAllText(Path.Combine(scratch, "commands.jsonl"), JsonSerializer.Serialize(new
            {
                command = request.Command.ToString(), request.ProfileId
            }) + Environment.NewLine);
            switch (request.Command)
            {
                case FluxVaultIpcCommand.GetStatus:
                    return FluxVaultIpcResponse.WithStatus(new(true, await store.LoadAsync(cancellationToken),
                        "Disposable fixture idle", null, [], []));
                case FluxVaultIpcCommand.SaveConfiguration:
                    if (++saves == 1)
                    {
                        if (failure == "cancelled") throw new OperationCanceledException("Disposable fixture save cancelled");
                        return FluxVaultIpcResponse.Failure("Disposable fixture rejected this save");
                    }
                    if (request.ProfileId != "default") throw new InvalidOperationException("Wrong fixture profile.");
                    await store.SaveAsync(request.Configuration ?? throw new InvalidOperationException("Missing configuration"), cancellationToken);
                    return FluxVaultIpcResponse.Ok();
                case FluxVaultIpcCommand.RunBackupNow:
                    if (request.ProfileId != "default") throw new InvalidOperationException("Wrong fixture backup profile.");
                    return FluxVaultIpcResponse.WithBackup(new(true, "Disposable fixture backup command accepted", 0, 0, DateTimeOffset.UtcNow));
                default:
                    return FluxVaultIpcResponse.Failure("Command is outside the disposable save fixture scope.");
            }
        }
    }

    private sealed class ServiceController : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FluxVaultWindowsServiceStatus("Disposable fixture", FluxVaultWindowsServiceState.Running, "Fixture only"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
