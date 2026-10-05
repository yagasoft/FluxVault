# Read-only closeout of the artifact-only preparations; never stops an unowned process.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $repo 'eng/fixtures/vault-windows-fixture.psm1') -Force
Import-VaultFixtureJobType
$identities=[Collections.Generic.Dictionary[string,hashtable]]::new()
$roots=[Collections.Generic.List[hashtable]]::new()
$databases=[Collections.Generic.List[hashtable]]::new()
$directories=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'artifacts') -Directory)+
    @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'integrity-refresh') -Directory)
foreach($directory in $directories) {
    $journalPath=Join-Path $directory.FullName 'owner.json'
    if(-not(Test-Path -LiteralPath $journalPath)){$journalPath=Join-Path $directory.FullName 'completed-owner.json'}
    $journal=Get-Content -LiteralPath $journalPath -Raw|ConvertFrom-Json -AsHashtable
    $root=Resolve-VaultFixtureRoot $journal.Root $journal.Parent $journal.FixtureId
    if($directory.Name -ne $journal.FixtureId -or $journal.State -ne 'Complete' -or @($journal.Resources|Where-Object State -notin @('Removed','Absent')).Count){throw 'Preparation ownership is incomplete.'}
    $captured=@($journal.RunnerIdentity)+@($journal.Resources|Where-Object Kind -in @('Process','Postmaster')|ForEach-Object Identity)
    foreach($file in Get-ChildItem -LiteralPath $directory.FullName -Filter 'tool-*-child.json') {$captured+=@(Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable)}
    $cleanupProcess=Join-Path $directory.FullName 'output-cleanup-process.json'
    if(Test-Path -LiteralPath $cleanupProcess){$captured+=@(Get-Content -LiteralPath $cleanupProcess -Raw|ConvertFrom-Json -AsHashtable)}
    foreach($identity in $captured) {
        if(-not $identity.ContainsKey('ProcessId')){continue}
        $key=[string]$identity.ProcessId+'|'+$identity.StartedUtc+'|'+$identity.Executable
        $process=Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
        $live=$false
        if($null -ne $process){try{$live=$process.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$identity.StartedUtc).UtcDateTime -and $process.Path -eq $identity.Executable}finally{$process.Dispose()}}
        if($live){throw 'A recorded preparation process remains.'}
        $identities[$key]=@{ProcessId=$identity.ProcessId;StartedUtc=$identity.StartedUtc;Executable=$identity.Executable;ExactIdentityAbsent=$true}
    }
    foreach($resource in $journal.Resources|Where-Object Kind -eq 'Job') {
        $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open($resource.Identity.KernelName,$resource.Identity.OwnerSid)
        try{if($null -ne $job){throw 'A preparation job still exists.'}}finally{if($job){$job.Dispose()}}
    }
    $cleanupOwner=Join-Path $directory.FullName 'output-cleanup-owner.json'
    if(Test-Path -LiteralPath $cleanupOwner) {
        $owner=Get-Content -LiteralPath $cleanupOwner -Raw|ConvertFrom-Json -AsHashtable
        if($owner.State -ne 'Complete'){throw 'Output cleanup ownership is incomplete.'}
        $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open($owner.KernelName,$owner.OwnerSid)
        try{if($null -ne $job){throw 'An output cleanup job remains.'}}finally{if($job){$job.Dispose()}}
    }
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $directory.FullName 'database-intents') -File -ErrorAction SilentlyContinue) {
        $intent=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable
        if($intent.State -ne 'Removed' -or $intent.InstanceId -ne $journal.FixtureId){throw 'Database intent is not retired.'}
        $databases.Add(@{FixtureId=$journal.FixtureId;Database=$intent.Database;State=$intent.State})
    }
    if(Test-Path -LiteralPath $root){throw 'A preparation private root remains.'}
    $roots.Add(@{FixtureId=$journal.FixtureId;RootAbsent=$true;NamedJobsAbsent=$true})
}
$keyRecords=@(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'imported-key-cleanup.json'))+
    @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'artifacts') -Filter 'native-key-cleanup.json' -Recurse -File)
