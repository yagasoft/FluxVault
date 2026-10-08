using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.ViewModels;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.App.Tests;

public sealed class HistoryWindowRenderingTests
{
    [Fact]
    public Task History_controls_render_at_minimum_size_and_close_after_reads_join() => RunSta(async () =>
    {
        var id = VaultId.New();
        var query = RepositoryHistoryPaging.Validate(new RepositoryHistoryQuery(id, @"C:\Studio\CAD", true));
        var row = new RepositoryVersionSummary(1.ToString("x32"), @"C:\Studio\CAD\assembly.dwg", DateTimeOffset.UtcNow,
            CaptureConsistency.BestEffort, 25_600_000, 12);
        var model = new VersionInventoryViewModel(@"C:\Studio\CAD", query,
            (q, _) => Task.FromResult(FluxVaultIpcResponse.Ok() with { HistoryPage = RepositoryHistoryPaging.Page(q, 1, [row], true, false) }),
            (q, _) => Task.FromResult(FluxVaultIpcResponse.Ok() with { SnapshotPage = new(q, row, [], null) }),
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        var window = new ProtectedFolderVersionsWindow(model) { Width = 980, Height = 520, ShowInTaskbar = false, ShowActivated = false };
        var errors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        Dispatcher.CurrentDispatcher.UnhandledException += handler;
        try
        {
            await model.InitialiseAsync();
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var shell = Assert.IsType<Grid>(window.Content);
            foreach (var name in new[] { "Load older history page", "Load newer history page", "Refresh history from the first page", "Load next folder entries page" })
            {
                var button = Descendants(shell).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
                Assert.True(button.ActualWidth > 35 && button.ActualHeight > 20);
                Assert.True(button.Focusable);
                var position = button.TransformToAncestor(shell).Transform(new Point());
                Assert.InRange(position.X, 0, shell.ActualWidth - button.ActualWidth);
                Assert.InRange(position.Y, 0, shell.ActualHeight - button.ActualHeight);
            }
            var render = Environment.GetEnvironmentVariable("FLUXVAULT_HISTORY_RENDER");
            if (!string.IsNullOrWhiteSpace(render))
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(shell.ActualWidth + shell.Margin.Left + shell.Margin.Right),
                    (int)Math.Ceiling(shell.ActualHeight + shell.Margin.Top + shell.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                bitmap.Render(background); bitmap.Render(shell);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(render); encoder.Save(output);
            }
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Empty(errors);
            Assert.False(window.IsVisible);
        }
        finally { Dispatcher.CurrentDispatcher.UnhandledException -= handler; await model.DisposeAsync(); if (window.IsVisible) window.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
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
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Owned WPF dispatcher thread did not exit."); }
    }
}
