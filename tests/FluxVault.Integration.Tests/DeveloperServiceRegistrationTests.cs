using System.Diagnostics;
using System.Text.Json;

namespace FluxVault.Integration.Tests;

public sealed class DeveloperServiceRegistrationTests
{
    [Theory]
    [InlineData("existing")]
    [InlineData("unsafe")]
    [InlineData("success")]
    [InlineData("native-failure")]
    [InlineData("writer")]
    [InlineData("inherited-writer")]
    public async Task Registration_refuses_unsafe_changes_and_never_starts_runtime(string scenario)
    {
        var repository = FindRoot();
        var workspace = Path.Combine(Path.GetTempPath(), "FluxVaultRegistrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "FluxVault", "service"));
        var executable = Path.Combine(workspace, "FluxVault", "service", "FluxVault.Service.exe");
        await File.WriteAllTextAsync(executable, "inert payload, never executed");
        var script = Path.Combine(workspace, "test.ps1");
        await File.WriteAllTextAsync(script, $$$"""
            $ErrorActionPreference='Stop'
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{{Quote(Path.Combine(repository, "eng/install-service.ps1"))}}}',[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'Installer has parser errors.'}
            foreach($function in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) {
                . ([scriptblock]::Create($function.Extent.Text))
            }
            if(-not (Get-Command Register-FluxVaultService -ErrorAction SilentlyContinue)){throw 'No safe registration flow exists.'}
            $scenario='{{{scenario}}}'
            $env:ProgramFiles='{{{Quote(workspace)}}}';$env:ProgramW6432='{{{Quote(workspace)}}}'
            $script:calls=[Collections.Generic.List[string]]::new()
            function Get-Service { param($Name,$ErrorAction) if($scenario -eq 'existing'){return @{Status='Running'}}; if($script:calls.Count){return @{Status='Stopped'}} }
            function sc.exe { $script:calls.Add('sc '+($args -join ' ')); $global:LASTEXITCODE=if($scenario -eq 'native-failure'){5}else{0} }
            function New-EventLog { $script:calls.Add('event'); }
            if($scenario -ne 'unsafe') {
                function Get-Acl {
                    param($LiteralPath)
                    $sddl='O:SYG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)'
                    if($scenario -eq 'writer'){$sddl+='(A;;GW;;;BU)'}
                    if($scenario -eq 'inherited-writer'){$sddl+='(A;OICIIO;GW;;;BU)'}
                    [pscustomobject]@{Sddl=$sddl}
                }
            }
            $failure=$null
            try {Register-FluxVaultService -RequestedPublishRoot '{{{Quote(Path.Combine(workspace, "FluxVault"))}}}' -Name 'FluxVaultTest' -Source 'FluxVaultTest' | Out-Null}
            catch {$failure=$_.Exception.Message}
            [pscustomobject]@{Calls=@($script:calls);Failure=$failure;Bytes=[IO.File]::ReadAllText('{{{Quote(executable)}}}')} | ConvertTo-Json -Compress
            """);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Registration test did not launch.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(process.ExitCode == 0, await error);
                using var document = JsonDocument.Parse(await output);
                var result = document.RootElement;
                Assert.Equal("inert payload, never executed", result.GetProperty("Bytes").GetString());
                var calls = result.GetProperty("Calls").EnumerateArray().Select(value => value.GetString()!).ToArray();
                var failure = result.GetProperty("Failure").GetString();
                if (scenario is "existing" or "unsafe" or "writer" or "inherited-writer")
                {
                    Assert.Empty(calls);
                    Assert.Contains(scenario == "existing" ? "already exists" : "untrusted", failure);
                }
                else
                {
                    var command = Assert.Single(calls, call => call.StartsWith("sc ", StringComparison.Ordinal));
                    Assert.Contains("create FluxVaultTest", command);
                    Assert.Contains("start= demand", command);
                    Assert.Contains("obj= LocalSystem", command);
                    Assert.DoesNotContain(calls, call => call.Contains("start FluxVaultTest", StringComparison.Ordinal));
                    if (scenario == "native-failure") { Assert.Contains("exit code 5", failure); Assert.DoesNotContain("event", calls); }
                    else Assert.Null(failure);
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(output, error);
            }
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }
    private static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);
    private static string FindRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "FluxVault.slnx"))) return path.FullName;
        throw new DirectoryNotFoundException();
    }
}
