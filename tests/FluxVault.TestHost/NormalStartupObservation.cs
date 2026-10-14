using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluxVault.App.ViewModels;

namespace FluxVault.TestHost;

// Explicit, read-only normal-startup diagnosis. No layout/render intervention,
// command execution or substituted client. Never an acceptance substitute for
// displayed pixels and the live recovery workflow.
internal static class NormalStartupObservation
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);

    internal static int Run(string scratch, bool softwareRendering = false, int seconds = 3, bool mvpUi = false)
    {
        if (mvpUi) Window.ShowActivatedProperty.OverrideMetadata(typeof(FluxVault.App.MainWindow), new FrameworkPropertyMetadata(false));
        if (softwareRendering) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(scratch, "ui-process.json"), JsonSerializer.Serialize(new
            { ProcessId = process.Id, StartedUtc = process.StartTime.ToUniversalTime().ToString("o"), Executable = process.MainModule!.FileName }));
        var app = new FluxVault.App.App();
        var dispatcherStarts = new Dictionary<DispatcherOperation, long>();
        var dispatcherDurations = new List<double>();
        DateTimeOffset? loadedAtUtc = null, contentRenderedAtUtc = null;
        DispatcherHookEventHandler operationStarted = (_, args) => dispatcherStarts[args.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler operationFinished = (_, args) =>
        {
            if (dispatcherStarts.Remove(args.Operation, out var start) && dispatcherDurations.Count < 100_000)
                dispatcherDurations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        };
        app.Dispatcher.Hooks.OperationStarted += operationStarted;
        app.Dispatcher.Hooks.OperationCompleted += operationFinished;
        app.Dispatcher.Hooks.OperationAborted += operationFinished;
        var rendering = 0;
        DateTimeOffset? firstRendering = null, lastRendering = null;
        var loaded = false;
        var contentRendered = false;
        var idle = false;
        var shuttingDown = false;
        void Write(string name, object value) => File.WriteAllText(Path.Combine(scratch, name + ".json"), JsonSerializer.Serialize(value));
        EventHandler render = (_, _) =>
        {
            rendering++;
            firstRendering ??= DateTimeOffset.UtcNow;
            lastRendering = DateTimeOffset.UtcNow;
        };
        RoutedEventHandler onLoaded = (sender, _) =>
        {
            if (sender is not FluxVault.App.MainWindow window) return;
            loaded = true;
            loadedAtUtc = DateTimeOffset.UtcNow;
            window.ContentRendered += (_, _) => { contentRendered = true; contentRenderedAtUtc = DateTimeOffset.UtcNow; };
        };
        EventManager.RegisterClassHandler(typeof(FluxVault.App.MainWindow), FrameworkElement.LoadedEvent, onLoaded);
        CompositionTarget.Rendering += render;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += async (_, _) =>
        {
            if (shuttingDown) return;
            shuttingDown = true;
            timer.Stop();
            try
            {
                // Freeze passive timings before explicit navigation/render instrumentation.
                var passiveDurations = dispatcherDurations.ToArray();
                var window = app.MainWindow;
                var root = window?.Content as FrameworkElement;
                var source = window is null ? null : PresentationSource.FromVisual(window) as HwndSource;
                var hwnd = source?.Handle ?? 0;
                var hasRect = hwnd != 0 && GetWindowRect(hwnd, out _);
                GetWindowRect(hwnd, out var rect);
                var path = root is null ? null : Descendants(root).OfType<TextBox>().FirstOrDefault(box =>
                    box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "RepositoryPath");
                var peer = path is null ? null : UIElementAutomationPeer.CreatePeerForElement(path);
                Write("ui-startup-observation", new
                {
                    StartupObserved = true, SoftwareRendering = softwareRendering, EffectiveRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                    Loaded = loaded, ContentRendered = contentRendered, ApplicationIdle = idle,
                    ProcessFresh = true, CacheState = "Uncontrolled; prior product and build activity. No cache flush.",
                    LoadedMillisecondsFromProcessStart = loadedAtUtc is null ? (double?)null : (loadedAtUtc.Value.UtcDateTime - process.StartTime.ToUniversalTime()).TotalMilliseconds,
                    ContentRenderedMillisecondsFromProcessStart = contentRenderedAtUtc is null ? (double?)null : (contentRenderedAtUtc.Value.UtcDateTime - process.StartTime.ToUniversalTime()).TotalMilliseconds,
                    DispatcherOperationCount = passiveDurations.Length,
                    DispatcherMaximumMilliseconds = passiveDurations.Length == 0 ? (double?)null : passiveDurations.Max(),
                    DispatcherOver100Milliseconds = passiveDurations.Count(value => value > 100),
                    DispatcherSlowestMilliseconds = passiveDurations.OrderDescending().Take(10).ToArray(),
                    ProcessCpuSeconds = process.TotalProcessorTime.TotalSeconds, WorkingSetBytes = process.WorkingSet64, HandleCount = process.HandleCount,
                    RenderingEvents = rendering, FirstRendering = firstRendering, LastRendering = lastRendering,
                    Window = window is null ? null : new { window.ActualWidth, window.ActualHeight, window.Visibility, window.IsVisible, window.IsEnabled, window.IsMeasureValid, window.IsArrangeValid },
                    Root = root is null ? null : new { root.ActualWidth, root.ActualHeight, root.Visibility, root.IsVisible, root.IsEnabled, root.IsMeasureValid, root.IsArrangeValid },
                    Hwnd = hwnd.ToInt64(), HasPresentationSource = source is not null, HasCompositionTarget = source?.CompositionTarget is not null,
                    NativeVisible = IsWindowVisible(hwnd),
                    Dpi = window is null ? (DpiScale?)null : VisualTreeHelper.GetDpi(window), HasNativeRect = hasRect,
                    NativeRect = new { rect.Left, rect.Top, rect.Right, rect.Bottom },
                    RepositoryTextBox = peer is null ? null : new { Bounds = peer.GetBoundingRectangle().ToString(System.Globalization.CultureInfo.InvariantCulture), Offscreen = peer.IsOffscreen() }
                });
                if (window?.DataContext is MainWindowViewModel model)
                {
                    if (mvpUi)
                    {
                        var tabs = (TabControl)window.FindName("OperationalCockpitWorkspace");
                        var navigation = new List<object>();
                        for (var pass = 0; pass < 2; pass++)
                        for (var index = 0; index < tabs.Items.Count; index++)
                        {
                            var timer = Stopwatch.StartNew();
                            model.SelectedWorkspaceIndex = index;
                            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                            navigation.Add(new { Pass = pass, Tab = ((TabItem)tabs.Items[index]).Header?.ToString(), Milliseconds = timer.Elapsed.TotalMilliseconds });
                            if (pass == 1 && index is 1 or 2) Render(window, index == 1 ? "file-browser" : "repository");
                        }
                        model.SelectedWorkspaceIndex = 0;
                        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                        Render(window, "dashboard");
                        var optionsModel = new OptionsViewModel(new FluxVault.Core.Ipc.NamedPipeFluxVaultClient(
                            FluxVault.Windows.Security.WindowsFluxVaultPipeClientFactory.ForService()));
                        var optionsTimer = Stopwatch.StartNew();
                        await optionsModel.InitialiseAsync();
                        var options = new FluxVault.App.OptionsWindow(optionsModel) { ShowActivated = false };
                        try
                        {
                            options.Show();
                            await app.Dispatcher.InvokeAsync(options.UpdateLayout, DispatcherPriority.ApplicationIdle);
                            Render(options, "automatic-options");
                            Write("ui-mvp", new { Navigation = navigation, AutomaticStatus = optionsModel.AutomaticProtectionStatus,
                                OptionsLoadedAndRenderedMilliseconds = optionsTimer.Elapsed.TotalMilliseconds,
                                NoMouseOrKeyboardInput = true, NoActivationRequested = true,
                                MainWindowIsActive = window.IsActive, OptionsWindowIsActive = options.IsActive });
                        }
                        finally { options.Close(); }
                    }
                    model.StopAutoRefresh();
                    await model.CancelAndJoinLocalProtectionDraftWriterAsync();
                    await model.StopRepositoryReadsAsync();
                }
                Write("ui-startup-joined", new { Joined = true });
            }
            catch (Exception exception) { Write("ui-startup-error", new { Error = exception.ToString() }); }
            finally
            {
                CompositionTarget.Rendering -= render;
                app.Dispatcher.Hooks.OperationStarted -= operationStarted;
                app.Dispatcher.Hooks.OperationCompleted -= operationFinished;
                app.Dispatcher.Hooks.OperationAborted -= operationFinished;
                app.Shutdown();
            }
        };
        app.Startup += (_, _) =>
        {
            Write("ui-startup-entered", new { Startup = true, ObservedUtc = DateTimeOffset.UtcNow });
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => idle = true);
            timer.Start();
        };
        // The parent must bound/join this child even if the dispatcher never runs.
        return app.Run();

        void Render(Window window, string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(scratch, name + ".png")); encoder.Save(stream);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
