# Builds only; never installs, signs, registers, provisions, changes authentication or starts services.
#Requires -Version 7.2
[CmdletBinding()]
param([string]$CandidateId='51b103b3c75645dfaa36600e2cbe07a5',
    [version]$ProductVersion='1.0.7.0')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($CandidateId -cnotmatch '^[0-9a-f]{32}$' -or [guid]::ParseExact($CandidateId,'N') -eq [guid]::Empty){throw 'A canonical nonempty candidate identity is required.'}
if($ProductVersion.Major -gt 255 -or $ProductVersion.Minor -gt 255 -or $ProductVersion.Build -lt 0 -or $ProductVersion.Build -gt 65535 -or $ProductVersion.Revision -ne 0){throw 'A four-part MSI version with zero revision is required.'}
$candidateRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$candidateRoot=[IO.Path]::GetFullPath((Join-Path $candidateRepo ('artifacts/staging-single-vault/'+$CandidateId)))
if(-not $candidateRoot.StartsWith($candidateRepo+'\artifacts\staging-single-vault\',[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate root is outside the checked workspace.'}
for($ancestor=[IO.DirectoryInfo]::new($candidateRoot).Parent;$null -ne $ancestor;$ancestor=$ancestor.Parent){
    if($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Candidate ancestor is a reparse point.'}
}
if(Test-Path -LiteralPath $candidateRoot){throw 'Candidate root already exists; preserve it and use a fresh reviewed build identity.'}
$sourceCommit=(& git -C $candidateRepo rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'Source revision could not be read.'}
$sourceChanges=@(& git -C $candidateRepo status --porcelain)
if($LASTEXITCODE -ne 0 -or $sourceChanges.Count){throw 'Commit the reviewed candidate sources before freezing a build.'}
[IO.Directory]::CreateDirectory($candidateRoot) | Out-Null
$publish=Join-Path $candidateRoot 'publish'
$staging=Join-Path $candidateRoot 'installer'
[IO.Directory]::CreateDirectory($staging) | Out-Null
$dotnetPath=(Get-Command dotnet -CommandType Application).Source
$launches=[Collections.Generic.List[object]]::new()
$candidateResult=@{CandidateId=$CandidateId;SourceCommit=$sourceCommit;Root=$candidateRoot;Version=$ProductVersion.ToString();Launches=$launches;Failure=$null}

function Invoke-CandidateBuild([string]$Step,[string[]]$Arguments){
    $start=[Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $start.WorkingDirectory=$candidateRepo;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.Environment['MSBUILDDISABLENODEREUSE']='1';$start.Environment['DOTNET_CLI_USE_MSBUILD_SERVER']='0'
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    if($null -eq $process){throw "Candidate step '$Step' did not launch."}
    $record=@{Step=$Step;ProcessId=$process.Id;StartedUtc=$process.StartTime.ToUniversalTime().ToString('O');Executable=$dotnetPath;Exited=$false;ExitCode=$null}
    $launches.Add($record)
    $output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync()
    try{
        if(-not $process.WaitForExit(120000)){throw "Candidate step '$Step' exceeded its build deadline."}
        $record.ExitCode=$process.ExitCode
    }finally{
        if(-not $process.HasExited){$process.Kill($true)}
        if(-not $process.WaitForExit(10000)){throw "Candidate step '$Step' did not exit after cleanup."}
        $record.Exited=$process.HasExited
        $log=$output.GetAwaiter().GetResult()+$errorOutput.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $candidateRoot ($Step+'.log')),$log)
        $process.Dispose()
    }
    if($record.ExitCode -ne 0){throw "Candidate step '$Step' failed (exit $($record.ExitCode)); preserve its log."}
    if($log -match '(?m)^\s*[1-9]\d*\s+Warning\(s\)' -or $log -match ':\s*warning\s+[A-Z]+\d+:'){throw "Candidate step '$Step' introduced a build warning."}
    Write-Host "Candidate step $Step joined successfully."
}

try{
    foreach($product in @('Service','App','Cli')){
        Invoke-CandidateBuild -Step ('publish-'+$product.ToLowerInvariant()) -Arguments @('publish',
            (Join-Path $candidateRepo ('src/FluxVault.'+$product+'/FluxVault.'+$product+'.csproj')),
            '-c','Release','--runtime','win-x64','--self-contained','false','--output',(Join-Path $publish $product.ToLowerInvariant()),
            '--disable-build-servers','-p:UseSharedCompilation=false','-nodeReuse:false')
    }
    foreach($project in @('Installer','Bundle')){
        Invoke-CandidateBuild -Step ('build-'+$project.ToLowerInvariant()) -Arguments @('build',
            (Join-Path $candidateRepo ('installer/wix/FluxVault.'+$project+'/FluxVault.'+$project+'.wixproj')),
            '-c','Release','--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false',
            ('-p:PublishRoot='+$publish),('-p:ProductVersion='+$ProductVersion),('-p:ReleasePackageRoot='+$staging),
            ('-p:OutputPath='+$staging+'\'),('-p:BaseIntermediateOutputPath='+(Join-Path $candidateRoot ('obj-'+$project.ToLowerInvariant()))+'\'))
    }
    $payload=@(Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{RelativePath=[IO.Path]::GetRelativePath($publish,$_.FullName);Length=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    $candidateResult.Payload=$payload
    $candidateResult.Installer=@(foreach($name in @('FluxVault.Installer.msi','FluxVault.Setup.exe')){
        $path=Join-Path $staging $name
        @{Path=$path;Length=(Get-Item -LiteralPath $path).Length;Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    })
}catch{
    $candidateResult.Failure=$_.Exception.Message
    throw
}finally{
    $candidateResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $candidateRoot 'candidate.json')
}
Write-Host "Frozen unsigned candidate prepared at $candidateRoot. No installation operation performed."
