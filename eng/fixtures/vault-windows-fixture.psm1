#Requires -Version 7.2
Set-StrictMode -Version Latest

function Assert-FixtureNoReparse {
    param([string]$Path)
    for ($current = [IO.Path]::GetFullPath($Path); -not [string]::IsNullOrEmpty($current); $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Fixture path contains a reparse point; no target was changed.'
            }
        }
    }
}

function Resolve-VaultFixtureRoot {
    param([string]$Root, [string]$Parent, [string]$FixtureId)
    $parsed = [guid]::Empty
    if (-not [guid]::TryParseExact($FixtureId, 'N', [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw 'Invalid fixture UUID.' }
    if (-not [IO.Path]::IsPathFullyQualified($Root) -or -not [IO.Path]::IsPathFullyQualified($Parent)) { throw 'Absolute fixture paths are required.' }
    $expected = [IO.Path]::GetFullPath((Join-Path $Parent $FixtureId))
    $resolved = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    if (-not [string]::Equals($resolved, $expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture root is outside its exact UUID parent.' }
    Assert-FixtureNoReparse $resolved
    return $resolved
}

function Assert-VaultFixturePostmasterLaunch {
    param([string]$Root, [string]$Executable, [string[]]$Arguments, [string]$InputText)
    $parent = 'C:\ProgramData\FluxVault.Tests\NEXT002'
    $resolved = Resolve-VaultFixtureRoot -Root $Root -Parent $parent -FixtureId ([IO.Path]::GetFileName([IO.Path]::TrimEndingDirectorySeparator($Root)))
    $expected = @('-D', (Join-Path $resolved 'data'), '-l', (Join-Path $resolved 'postgres.log'), '-w', '-t', '30', 'start')
    if (-not [string]::Equals($Executable, (Join-Path $resolved 'postgresql/bin/pg_ctl.exe'), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::IsNullOrEmpty($InputText) -or $Arguments.Count -ne $expected.Count) {
        throw 'Unreviewed daemon starter refused.'
    }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        $comparison = if ($index -in @(1, 3)) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not [string]::Equals($Arguments[$index], $expected[$index], $comparison)) { throw 'Unreviewed daemon starter refused.' }
    }
}

function Read-VaultFixtureLog {
    param([string]$Path, [long]$StartOffset = 0, [long]$EndOffset = -1)
    Assert-FixtureNoReparse $Path
    $stream = [IO.FileStream]::new($Path, 'Open', 'Read', 'ReadWrite')
    try {
        if ($EndOffset -eq -1) { $EndOffset = $stream.Length }
        if ($StartOffset -lt 0 -or $EndOffset -lt $StartOffset -or $EndOffset -gt $stream.Length -or $EndOffset - $StartOffset -gt 4MB) {
            throw 'Fixture log window is truncated, invalid or oversized.'
        }
        $bytes = [byte[]]::new([int]($EndOffset - $StartOffset))
        $stream.Position = $StartOffset
        $stream.ReadExactly($bytes)
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    } finally { $stream.Dispose() }
}

function Assert-VaultFixtureSspiRefusal {
    param([string]$LogWindow, [string]$ExpectedSid, [ValidateSet('127.0.0.1','::1')][string]$HostAddress)
    $records = [regex]::Matches($LogWindow, '(?m)^\[(?<pid>\d+)\] .*? (?<level>LOG|FATAL|DETAIL):  (?<message>[^\r\n]*)$')
    $principals = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $connections = 0
    foreach ($backend in $records | Group-Object { $_.Groups['pid'].Value }) {
        $messages = @($backend.Group | ForEach-Object { $_.Groups['message'].Value })
        $received = @($messages | Where-Object { $_ -match '^connection received: host=' })
        if (-not $received.Count) { continue }
        $connections++
        $authenticated = @($messages | Where-Object { $_ -match '^connection authenticated:' })
        $refusals = @($messages | Where-Object { $_ -match '^no match in usermap' })
        if ($received.Count -ne 1 -or $received[0] -notmatch ('^connection received: host=' + [regex]::Escape($HostAddress) + ' port=\d+$') -or
            $authenticated.Count -ne 1 -or $refusals.Count -ne 1 -or
            @($messages | Where-Object { $_ -match '^connection authorized:' }).Count -or
            'SSPI authentication failed for user "fv_gate_service"' -notin $messages) { throw 'Contradictory or incomplete owned SSPI refusal.' }
        $identity = [regex]::Match($authenticated[0], '^connection authenticated: identity="([^"\r\n]+)" method=sspi ')
        $mapped = [regex]::Match($refusals[0], '^no match in usermap "fv_gate_system" for user "fv_gate_service" authenticated as "([^"\r\n]+)"$')
        if (-not $identity.Success -or -not $mapped.Success) { throw 'Owned refusal did not establish the exact SSPI map and role.' }
        $principal = $mapped.Groups[1].Value
        $separator = $principal.LastIndexOf('@')
        if ($principal -notmatch '^[A-Za-z0-9_@$ .\\-]{1,256}$' -or $separator -le 0 -or $separator -eq $principal.Length - 1) { throw 'Qualified SSPI principal is missing.' }
        $mappedWindowsName = $principal.Substring($separator + 1) + '\' + $principal.Substring(0, $separator)
        foreach ($windowsName in @($identity.Groups[1].Value, $mappedWindowsName)) {
            $sid = [Security.Principal.NTAccount]::new($windowsName).Translate([Security.Principal.SecurityIdentifier]).Value
            if ($sid -ne $ExpectedSid) { throw 'Owned SSPI refusal authenticated another Windows SID.' }
        }
        $null = $principals.Add($principal)
    }
    if (-not $connections -or $principals.Count -ne 1) { throw 'A single owned qualified SSPI principal could not be established.' }
    return @($principals)[0]
}

function Assert-FixtureResource {
    param([hashtable]$Resource)
    foreach ($key in $Resource.Keys) {
        if ($key -notin @('Kind', 'Name', 'State', 'Identity')) { throw 'Unknown fixture resource field; secrets cannot be journalled.' }
    }
    $allowed = switch ($Resource.Kind) {
        'Account' { @('Sid') }
        'Group' { @('Sid') }
        'Task' { @('DefinitionSha256', 'InstanceId') }
        'Process' { @('ProcessId', 'StartedUtc', 'Executable') }
        'Postmaster' { @('ProcessId', 'StartedUtc', 'Executable', 'DataDirectory', 'Port') }
        'Job' { @('KernelName','OwnerSid') }
        default { throw 'Unknown fixture resource kind.' }
    }
    if ($Resource.Name -notmatch '^[A-Za-z0-9_-]{1,80}$' -or $Resource.State -notin @('Intent', 'Created', 'Removed', 'Absent')) { throw 'Invalid fixture resource.' }
    foreach ($key in $Resource.Identity.Keys) {
        if ($key -notin $allowed) { throw 'Unknown fixture identity field; secrets cannot be journalled.' }
    }
    if ($Resource.State -notin @('Intent','Absent') -and @($allowed | Where-Object { -not $Resource.Identity.ContainsKey($_) }).Count) {
        throw 'Created resource identity is incomplete.'
    }
}

function Save-FixtureJournal {
    param([hashtable]$Journal, [switch]$New)
    foreach ($key in $Journal.Keys) {
        if ($key -notin @('Version', 'FixtureId', 'Root', 'Parent', 'Path', 'CreatedUtc', 'State', 'Resources', 'RunnerIdentity')) { throw 'Unknown fixture journal field; secrets cannot be journalled.' }
    }
    if ($Journal.Resources.Count -gt 64) { throw 'Fixture ownership journal exceeds its resource bound.' }
    if($Journal.ContainsKey('RunnerIdentity')) { Assert-FixtureResource @{Kind='Process';Name='runner';State='Created';Identity=$Journal.RunnerIdentity} }
    $root = Resolve-VaultFixtureRoot $Journal.Root $Journal.Parent $Journal.FixtureId
    if ($Journal.Path -ne (Join-Path $root 'owner.json')) { throw 'Fixture journal path changed.' }
    foreach ($resource in $Journal.Resources) { Assert-FixtureResource $resource }
    if ($New -and (Test-Path -LiteralPath $Journal.Path)) { throw 'Existing fixture ownership journal must not be adopted.' }
    Assert-FixtureNoReparse $Journal.Path
    $temporary = Join-Path $root ('.owner-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Journal | ConvertTo-Json -Depth 8))
    try {
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($temporary, $Journal.Path, -not $New)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function New-VaultFixtureJournal {
    param([string]$Root, [string]$Parent, [string]$FixtureId)
    $resolved = Resolve-VaultFixtureRoot $Root $Parent $FixtureId
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw 'Create the protected fixture root before its ownership journal.' }
    $journal = @{ Version = 1; FixtureId = $FixtureId; Root = $resolved; Parent = [IO.Path]::GetFullPath($Parent)
        Path = (Join-Path $resolved 'owner.json'); CreatedUtc = [DateTime]::UtcNow.ToString('o'); State = 'Prepared'; Resources = @() }
    $runner=Get-Process -Id $PID
    try {$journal.RunnerIdentity=Get-VaultFixtureProcessIdentity $runner} finally {$runner.Dispose()}
    Save-FixtureJournal $journal -New
    return $journal
}

function Add-VaultFixtureIntent {
    param($Journal, [string]$Kind, [string]$Name, [hashtable]$Identity = @{})
    if ($Journal.State -ne 'Prepared') { throw 'Closed fixture journal cannot admit another resource.' }
    if (@($Journal.Resources | Where-Object { $_.Kind -eq $Kind -and $_.Name -eq $Name }).Count) { throw 'Fixture resource is already recorded.' }
    $resource = @{ Kind = $Kind; Name = $Name; State = 'Intent'; Identity = $Identity }
    Assert-FixtureResource $resource
    $Journal.Resources += @($resource)
    Save-FixtureJournal $Journal
}

function Read-VaultFixtureJournal {
    param([string]$Root, [string]$Parent, [string]$FixtureId)
    $resolved = Resolve-VaultFixtureRoot $Root $Parent $FixtureId
    $path = Join-Path $resolved 'owner.json'
    Assert-FixtureNoReparse $path
    if ((Get-Item -LiteralPath $path).Length -gt 65536) { throw 'Fixture ownership journal exceeds its bound.' }
    $journal = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 8
    foreach ($key in $journal.Keys) {
        if ($key -notin @('Version', 'FixtureId', 'Root', 'Parent', 'Path', 'CreatedUtc', 'State', 'Resources', 'RunnerIdentity')) { throw 'Unknown fixture journal field.' }
    }
    if ($journal.Version -ne 1 -or $journal.FixtureId -ne $FixtureId -or $journal.Root -ne $resolved -or
        $journal.Parent -ne [IO.Path]::GetFullPath($Parent) -or $journal.Path -ne $path -or
        $journal.State -notin @('Prepared', 'Complete') -or $journal.Resources.Count -gt 64) { throw 'Fixture ownership journal identity changed.' }
    $journal.CreatedUtc = ([DateTimeOffset]$journal.CreatedUtc).UtcDateTime.ToString('o')
    if($journal.ContainsKey('RunnerIdentity')){
        Assert-FixtureResource @{Kind='Process';Name='runner';State='Created';Identity=$journal.RunnerIdentity}
        $journal.RunnerIdentity.StartedUtc=([DateTimeOffset]$journal.RunnerIdentity.StartedUtc).UtcDateTime.ToString('o')
    }
    foreach ($resource in $journal.Resources) {
        Assert-FixtureResource $resource
        if ($resource.Identity.ContainsKey('StartedUtc')) {
            $resource.Identity.StartedUtc = ([DateTimeOffset]$resource.Identity.StartedUtc).UtcDateTime.ToString('o')
        }
    }
    return $journal
}

function Set-VaultFixtureResourceState {
    param([hashtable]$Journal, [string]$Kind, [string]$Name,
        [ValidateSet('Created', 'Removed')][string]$State, [hashtable]$Identity)
    $matches = @($Journal.Resources | Where-Object { $_.Kind -eq $Kind -and $_.Name -eq $Name })
    if ($matches.Count -ne 1 -or $Journal.State -ne 'Prepared') { throw 'Fixture resource intent is missing or closed.' }
    $resource = $matches[0]
    if ($State -eq 'Removed' -and $resource.State -ne 'Created') { throw 'Unconfirmed resource cannot be marked removed.' }
    if ($State -eq 'Created' -and $resource.State -ne 'Intent') { throw 'Resource was already created.' }
    $updated = @{ Kind = $Kind; Name = $Name; State = $State; Identity = $Identity }
    Assert-FixtureResource $updated
    if ($State -eq 'Removed') {
        if ($resource.Identity.Count -ne $Identity.Count) { throw 'Resource identity changed before removal.' }
        foreach ($key in $resource.Identity.Keys) {
            if (-not $Identity.ContainsKey($key) -or $resource.Identity[$key] -ne $Identity[$key]) { throw 'Resource identity changed before removal.' }
        }
    }
    $resource.State = $State
    $resource.Identity = $Identity
    Save-FixtureJournal $Journal
}

function Complete-VaultFixtureJournal {
    param([hashtable]$Journal)
    if (@($Journal.Resources | Where-Object { $_.State -notin @('Removed','Absent') }).Count) { throw 'Unresolved fixture resources remain.' }
    $Journal.State = 'Complete'
    Save-FixtureJournal $Journal
}

function Set-VaultFixtureTaskInstance {
    param([hashtable]$Journal, [string]$Name, [string]$DefinitionSha256, [string]$InstanceId)
    $resources = @($Journal.Resources | Where-Object { $_.Kind -eq 'Task' -and $_.Name -eq $Name })
    $parsed = [guid]::Empty
    if ($resources.Count -ne 1 -or $resources[0].State -ne 'Created' -or
        $resources[0].Identity.DefinitionSha256 -ne $DefinitionSha256 -or
        -not [guid]::TryParse($InstanceId, [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw 'Owned task definition or instance identity changed.' }
    # The caller has joined the prior scheduler instance before publishing this new mission.
    $resources[0].Identity.InstanceId = $InstanceId
    Save-FixtureJournal $Journal
}

function Remove-VaultFixtureTree {
    param([string]$Root, [string]$Parent, [string]$FixtureId)
    $resolved = Resolve-VaultFixtureRoot $Root $Parent $FixtureId
    $journal = Read-VaultFixtureJournal $resolved $Parent $FixtureId
    if ($journal.State -ne 'Complete' -or @($journal.Resources | Where-Object { $_.State -notin @('Removed','Absent') }).Count) {
        throw 'Unresolved fixture ownership remains; directory was retained.'
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($resolved)
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture cleanup refuses a reparse entry; no target was changed.' }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
    # Call only after all fixture users/processes have been removed and the protected root is quiescent.
    Remove-Item -LiteralPath $resolved -Recurse
    if (Test-Path -LiteralPath $resolved) { throw 'Fixture directory remains.' }
}

function Set-VaultFixtureIntentAbsent {
    param([hashtable]$Journal, [string]$Kind, [string]$Name)
    $matches = @($Journal.Resources | Where-Object { $_.Kind -eq $Kind -and $_.Name -eq $Name })
    if ($matches.Count -ne 1 -or $matches[0].State -ne 'Intent' -or $Journal.State -ne 'Prepared') { throw 'Only an unresolved creation intent can be resolved as absent.' }
    # The protected runner must first recheck the exact external resource; this never substitutes for a removal.
    $matches[0].State = 'Absent'
    Save-FixtureJournal $Journal
}

function Get-VaultFixtureProcessIdentity {
    param([Diagnostics.Process]$Process)
    Import-VaultFixtureJobType
    return @{ ProcessId = $Process.Id; StartedUtc = $Process.StartTime.ToUniversalTime().ToString('o'); Executable = [FluxVault.Fixtures.WindowsProcessIdentity]::ImagePath($Process) }
}

function Get-VaultFixtureLiveProcessIdentity {
    param([Diagnostics.Process]$Process)
    if($Process.HasExited){return $null}
    try{return Get-VaultFixtureProcessIdentity $Process}
    catch{if($Process.HasExited){return $null};throw}
}

function Stop-VaultFixtureProcess {
    param([hashtable]$Identity, [ValidateRange(1, 30)][int]$ExitTimeoutSeconds = 5)
    $process = Get-Process -Id $Identity.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    try {
        Import-VaultFixtureJobType
        if ($process.StartTime.ToUniversalTime().ToString('o') -ne $Identity.StartedUtc -or
            -not [string]::Equals([FluxVault.Fixtures.WindowsProcessIdentity]::ImagePath($process), $Identity.Executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Process identity changed; refusing to stop it.'
        }
        if (-not $process.HasExited) { $process.Kill($true) }
        if (-not $process.WaitForExit($ExitTimeoutSeconds * 1000)) { throw 'Owned fixture process did not exit.' }
    } finally { $process.Dispose() }
}

function Assert-VaultFixtureTrustedPath {
    param([string]$Path, [string]$AdditionalTrustedOwnerSid)
    Assert-FixtureNoReparse $Path
    $trusted = @('S-1-5-18', 'S-1-5-32-544', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if($AdditionalTrustedOwnerSid) {
        if($AdditionalTrustedOwnerSid -notin @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object {$_.SID.Value})) {
            throw 'Additional fixture owner is not a direct local administrator.'
        }
        $trusted += $AdditionalTrustedOwnerSid
    }
    $target = [IO.Path]::GetFullPath($Path)
    $protectedNext = $null
    for ($current = $target; -not [string]::IsNullOrEmpty($current); $current = [IO.Path]::GetDirectoryName($current)) {
        if (-not (Test-Path -LiteralPath $current)) { throw 'Trusted fixture component is missing.' }
        $acl = Get-Acl -LiteralPath $current
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Fixture component has an untrusted owner.' }
        $dangerous = [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
        if ($current -eq $target) {
            $dangerous = $dangerous -bor [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor
                [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes
        } elseif ($null -eq $protectedNext -or -not [IO.Directory]::Exists($current) -or -not (Test-Path -LiteralPath $protectedNext)) {
            throw 'Trusted ancestor has lost its protected next component.'
        }
        # A checked child cannot be deleted/replaced. It anchors this nonempty ancestor, so
        # creating siblings or writing directory attributes cannot convert it to a reparse point.
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
                $rule.IdentityReference.Value -notin $trusted -and ($rule.FileSystemRights -band $dangerous)) {
                throw 'Privileged fixture component or ancestor permits standard-user write/replacement.'
            }
        }
        $protectedNext = $current
    }
}

function New-VaultFixtureProtectedDirectory {
    param([string]$Path, [string[]]$ReadSids = @(), [string[]]$ModifySids = @(), [switch]$TraverseUsers)
    Assert-FixtureNoReparse $Path
    if (Test-Path -LiteralPath $Path) { throw 'Protected fixture directory already exists.' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($owner)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544', $owner.Value) | Select-Object -Unique) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    foreach ($sid in $ReadSids) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    foreach ($sid in $ModifySids) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    if ($TraverseUsers) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Traverse', 'None', 'None', 'Allow')) }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path), $acl)
}

function Import-VaultFixtureJobType {
    if ($null -eq ('FluxVault.Fixtures.OwnedWindowsJob' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'owned-windows-job.cs') }
}

function Add-VaultFixtureToolIntent {
    param([hashtable]$Journal,[string]$Executable)
    $name='tool-'+[guid]::NewGuid().ToString('N')
    Add-VaultFixtureIntent $Journal Process $name @{StartedUtc=[DateTime]::UtcNow.ToString('o');Executable=$Executable}
    return $name
}

function Test-VaultFixtureInstallationUnchanged {
    param($Expected,$Actual)
    foreach($original in $Expected.Services) {
        $current=@($Actual.Services | Where-Object {$_.Name -eq $original.Name})
        if($current.Count -ne 1 -or $current[0].StartName -ne $original.StartName -or $current[0].PathName -ne $original.PathName){return $false}
        foreach($key in @('ProcessId','Executable')){if($current[0].Identity.$key -ne $original.Identity.$key){return $false}}
        if(([DateTimeOffset]$current[0].Identity.StartedUtc).UtcDateTime -ne ([DateTimeOffset]$original.Identity.StartedUtc).UtcDateTime){return $false}
    }
    foreach($original in $Expected.Files){$current=@($Actual.Files | Where-Object {$_.Path -eq $original.Path});if($current.Count -ne 1 -or $current[0].Sha256 -ne $original.Sha256){return $false}}
    return $true
}

function Get-VaultFixtureProcessTreeIdentity {
    param([hashtable]$Identity)
    $root = Get-Process -Id $Identity.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $root) { return }
    try {
        $actual = Get-VaultFixtureProcessIdentity $root
        if ($actual.StartedUtc -ne $Identity.StartedUtc -or $actual.Executable -ne $Identity.Executable) { throw 'Process identity changed before descendant discovery.' }
        $identities = [Collections.Generic.List[hashtable]]::new()
        $identities.Add($actual)
        $snapshot = @(Get-CimInstance Win32_Process)
        for ($index = 0; $index -lt $identities.Count; $index++) {
            $parent = $identities[$index]
            foreach ($candidate in $snapshot | Where-Object { $_.ParentProcessId -eq $parent.ProcessId -and $_.CreationDate.ToUniversalTime() -ge ([DateTimeOffset]$parent.StartedUtc).UtcDateTime }) {
                if (@($identities | Where-Object { $_.ProcessId -eq $candidate.ProcessId }).Count) { continue }
                $child = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
                if ($null -eq $child) { continue }
                try {
                    $captured = Get-VaultFixtureProcessIdentity $child
                    if ([Math]::Abs((([DateTimeOffset]$captured.StartedUtc).UtcDateTime - $candidate.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1) {
                        throw 'Descendant process identity changed during discovery.'
                    }
                    $identities.Add($captured)
                } finally { $child.Dispose() }
            }
        }
        return $identities.ToArray()
    } finally { $root.Dispose() }
}

function Stop-VaultFixtureProcessTree {
    param([hashtable]$Identity)
    $identities = @(Get-VaultFixtureProcessTreeIdentity $Identity)
    for ($index = $identities.Count - 1; $index -ge 0; $index--) { Stop-VaultFixtureProcess $identities[$index] }
    foreach ($captured in $identities) {
        $remaining = Get-Process -Id $captured.ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $remaining) {
            try {
                if ($remaining.StartTime.ToUniversalTime().ToString('o') -eq $captured.StartedUtc) { throw 'Owned descendant remains after teardown.' }
            } finally { $remaining.Dispose() }
        }
    }
    return $identities
}

Export-ModuleMember -Function *-VaultFixture*
