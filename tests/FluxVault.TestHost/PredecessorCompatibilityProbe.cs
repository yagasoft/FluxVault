using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Testing;
using Npgsql;

namespace FluxVault.TestHost;

/// <summary>Actual frozen predecessor readers; confined to the already authenticated owned fixture.</summary>
internal static class PredecessorCompatibilityProbe
{
    internal static async Task RunAsync(NpgsqlConnection connection,VaultCatalogueEntry vault,WindowsDatabaseProbeConfiguration fixture,List<string> checks)
    {
        const string retained=@"C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f\retained-candidates\c4e807860b1c49e798b2380652ca5926";
        var manifestPath=Path.Combine(retained,"candidate.json");
        Check(Hash(manifestPath)=="151024FCF7CDA53ED0345A85707E042276A244B80A7FFE71FFB1275276393BE0","predecessor manifest is the accepted 1.0.7 candidate");
        var appRoot=Path.Combine(retained,"compatibility-app");
        WindowsDatabaseProbeConfiguration.RejectReparseComponents(appRoot);
        using(var manifest=JsonDocument.Parse(File.ReadAllBytes(manifestPath)))
            foreach(var entry in manifest.RootElement.GetProperty("Payload").EnumerateArray())
            {
                var relative=entry.GetProperty("RelativePath").GetString()!;
                if(!relative.StartsWith("app\\",StringComparison.Ordinal))continue;
                if(relative.Contains("..",StringComparison.Ordinal) || relative.Contains(':'))throw new InvalidDataException("Invalid predecessor payload path.");
                var path=Path.Combine(appRoot,relative[4..]);
                WindowsDatabaseProbeConfiguration.RejectReparseComponents(path);
                if(Hash(path)!=entry.GetProperty("Sha256").GetString())throw new InvalidDataException("Predecessor payload changed.");
            }
        var loader=new PredecessorContext(appRoot);
        try
        {
            var oldCore=loader.LoadFromAssemblyPath(Path.Combine(appRoot,"FluxVault.Core.dll"));
            var oldApp=loader.LoadFromAssemblyPath(Path.Combine(appRoot,"FluxVault.App.dll"));
            Type Core(string name)=>oldCore.GetType(name,true)!;
            var endpoint=Activator.CreateInstance(Core("FluxVault.Core.Security.VaultCatalogueEndpoint"),
                Guid.ParseExact(fixture.FixtureId,"N"),"127.0.0.1",fixture.Port,fixture.Database,fixture.Role,5,15,24)!;
            var oldCatalogue=Activator.CreateInstance(Core("FluxVault.Core.Security.PostgreSqlVaultCatalogue"),endpoint,null)!;
            try
            {
                // Invoke the unchanged actual predecessor row readers with the real PostgreSQL result.
                await using var query=new NpgsqlCommand("SELECT vault_id,revision,display_name,owner_sid,grants,binding,configuration FROM fv_control.vault WHERE singleton=true",connection);
                await using var reader=await query.ExecuteReaderAsync();
                Check(await reader.ReadAsync(),"predecessor has a real fixture catalogue row");
                var policy=oldCatalogue.GetType().GetMethod("ReadPolicy",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(oldCatalogue,[reader]);
                var loaded=oldCatalogue.GetType().GetMethod("ReadVault",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(oldCatalogue,[reader,policy])!;
                var configuration=loaded.GetType().GetProperty("Configuration")!.GetValue(loaded)!;
                Check(JsonSerializer.Serialize(configuration,configuration.GetType())==JsonSerializer.Serialize(vault.Configuration),
                    "actual 1.0.7 strict catalogue reader preserves the complete 1.0.8 base configuration");
            }
            finally{await ((IAsyncDisposable)oldCatalogue).DisposeAsync();}
            var root=Path.Combine(fixture.Root,"predecessor-read-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var ordinary=new PendingConfigurationSave(Guid.NewGuid(),vault.Binding.Id.Value,vault.Revision,vault.Configuration,false,[],[]);
            foreach(var (name,pending,readable) in new[]{("ordinary",ordinary,true),
                ("pause",ordinary with {OperationId=Guid.NewGuid(),Configuration=vault.Configuration with{IsEnabled=false},IsProtectionPaused=true},false),
                ("delete",ordinary with {OperationId=Guid.NewGuid(),Origin=ConfigurationSaveOrigin.HistoryDeletion,
                    RemovedSelections=[new(root,RepositoryPurgeScopeKind.RecursiveFolder)],HistoryDeletionFingerprint=new string('A',64)},false)})
            {
                var path=Path.Combine(root,name+".json");new FileConfigurationSaveOperationStore(path).Reserve(pending);
                var before=Hash(path);var store=Activator.CreateInstance(oldApp.GetType("FluxVault.App.Services.FileConfigurationSaveOperationStore",true)!,path)!;
                var didRead=false;
                try{didRead=store.GetType().GetMethod("Read")!.Invoke(store,null) is not null;}
                catch(TargetInvocationException exception) when(exception.InnerException is InvalidDataException){ }
                Check(didRead==readable && Hash(path)==before,"actual 1.0.7 pending reader "+name+" compatibility is known and original bytes are preserved");
            }
            var draftPath=Path.Combine(root,"draft.json");
            new FileProtectionDraftStore(draftPath).Write(null,new(Guid.NewGuid(),Guid.NewGuid(),vault.Binding.Id.Value,vault.Revision,new string('B',64),vault.Configuration));
            var oldDraft=Activator.CreateInstance(oldApp.GetType("FluxVault.App.Services.FileProtectionDraftStore",true)!,draftPath)!;
            Check(oldDraft.GetType().GetMethod("Read")!.Invoke(oldDraft,null) is not null,"actual 1.0.7 local unsent draft remains readable");
            var source=Path.Combine(root,"source.txt");await File.WriteAllTextAsync(source,"owned predecessor recovery bytes "+Guid.NewGuid().ToString("N"));
            var repositoryRoot=Path.Combine(root,"repository");
            var repository=new FileSystemChunkRepository(repositoryRoot,new FastCdcChunker(new()),new(),new());
            await using var content=File.OpenRead(source);
            var committed=await repository.CommitAsync(new("compatibility",source,DateTimeOffset.UtcNow,CaptureConsistency.BestEffort,CompressionPreference.Off,1,content));
            var chunkerType=Core("FluxVault.Core.Chunking.FastCdcChunker");var hasherType=Core("FluxVault.Core.Content.Blake3ContentHasher");var codecType=Core("FluxVault.Core.Content.ZstdChunkCodec");
            var options=Activator.CreateInstance(Core("FluxVault.Core.Chunking.ChunkingOptions"),65536,262144,1048576)!;
            var oldRepository=Core("FluxVault.Core.Storage.FileSystemChunkRepository").GetConstructor([typeof(string),chunkerType,hasherType,codecType,typeof(string)])!
                .Invoke([repositoryRoot,Activator.CreateInstance(chunkerType,options),Activator.CreateInstance(hasherType),Activator.CreateInstance(codecType),null]);
            var listed=await ResultAsync(oldRepository.GetType().GetMethod("ListVersionsAsync")!.Invoke(oldRepository,[CancellationToken.None])!);
            Check(((IEnumerable)listed!).Cast<object>().Any(item=>(string)item.GetType().GetProperty("VersionId")!.GetValue(item)! == committed.Manifest.VersionId),
                "actual 1.0.7 history reader finds the 1.0.8 published version");
            var output=Path.Combine(root,"recovered.txt");
            await ResultAsync(oldRepository.GetType().GetMethod("RestoreAsync",[typeof(string),typeof(string),typeof(CancellationToken)])!.Invoke(oldRepository,[committed.Manifest.VersionId,output,CancellationToken.None])!);
            Check(Hash(source)==Hash(output),"actual 1.0.7 independently SHA256 verified recovery reads 1.0.8 content");
        }
        finally{loader.Unload();}
        void Check(bool okay,string name){if(!okay)throw new InvalidOperationException(name);checks.Add(name);}
    }
    private static async Task<object?> ResultAsync(object value){var task=(Task)value;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
    private static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    private sealed class PredecessorContext(string root):AssemblyLoadContext(isCollectible:true)
    {
        protected override Assembly? Load(AssemblyName name)=>name.Name?.StartsWith("FluxVault.",StringComparison.Ordinal)==true
            ? LoadFromAssemblyPath(Path.Combine(root,name.Name+".dll")) : null;
    }
}
