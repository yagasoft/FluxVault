using System.IO;
using System.Text.Json;
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

namespace FluxVault.TestHost;

// Finite rendered WPF evidence; all stores and responses are disposable/local.
internal static class ProtectionDraftUiFixture
{
    internal static int Render(string scratch, bool interactive = false)
    {
        using var process=System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(scratch,"ui-process.json"),JsonSerializer.Serialize(new
            {ProcessId=process.Id,StartedUtc=process.StartTime.ToUniversalTime().ToString("o"),Executable=process.MainModule!.FileName}));
        var store=new FileFluxVaultConfigurationStore(Path.Combine(scratch,"configuration.json"),scratch);
        store.SaveAsync(FluxVaultConfiguration.CreateDefault(scratch)).GetAwaiter().GetResult();
        var client=new Client(store);
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var model=new MainWindowViewModel(client,TimeSpan.FromHours(1),new FileBrowserViewModel(new Files()),new Controller(),new Destination(),new Overwrite(),
            saveOperationStore:new FileConfigurationSaveOperationStore(Path.Combine(scratch,"save.json")),protectionDraftStore:new FileProtectionDraftStore(Path.Combine(scratch,"draft.json")));
        var window=new FluxVault.App.MainWindow(new FileDataGridLayoutStore(Path.Combine(scratch,"layout.json")))
            {DataContext=model,Width=1440,Height=900,Title="FluxVault disposable local draft verification"};
        FluxVault.App.OptionsWindow? options=null; Exception? failure=null;
        var closing=false; var joined=false;
        var lifetime=new DispatcherTimer{Interval=TimeSpan.FromMinutes(8)};
        if(interactive)
        {
            // Finite UI observation only: the actual window/view-model and local
            // stores run with a rejecting client, never the installed service.
            lifetime.Tick+=(_,_)=>window.Close(); lifetime.Start();
            window.Closing+=async (_,args)=>
            {
                if(joined)return;
                args.Cancel=true;
                if(closing)return;
                closing=true;
                try
                {
                    if(!await model.PrepareForExitAsync())throw new IOException(model.LocalProtectionDraftMessage);
                    await model.StopRepositoryReadsAsync();
                    File.WriteAllText(Path.Combine(scratch,"ui-result.json"),JsonSerializer.Serialize(new
                    {Interactive=true,Joined=true,client.Backups,client.Saves,model.ProtectionSaveMessage,model.LocalProtectionDraftMessage,
                        model.RepositoryPath,SaveState=model.ProtectionSaveState.ToString()}));
                }
                catch(Exception exception){failure=exception;}
                finally
                {
                    try{await model.CancelAndJoinLocalProtectionDraftWriterAsync();}
                    finally{lifetime.Stop();joined=true;window.Close();app.Shutdown();}
                }
            };
        }
        window.Loaded+=async (_,_)=>
        {
            if(interactive)
            {
                try{await model.RefreshAsync();model.SelectedWorkspaceIndex=1;}
                catch(Exception exception){failure=exception;window.Close();}
                return;
            }
            try
            {
                await model.RefreshAsync(); model.RepositoryPath=Path.Combine(scratch,"unfinished-repository");
                model.SelectedWorkspaceIndex=1; await model.RunBackupNowCommand.ExecuteAsync(null);
                await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
                var saved=(TextBlock)window.FindName("ProtectionSaveStatusText");
                var local=(TextBlock)window.FindName("LocalProtectionDraftStatusText");
                if(saved.Visibility!=Visibility.Visible || local.Visibility!=Visibility.Visible ||
                    saved.TransformToAncestor(window).Transform(new Point(0,saved.ActualHeight)).Y>local.TransformToAncestor(window).Transform(new Point()).Y)
                    throw new InvalidOperationException("Save and local draft status must be separately visible without overlap.");
                Snapshot(window,Path.Combine(scratch,"protect-draft.png"));
                var optionsModel=new OptionsViewModel(client,new Explorer()); await optionsModel.InitialiseAsync();
                options=new FluxVault.App.OptionsWindow(optionsModel){Owner=window}; options.Show();
                ((TabControl)Descendants(options).First(element=>element is TabControl)).SelectedIndex=2;
                await options.Dispatcher.InvokeAsync(()=>options.UpdateLayout(),DispatcherPriority.ApplicationIdle);
                var delay=(TextBox)options.FindName("ProtectionDraftSaveDelayTextBox"); delay.BringIntoView();
                await options.Dispatcher.InvokeAsync(()=>options.UpdateLayout(),DispatcherPriority.ApplicationIdle);
                var top=delay.TransformToAncestor(options).Transform(new Point()).Y;
                if(delay.Text!="500" || delay.ActualWidth<=0 || top<100 || top+delay.ActualHeight>options.ActualHeight-80)
                    throw new InvalidOperationException("Draft policy must be visible within the actual Options viewport.");
                Snapshot(options,Path.Combine(scratch,"options-draft.png"));
                File.WriteAllText(Path.Combine(scratch,"ui-result.json"),JsonSerializer.Serialize(new
                    {LocalAndServiceStatesVisible=true,StatusDoesNotOverlap=true,OptionsDelayVisible=true,client.Backups,model.ProtectionSaveMessage,model.LocalProtectionDraftMessage}));
            }
            catch(Exception exception){failure=exception;}
            finally
            {
                await model.CancelAndJoinLocalProtectionDraftWriterAsync(); await model.StopRepositoryReadsAsync();
                options?.Close(); window.Close(); app.Shutdown();
            }
        };
        app.Run(window);
        if(failure is not null)throw new InvalidOperationException("Disposable WPF draft rendering failed.",failure);
        return 0;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);yield return child;
            foreach(var descendant in Descendants(child))yield return descendant;
        }
    }
    private static void Snapshot(Window window,string path)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(path);encoder.Save(output);
    }
    private sealed class Client(IFluxVaultConfigurationStore store):IFluxVaultServiceClient
    {
        private readonly VaultId id=VaultId.New(); internal int Backups; internal int Saves;
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request,CancellationToken cancellationToken=default)
        {
            if(request.Command==FluxVaultIpcCommand.GetStatus)return FluxVaultIpcResponse.WithStatus(new(true,await store.LoadAsync(cancellationToken),"Disposable local UI fixture",null,[],[],TrackedEntries:[])) with{VaultId=id,VaultRevision=1};
            if(request.Command==FluxVaultIpcCommand.RunBackupNow)Backups++;
            if(request.Command==FluxVaultIpcCommand.SaveConfiguration)Saves++;
            return FluxVaultIpcResponse.Failure("Disposable service rejected the save. Check the selection and try again.") with{ErrorCode=FluxVaultIpcErrorCode.InvalidRequest};
        }
    }
    private sealed class Explorer:IExplorerContextMenuService
    {
        public ExplorerContextMenuStatus GetStatus()=>new(false,false,false,"Disposable Explorer registration is disabled.");
        public ExplorerContextMenuStatus Register()=>throw new NotSupportedException();
        public ExplorerContextMenuStatus Unregister()=>throw new NotSupportedException();
    }
    private sealed class Files:IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()=>[];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)=>[];
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)=>[];
    }
    private sealed class Controller:IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture",FluxVaultWindowsServiceState.Running,"Fixture"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken=default)=>throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken=default)=>throw new InvalidOperationException();
    }
    private sealed class Destination:IRestoreDestinationPicker{public string? PickDestination(VersionRow version)=>throw new InvalidOperationException();}
    private sealed class Overwrite:IRestoreOverwriteConfirmation{public bool ConfirmOverwrite(string destinationPath)=>throw new InvalidOperationException();}
}
