using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;
using FluxVault.Testing;
using FluxVault.Windows.Security;

namespace FluxVault.TestHost;

internal static class WindowsPackagedIdentityProbe
{
    internal static async Task<int> RunAsync(string configurationPath,string actor,string expectedPackage)
    {
        var fixture=WindowsDatabaseProbeConfiguration.Read(configurationPath);
        using var identity=WindowsIdentity.GetCurrent();
        if (actor is not ("A" or "B") || identity.User?.Value != fixture.Actors[actor] ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("An ordinary owned fixture identity is required.");
        var expectedName="FVGate.Package." + fixture.FixtureId;
        if (!expectedPackage.StartsWith(expectedName+"_1.0.0.0_x64__",StringComparison.Ordinal))
            throw new ArgumentException("The expected package must belong to this exact GUID fixture.");
        var actual=CurrentPackage();
        if (actual != expectedPackage) throw new InvalidOperationException("The process does not have the expected native package identity.");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var client=new NamedPipeFluxVaultClient(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(
            "FluxVault.Tests."+fixture.FixtureId,fixture.Actors["System"]));
        var status=await client.SendAsync(FluxVaultIpcRequest.GetStatus(),deadline.Token);
        var checks=new List<string>{"actual ordinary Windows SID verified","native package full name matches the exact registered identity"};
        if (actor == "B")
        {
            Check(!status.Success && status.ErrorCode == FluxVaultIpcErrorCode.Denied && status.Status is null && status.VaultId is null,
                "package identity cannot grant an ungranted Windows user status or repository identity");
            var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=new(Guid.ParseExact(fixture.FixtureId,"N"))},deadline.Token);
            Check(!history.Success && history.ErrorCode == FluxVaultIpcErrorCode.Denied && history.Versions is null,
                "package identity cannot grant an ungranted Windows user history");
        }
        else
        {
            Check(status.Success && status.VaultId?.Value == Guid.ParseExact(fixture.FixtureId,"N") && status.VaultRevision is > 0,
                "packaged creator receives the original single-vault binding");
            var history=await client.SendAsync(FluxVaultIpcRequest.ListVersions() with {VaultId=status.VaultId},deadline.Token);
            Check(history.Success && history.Versions is {Count:>0},"packaged creator can read history");
            var source=Path.Combine(fixture.Root,"output-A","office","document.docx");
            var version=history.Versions!.Where(v=>v.EntryKind==RepositoryEntryKind.File && v.SourcePath==source)
                .OrderByDescending(v=>v.CapturedAtUtc).First();
            var output=Path.Combine(fixture.Root,"output-A","packaged-recovery-"+Guid.NewGuid().ToString("N")+".docx");
            var restored=await client.SendAsync(FluxVaultIpcRequest.RestoreVersion(version.VersionId,output) with
                {VaultId=status.VaultId,ExpectedVaultRevision=status.VaultRevision,OperationId=Guid.NewGuid()},deadline.Token);
            Check(restored.Success && restored.RestoreResult?.VerifiedLogicalBytes==new FileInfo(source).Length &&
                Hash(source)==Hash(output),"packaged creator publishes independently verified caller-authorised recovery");
        }
        Console.WriteLine(JsonSerializer.Serialize(new {Actor=actor,WindowsSid=identity.User!.Value,
            PackageFullName=actual,Passed=checks.Count,Checks=checks,NativePackagedVerified=true}));
        return 0;
        void Check(bool result,string name) {if(!result)throw new InvalidOperationException("Packaged identity contract failed: "+name);checks.Add(name);}
    }
    private static string Hash(string path) {using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    private static string CurrentPackage()
    {
        uint length=0;
        if(GetCurrentPackageFullName(ref length,null)!=122 || length is <1 or >1024)
            throw new InvalidOperationException("The apphost has no native package identity.");
        var value=new StringBuilder((int)length);
        if(GetCurrentPackageFullName(ref length,value)!=0)throw new InvalidOperationException("Native package identity could not be read.");
        return value.ToString();
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length,StringBuilder? fullName);
}
