using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;
using FluxVault.Windows.Security;

namespace FluxVault.TestHost;

/// <summary>Bounded installed staging acceptance through the real authenticated service interfaces.</summary>
internal static class InstalledAutomaticAcceptanceProbe
{
    internal static async Task RunAsync(string root,string phase)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(identity.User?.Value!="S-1-5-21-136112424-624261118-1239521417-1001")throw new UnauthorizedAccessException();
        root=Path.GetFullPath(root);
        var parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"FluxVault Staging","NEXT005");
        if(Path.GetDirectoryName(root)!=parent || !Guid.TryParseExact(Path.GetFileName(root),"N",out _))throw new InvalidDataException("Use one owned NEXT005 staging run directory.");
        FluxVault.Testing.WindowsDatabaseProbeConfiguration.RejectReparseComponents(root);
        Directory.CreateDirectory(root);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(120));var ct=deadline.Token;
        var client=new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForService());
        var status=await SendAsync(FluxVaultIpcRequest.GetStatus());
        if(status.VaultId?.Value!=Guid.Parse("67539278-9292-4870-93cd-a011419228dc") || status.Status?.AutomaticProtection is null)
            throw new InvalidDataException("The installed automatic staging vault was not verified.");
        var id=status.VaultId!.Value;
        var working=Path.Combine(root,"working");Directory.CreateDirectory(working);
        var file=Path.Combine(working,"saved.txt");
        switch(phase)
        {
            case "prepare":
                if(status.Status.AutomaticProtection.Policy.Enabled)throw new InvalidDataException("Default-off upgrade was not observed.");
                WriteNew("baseline.json",status);
                WriteNew("history-before.json",await SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id}));
                var configuration=status.Status.Configuration with
                {
                    WatchedFolders=[new("next005-staging",working,true,["*.txt"],[],CompressionPreference.Off,ResourceProfile.Fast,true)],
                    SelectionRules=[],
                    CaptureCadencePolicy=status.Status.Configuration.CaptureCadencePolicy with
                    {FastDebounce=TimeSpan.FromSeconds(2),MinimumSameFileCaptureInterval=TimeSpan.FromSeconds(15)}
                };
                await MutateAsync(FluxVaultIpcRequest.SaveConfiguration(configuration));
                var options=new OptionsViewModel(client);await options.InitialiseAsync(ct);options.AutomaticProtectionEnabled=true;
                await options.ApplyAutomaticProtectionAsync(ct);
                if(options.HasUnconfirmedSave)throw new InvalidOperationException("Installed Options preference was not confirmed.");
                WriteNew("prepared.json",await SendAsync(FluxVaultIpcRequest.GetStatus()));
                break;
            case "capture":
                if(GetProcesses("FluxVault.App").Length!=0)throw new InvalidOperationException("Close and join the desktop before proving service-only capture.");
                var bytes=RandomNumberGenerator.GetBytes(1024*1024);await File.WriteAllBytesAsync(Path.Combine(root,"expected-first.bin"),bytes,ct);
                var timer=Stopwatch.StartNew();await File.WriteAllBytesAsync(file,bytes,ct);var saveMs=timer.Elapsed.TotalMilliseconds;
                var first=await WaitForVersionAsync(null);var captureMs=timer.Elapsed.TotalMilliseconds;
                await File.WriteAllBytesAsync(Path.Combine(root,"expected-pending.bin"),bytes.Select(value=>(byte)(value^0x5A)).ToArray(),ct);
                await File.WriteAllBytesAsync(file,await File.ReadAllBytesAsync(Path.Combine(root,"expected-pending.bin"),ct),ct);
                FluxVaultIpcResponse pending;
                do{pending=await SendAsync(FluxVaultIpcRequest.GetStatus());if(pending.Status!.AutomaticProtection!.PendingCount>0)break;await Task.Delay(50,ct);}while(true);
                timer.Restart();await MutateAsync(FluxVaultIpcRequest.SetProtectionPaused(true));var pauseMs=timer.Elapsed.TotalMilliseconds;
                var paused=await SendAsync(FluxVaultIpcRequest.GetStatus());
                var versions=await SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id});
                if(paused.Status!.AutomaticProtection!.PendingCount<1 || versions.Versions!.Count(item=>item.SourcePath==file)!=1)
                    throw new InvalidOperationException("The saved second observation was not retained pending before restart.");
                WriteNew("captured.json",new {FirstVersion=first.VersionId,Source=file,SaveMs=saveMs,AutomaticCaptureMs=captureMs,PauseAcknowledgementMs=pauseMs,Paused=paused});
                break;
            case "verify-restart":
                using(var captured=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root,"captured.json"),ct)))
                {
                    var firstId=captured.RootElement.GetProperty("FirstVersion").GetString()!;
                    if(status.Status.AutomaticProtection.State!=AutomaticProtectionState.Paused || status.Status.AutomaticProtection.PendingCount<1)
                        throw new InvalidOperationException("Restart did not preserve the paused durable pending observation.");
                    await VerifyRecoveryAsync(firstId,"first");
                    await MutateAsync(FluxVaultIpcRequest.SetProtectionPaused(false));
                    var second=await WaitForVersionAsync(firstId);
                    await VerifyRecoveryAsync(second.VersionId,"pending");
                    var timings=new List<double>();
                    for(var i=0;i<20;i++){var watch=Stopwatch.StartNew();await SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id});timings.Add(watch.Elapsed.TotalMilliseconds);}
                    WriteNew("restart-verified.json",new {FirstVersion=firstId,PendingVersion=second.VersionId,IndependentSha256Recovery=true,HistorySamplesMs=timings,Status=await SendAsync(FluxVaultIpcRequest.GetStatus())});
                }
                break;
            case "restore-baseline":
                var baseline=JsonSerializer.Deserialize<FluxVaultIpcResponse>(await File.ReadAllBytesAsync(Path.Combine(root,"baseline.json"),ct))!;
                var stop=new OptionsViewModel(client);await stop.InitialiseAsync(ct);stop.AutomaticProtectionEnabled=false;await stop.ApplyAutomaticProtectionAsync(ct);
                if(stop.HasUnconfirmedSave)throw new InvalidOperationException("Disabling the scoped fixture was not confirmed.");
                await MutateAsync(FluxVaultIpcRequest.SaveConfiguration(baseline.Status!.Configuration));
                var restored=await SendAsync(FluxVaultIpcRequest.GetStatus());
                if(JsonSerializer.Serialize(restored.Status!.Configuration)!=JsonSerializer.Serialize(baseline.Status.Configuration) || restored.Status.AutomaticProtection!.Policy.Enabled)
                    throw new InvalidOperationException("Original configuration/default-off policy was not restored.");
                var before=JsonSerializer.Deserialize<FluxVaultIpcResponse>(await File.ReadAllBytesAsync(Path.Combine(root,"history-before.json"),ct))!;
                var after=await SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id});
                if(before.Versions!.Any(previous=>!after.Versions!.Any(current=>current.VersionId==previous.VersionId)))throw new InvalidOperationException("Earlier history was not preserved.");
                WriteNew("baseline-restored.json",new {OriginalConfigurationPreserved=true,OriginalVersionsPreserved=before.Versions!.Count,OwnedVersionsRetained=after.Versions!.Count(item=>item.SourcePath==file),Status=restored});
                break;
            default:throw new ArgumentException("Unsupported installed acceptance phase.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new {Phase=phase,Root=root,Passed=true}));

        async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request)
        {
            var response=await client.SendAsync(request,ct);
            if(!response.Success)throw new InvalidOperationException(response.ErrorCode+": "+response.ErrorMessage);
            return response;
        }
        async Task<FluxVaultIpcResponse> MutateAsync(FluxVaultIpcRequest request)
        {
            var current=await SendAsync(FluxVaultIpcRequest.GetStatus());
            return await SendAsync(request with {VaultId=id,ExpectedVaultRevision=current.VaultRevision,OperationId=Guid.NewGuid()});
        }
        async Task<RepositoryVersionSummary> WaitForVersionAsync(string? except)
        {
            while(true)
            {
                var response=await SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=id});
                var found=response.Versions!.FirstOrDefault(item=>item.SourcePath==file && !item.IsDeleted && item.VersionId!=except);
                if(found is not null)return found;
                await Task.Delay(100,ct);
            }
        }
        async Task VerifyRecoveryAsync(string version,string expectedName)
        {
            var output=Path.Combine(working,"recovered-"+expectedName+".bin");
            await MutateAsync(FluxVaultIpcRequest.RestoreVersion(version,output));
            var actual=SHA256.HashData(await File.ReadAllBytesAsync(output,ct));
            var expected=SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root,"expected-"+expectedName+".bin"),ct));
            if(!actual.SequenceEqual(expected))throw new InvalidDataException("Independent recovery digest differs.");
            WriteNew("recovery-"+expectedName+".json",new {VersionId=version,ExpectedSha256=Convert.ToHexString(expected),ActualSha256=Convert.ToHexString(actual),IndependentVerification=true});
        }
        void WriteNew(string name,object value)
        {using var stream=new FileStream(Path.Combine(root,name),FileMode.CreateNew);JsonSerializer.Serialize(stream,value);stream.Flush(true);}
    }

    private static int[] GetProcesses(string name)
    {
        var processes=Process.GetProcessesByName(name);
        try{return processes.Select(process=>process.Id).ToArray();}
        finally{foreach(var process in processes)process.Dispose();}
    }
}
