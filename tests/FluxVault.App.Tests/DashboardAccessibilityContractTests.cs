using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
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

public sealed class DashboardAccessibilityContractTests
{
    [Fact]
    public Task Primary_controls_expose_useful_native_automation_names() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var model = await fixture.ModelAsync();
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var bar = Assert.IsType<StackPanel>(window.FindName("OperationalCockpitCommandBar"));
            var expected = new[] { "Refresh", model.ServiceControlActionLabel, "Run backup now", "Restore", "Options", "About", "Export diagnostics" };
            var controls = bar.Children.OfType<Button>().ToArray();
            Assert.Equal(expected.Length, controls.Length);
            for (var index = 0; index < controls.Length; index++)
            {
                var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(controls[index]));
                Assert.Equal(expected[index], peer.GetName());
            }
            var path = Descendants(window).OfType<TextBox>().Single(control =>
                control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "RepositoryPath");
            Assert.Equal("Repository path", UIElementAutomationPeer.CreatePeerForElement(path)!.GetName());
        }
        finally { await model.CancelAndJoinLocalProtectionDraftWriterAsync(); await model.StopRepositoryReadsAsync(); window.Close(); }
    });

    [Theory]
    [InlineData(1080)]
    [InlineData(1440)]
    public Task Protection_page_shows_the_complete_failed_save_explanation_without_hover(double width) => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var original = await fixture.Store.LoadAsync();
        var model = await fixture.ModelAsync();
        var window = new MainWindow { DataContext = model, Width = width, Height = 760, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            model.RepositoryPath = Path.Combine(fixture.Root, "pending-repository");
            await model.RunBackupNowCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Assert.Equal(ProtectionSaveState.Failed, model.ProtectionSaveState);
            Assert.Equal(1, fixture.Client.Saves); Assert.Equal(0, fixture.Client.Backups);
            Assert.Equal(Path.Combine(fixture.Root, "pending-repository"), model.RepositoryPath);
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await fixture.Store.LoadAsync()));
            var notice = Assert.Single(Descendants(window).OfType<TextBlock>(), text =>
                text.IsVisible && text.Text == model.ProtectionSaveMessage);
            Assert.Contains("Your changes are kept", notice.Text);
            Assert.Contains("Backup has not started", notice.Text);
            Assert.Equal(notice.Text, UIElementAutomationPeer.CreatePeerForElement(notice)!.GetName());
            var formatted = new FormattedText(notice.Text, CultureInfo.CurrentUICulture, notice.FlowDirection,
                new Typeface(notice.FontFamily, notice.FontStyle, notice.FontWeight, notice.FontStretch), notice.FontSize,
                notice.Foreground, VisualTreeHelper.GetDpi(notice).PixelsPerDip) { MaxTextWidth = Math.Max(1, notice.ActualWidth) };
            Assert.True(notice.ActualHeight >= formatted.Height - 0.5, "The full explanation must fit, without clipping or ellipsis.");
            var position = notice.TransformToAncestor(window).Transform(new Point());
            Assert.InRange(position.Y, 0, window.ActualHeight - notice.ActualHeight);
            Assert.InRange(position.X, 0, window.ActualWidth - notice.ActualWidth);
            var render = Environment.GetEnvironmentVariable("FLUXVAULT_DASHBOARD_RENDER_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(render))
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(render, $"protect-failure-{width:0}.png")); encoder.Save(output);
            }
        }
        finally { await model.CancelAndJoinLocalProtectionDraftWriterAsync(); await model.StopRepositoryReadsAsync(); window.Close(); }
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
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Owned WPF dispatcher did not exit."); }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "FluxVault.DashboardUx." + Guid.NewGuid().ToString("N"));
        internal FileFluxVaultConfigurationStore Store { get; }
        internal Client Client { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "configuration.json"), Root); Client = new(Store);
            Store.SaveAsync(FluxVaultConfiguration.CreateDefault(Root)).GetAwaiter().GetResult();
        }
        internal async Task<MainWindowViewModel> ModelAsync()
        {
            var model = new MainWindowViewModel(Client, TimeSpan.FromHours(1), new FileBrowserViewModel(new Files()), new Controller(),
                new Destination(), new Overwrite(), protectionDraftStore: new FileProtectionDraftStore(Path.Combine(Root, "draft.json")));
            await model.RefreshAsync(); return model;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private sealed class Client(FileFluxVaultConfigurationStore store) : IFluxVaultServiceClient
    {
        private readonly VaultId id = VaultId.New(); internal int Saves; internal int Backups;
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Command == FluxVaultIpcCommand.GetStatus)
                return FluxVaultIpcResponse.WithStatus(new(true, await store.LoadAsync(cancellationToken), "Disposable fixture", null, [], [], TrackedEntries: []))
                    with { VaultId = id, VaultRevision = 1 };
            if (request.Command == FluxVaultIpcCommand.SaveConfiguration) Saves++;
            if (request.Command == FluxVaultIpcCommand.RunBackupNow) Backups++;
            return FluxVaultIpcResponse.Failure("The disposable service could not save the selections. Review the pending configuration and try again.")
                with { ErrorCode = FluxVaultIpcErrorCode.InvalidRequest };
        }
    }
    private sealed class Files : IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots() => [];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path) => [];
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path) => [];
    }
    private sealed class Controller : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture", FluxVaultWindowsServiceState.Running, "Fixture only"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Destination : IRestoreDestinationPicker { public string? PickDestination(VersionRow version) => throw new NotSupportedException(); }
    private sealed class Overwrite : IRestoreOverwriteConfirmation { public bool ConfirmOverwrite(string destinationPath) => throw new NotSupportedException(); }
}
