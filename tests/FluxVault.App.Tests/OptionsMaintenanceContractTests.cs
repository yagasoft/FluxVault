using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class OptionsMaintenanceContractTests
{
    [Fact]
    public async Task Actual_options_save_preserves_unavailable_schedule_exactly_through_the_file_store()
    {
        using var fixture = new Fixture();
        var original = await fixture.InitialiseAsync();
        var model = fixture.Model();
        await model.InitialiseAsync();
        model.KeepAllHours = 13;
        model.RestoreRehearsalVersionCount = 7;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Options saved.", model.StatusText);
        var reopened = await fixture.Reopen().LoadAsync();
        Assert.Equal(original.RepositoryMaintenancePolicy with { RestoreRehearsalVersionCount = 7 }, reopened.RepositoryMaintenancePolicy);
        Assert.Equal(TimeSpan.FromHours(13), reopened.RetentionPolicy.KeepAllFor);
        Assert.Equal(original.MetadataStore, reopened.MetadataStore);
        Assert.Equal(original.IsEnabled, reopened.IsEnabled);
        Assert.Single(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.DoesNotContain(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
    }

    [Fact]
    public Task Rendered_options_explain_unavailable_scheduling_and_disable_its_controls() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.InitialiseAsync();
        var model = fixture.Model();
        var window = new OptionsWindow(model) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            await model.InitialiseAsync();
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var descendants = Descendants(window).ToArray();
            var automatic = Assert.IsType<CheckBox>(window.FindName("AutomaticMaintenanceToggle"));
            var interval = Assert.IsType<TextBox>(window.FindName("AutomaticMaintenanceInterval"));
            Assert.False(automatic.IsEnabled);
            Assert.False(interval.IsEnabled);
            Assert.Equal("07:31:00", interval.Text);
            Assert.True(automatic.IsChecked); // Preserve the saved preference without promising an active schedule.
            var message = Assert.Single(descendants.OfType<TextBlock>(), item => item.Text.Contains("Automatic maintenance is unavailable", StringComparison.Ordinal));
            Assert.True(message.IsVisible && message.ActualWidth > 0 && message.ActualHeight > 0);
            var manual = Assert.Single(descendants.OfType<TextBox>(), item =>
                item.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "RestoreRehearsalVersionCount");
            Assert.True(manual.IsEnabled);
            var render = Environment.GetEnvironmentVariable("FLUXVAULT_OPTIONS_MAINTENANCE_RENDER");
            if (!string.IsNullOrWhiteSpace(render))
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.RenderSize.Width),
                    (int)Math.Ceiling(window.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(render); encoder.Save(output);
            }
        }
        finally { window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static async Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Owned Options dispatcher did not exit."); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FluxVault.OptionsMaintenance." + Guid.NewGuid().ToString("N"));
        internal StoreClient Client { get; }
        internal Fixture() { Directory.CreateDirectory(root); Client = new(Reopen()); }
        internal FileFluxVaultConfigurationStore Reopen() => new(Path.Combine(root, "config.json"), root);
        internal async Task<FluxVaultConfiguration> InitialiseAsync()
        {
            await Reopen().SaveAsync(FluxVaultConfiguration.CreateDefault(root) with
            { RepositoryMaintenancePolicy = new(false, TimeSpan.FromMinutes(451), false, 5, true) });
            return await Reopen().LoadAsync();
        }
        internal OptionsViewModel Model() => new(Client, new Explorer());
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
    private sealed class StoreClient(FileFluxVaultConfigurationStore store) : IFluxVaultServiceClient
    {
        private readonly VaultId id = VaultId.New();
        private long revision = 1;
        internal List<FluxVaultIpcRequest> Requests { get; } = [];
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Command == FluxVaultIpcCommand.GetStatus)
                return FluxVaultIpcResponse.WithStatus(new(true, await store.LoadAsync(cancellationToken), "Manual backup available", null, [], []))
                    with { VaultId = id, VaultRevision = revision };
            if (request.Command != FluxVaultIpcCommand.SaveConfiguration || request.VaultId != id || request.ExpectedVaultRevision != revision || request.OperationId is null)
                throw new InvalidOperationException("Unexpected unbound Options command.");
            await store.SaveAsync(request.Configuration!, cancellationToken); revision++;
            return FluxVaultIpcResponse.Ok() with { VaultId = id, VaultRevision = revision, OperationId = request.OperationId };
        }
    }
    private sealed class Explorer : IExplorerContextMenuService
    {
        public ExplorerContextMenuStatus GetStatus() => new(false, false, false, "Fixture");
        public ExplorerContextMenuStatus Register() => throw new NotSupportedException();
        public ExplorerContextMenuStatus Unregister() => throw new NotSupportedException();
    }
}