$removedKeys=[Collections.Generic.List[hashtable]]::new()
$provider=[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
foreach($file in $keyRecords) {
    $record=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable
    if($record.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or $record.State -ne 'Removed'){throw 'Signing-key ownership is incomplete.'}
    $keys=$(if($record.ContainsKey('OwnedKeys')){$record.OwnedKeys}else{$record['Keys']})
    foreach($key in $keys) {
        if($key.State -ne 'Removed' -or [Security.Cryptography.CngKey]::Exists($key.KeyName,$provider,[Security.Cryptography.CngKeyOpenOptions]::Silent)) {throw 'An owned signing key remains.'}
        if($key.UniqueName -notmatch '^[a-fA-F0-9]{32}_[a-fA-F0-9-]{36}$'){throw 'Signing key file identity changed.'}
        $keyFile=Join-Path ([Environment]::GetFolderPath('ApplicationData')) ('Microsoft/Crypto/Keys/'+$key.UniqueName)
        if(Test-Path -LiteralPath $keyFile){throw 'An owned signing-key backing file remains.'}
        $removedKeys.Add(@{KeyName=$key.KeyName;UniqueName=$key.UniqueName;NativeAbsent=$true;BackingFileAbsent=$true})
    }
}
$enumeration=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cng-enumeration.json') -Raw|ConvertFrom-Json -AsHashtable
$process=Get-Process -Id $enumeration.ProcessId -ErrorAction SilentlyContinue
if($null -ne $process){try{if($process.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$enumeration.StartedUtc).UtcDateTime -and $process.Path -eq $enumeration.Executable){throw 'The read-only enumeration tool remains.'}}finally{$process.Dispose()}}
$identities[([string]$enumeration.ProcessId+'|'+$enumeration.StartedUtc+'|'+$enumeration.Executable)]=@{ProcessId=$enumeration.ProcessId;StartedUtc=$enumeration.StartedUtc;Executable=$enumeration.Executable;ExactIdentityAbsent=$true}
$prior=Get-Content -LiteralPath (Join-Path $repo 'docs/verification/2026-10-05-next002-native-access/native/ccabb1b8008d492986e30a7fca83f2f8/before.json') -Raw|ConvertFrom-Json
$current=@{Services=@(foreach($old in $prior.Services){$service=Get-CimInstance Win32_Service -Filter "Name='$($old.Name)'";$process=Get-Process -Id $service.ProcessId;try{@{Name=$service.Name;StartName=$service.StartName;PathName=$service.PathName;Identity=(Get-VaultFixtureProcessIdentity $process)}}finally{$process.Dispose()}});
    Files=@(foreach($old in $prior.Files){@{Path=$old.Path;Sha256=(Get-FileHash -LiteralPath $old.Path).Hash}})}
$normalUnchanged=Test-VaultFixtureInstallationUnchanged $prior $current
if(-not $normalUnchanged){throw 'Normal installation changed from the recorded live baseline.'}
$prerequisites=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'read-only-prerequisites.json') -Raw|ConvertFrom-Json
$runnerTrust=@(Get-ChildItem Cert:\CurrentUser\TrustedPeople|ForEach-Object Thumbprint|Sort-Object)
$machineTrust=@(Get-ChildItem Cert:\LocalMachine\TrustedPeople|ForEach-Object Thumbprint|Sort-Object)
$trustUnchanged=[string]::Join(',',@($prerequisites.NormalUserTrustedPeople|Sort-Object)) -eq [string]::Join(',',$runnerTrust) -and
    [string]::Join(',',@($prerequisites.MachineTrustedPeople|Sort-Object)) -eq [string]::Join(',',$machineTrust)
if(-not $trustUnchanged){throw 'Normal runner or machine certificate trust changed.'}
$packages=@(Get-AppxPackage -AllUsers -Name 'FVGate.Package.*')
$profiles=@(Get-CimInstance Win32_UserProfile|Where-Object LocalPath -match '\\FVGate[AB]_261003$')
$users=@(Get-LocalUser|Where-Object Name -in @('FVGateA_261003','FVGateB_261003'))
$groups=@(Get-LocalGroup -Name 'FVGate_261003' -ErrorAction SilentlyContinue)
$tasks=@(Get-ScheduledTask -TaskName 'FluxVault-NEXT002-261003-SYSTEM' -ErrorAction SilentlyContinue)
if($packages.Count -or $profiles.Count -or $users.Count -or $groups.Count -or $tasks.Count){throw 'A fixture installation resource remains.'}
$primary=@(foreach($pair in @(@{Name='fluxvault-index.json';Hash='9752DDF184A20EF4C574BB462083265C2FD3126153F73F8C2783F6A5FD1A5911'},
    @{Name='fluxvault-index.ps1';Hash='19BDE2B0593DA98696FC768F0DD0A68C08AE939D718B08B57891511E35F1CFE0'})) {
    $path=Join-Path 'E:\Drive\Work (1)\Code\FluxVault\scripts' $pair.Name
    @{Path=$path;Unchanged=((Get-FileHash -LiteralPath $path).Hash -eq $pair.Hash)}
})
if(@($primary|Where-Object Unchanged -ne $true).Count){throw 'Unrelated primary checkout edits changed.'}
@{ObservedUtc=[DateTime]::UtcNow.ToString('o');Roots=@($roots);CapturedProcesses=@($identities.Values);CapturedCount=$identities.Count;
    NormalInstallationUnchanged=$normalUnchanged;NormalCertificateStoresUnchanged=$trustUnchanged;RemovedSigningKeys=@($removedKeys);
    DatabaseIntents=@($databases);DatabaseIntentCount=$databases.Count;
    FixturePackagesAbsent=$true;FixtureProfilesAbsent=$true;FixtureAccountsAbsent=$true;FixtureGroupsAbsent=$true;FixtureTasksAbsent=$true;UnrelatedPrimaryEdits=$primary}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifact-process-census.json')
Write-Output ('Verified '+$roots.Count+' private roots and '+$identities.Count+' exact process identities absent; normal installation/trust and unrelated edits unchanged.')
