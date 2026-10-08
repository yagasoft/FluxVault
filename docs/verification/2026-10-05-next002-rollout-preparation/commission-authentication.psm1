# Internal operator procedure; import performs no system change. Its caller must
# own the SYSTEM Windows job, stop FluxVault, and retain the originals on failure.
#Requires -Version 7.2
Set-StrictMode -Version Latest

function Invoke-CommissionTool {
    param([hashtable]$Context,[string]$Executable,[string[]]$Arguments,[string]$InputText,
        [ValidateRange(1,120)][int]$TimeoutSeconds=15)
    $start=[Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$Context.WorkRoot
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($key in @($start.Environment.Keys | Where-Object {$_ -like 'PG*' -or $_ -like 'NPGSQL*'})){$null=$start.Environment.Remove($key)}
    $start.Environment['PGCONNECT_TIMEOUT']='3';$start.Environment['PGPASSFILE']=$Context.EmptyPasswordFile
    $start.Environment['PGOPTIONS']='-c statement_timeout=5000 -c lock_timeout=3000';$start.Environment['PGAPPNAME']=$Context.ApplicationName+'.check'
    $windows=[Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $start.Environment['PATH']=[string]::Join(';',@([IO.Path]::GetDirectoryName($Context.PsqlPath),(Join-Path $windows 'System32'),$windows))
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $process=$null;$stdout=$null;$stderr=$null
    try {
        $process=[Diagnostics.Process]::Start($start)
        if($null -eq $process){throw 'Commissioning tool did not start.'}
        $identity=Get-VaultFixtureProcessIdentity $process
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $journal=[IO.FileStream]::new((Join-Path $Context.WorkRoot 'commission-processes.jsonl'),'OpenOrCreate','Write','None')
        try{$journal.Position=$journal.Length;$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($identity | ConvertTo-Json -Compress)+"`n");$journal.Write($bytes);$journal.Flush($true)}finally{$journal.Dispose()}
        if($null -ne $InputText){$process.StandardInput.Write($InputText)};$process.StandardInput.Close()
        if(-not $process.WaitForExit($TimeoutSeconds*1000)){throw 'Commissioning tool exceeded its finite deadline.'}
        $output=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if($output.Length -gt 65536 -or $errorText.Length -gt 65536){throw 'Commissioning diagnostic exceeds its bound.'}
        return @{ExitCode=$process.ExitCode;Output=$output;Error=$errorText;Process=$identity;Joined=$true}
    } finally {
        if($null -ne $process){try{
            if(-not $process.HasExited){$process.Kill($true)}
            if(-not $process.WaitForExit(5000)){throw 'Owned commissioning child remains; runtime must stay blocked.'}
            if($null -ne $stdout){$null=$stdout.GetAwaiter().GetResult()}
            if($null -ne $stderr){$null=$stderr.GetAwaiter().GetResult()}
        }finally{$process.Dispose()}}
    }
}

function Invoke-CommissionQuery {
    param([hashtable]$Context,[string]$Connection,[string]$Sql,[switch]$AllowRefusal)
    $reply=Invoke-CommissionTool $Context $Context.PsqlPath @('-X','-w','-A','-t','-v','ON_ERROR_STOP=1','--dbname',$Connection) $Sql
    if(-not $AllowRefusal -and $reply.ExitCode -ne 0){
        $diagnostic=$reply.Error.Trim();if($diagnostic.Length -gt 2048){$diagnostic=$diagnostic.Substring(0,2048)}
        throw ('Commissioning inspection failed (exit '+$reply.ExitCode+'); runtime must stay blocked. '+$diagnostic)
    }
    return $reply
}

function Assert-CommissionPostmaster {
    param([hashtable]$Context)
    $process=Get-Process -Id $Context.Postmaster.ProcessId -ErrorAction Stop
    try {
        $actual=Get-VaultFixtureProcessIdentity $process
        if(([DateTimeOffset]$actual.StartedUtc).UtcDateTime -ne ([DateTimeOffset]$Context.Postmaster.StartedUtc).UtcDateTime -or
            $actual.Executable -ine $Context.Postmaster.Executable){throw 'PostgreSQL postmaster changed; no authentication transition is allowed.'}
        $pidFile=Join-Path $Context.DataDirectory 'postmaster.pid'
        $lines=[IO.File]::ReadAllLines($pidFile)
        if($lines.Count -lt 4 -or [int]$lines[0] -ne $actual.ProcessId -or
            [IO.Path]::GetFullPath($lines[1]) -ine [IO.Path]::GetFullPath($Context.DataDirectory) -or [int]$lines[3] -ne $Context.Port){throw 'PostgreSQL data/port binding changed.'}
    } finally {$process.Dispose()}
}

function Assert-CommissionAuthenticationPath {
    param([hashtable]$Context,[string]$Path)
    Assert-CommissionPostgresqlPath $Context $Path -Authentication
}

function Assert-CommissionPostgresqlPath {
    param([hashtable]$Context,[string]$Path,[switch]$Authentication)
    # This extra existing writer belongs to the observed PostgreSQL daemon only.
    # It must never become a trusted writer for executable/commissioning/vault paths.
    if($Context.NormalInstallation) {
        if($Context.DataDirectory -ine 'D:\Program Files\PostgreSQL\18\data' -or
            ($Authentication -and $Path -inotmatch '^D:\\Program Files\\PostgreSQL\\18\\data\\pg_(hba|ident)\.conf$') -or
            (-not $Authentication -and $Path -inotmatch '^D:\\Program Files\\PostgreSQL\\18\\bin\\[^\\]+\.(exe|dll)$')){throw 'Normal PostgreSQL path is outside the reviewed target.'}
        $service=Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-18'"
        if($service.State -ne 'Running' -or $service.StartName -ine 'NT AUTHORITY\NetworkService'){throw 'The observed PostgreSQL daemon authority changed.'}
        if($Context.OperatorSid -notin @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object {$_.SID.Value})){throw 'The intended operator is no longer a direct administrator.'}
    } else {Assert-VaultFixtureTrustedPath $Path -AdditionalTrustedOwnerSid $Context.OperatorSid;return}
    $trusted=@('S-1-5-18','S-1-5-32-544',$Context.OperatorSid,
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $target=[IO.Path]::GetFullPath($Path);$next=$null;$nextAcl=$null;$templatesCanReachLeaf=$true
    for($current=$target;$current;$current=[IO.Path]::GetDirectoryName($current)) {
        $item=Get-Item -LiteralPath $current -Force
        if($current -eq $target -and $item.PSIsContainer){throw 'PostgreSQL endpoint is not a file.'}
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Authentication path contains a reparse point.'}
        $acl=Get-Acl -LiteralPath $current
        $componentTrusted=@($trusted)
        if($Authentication -and ($current -ieq $target -or $current -ieq $Context.DataDirectory)){$componentTrusted+='S-1-5-20'}
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $componentTrusted){throw 'PostgreSQL component has an untrusted owner.'}
        $danger=[Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
        if($current -eq $target){$danger=$danger -bor [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::WriteAttributes}
        elseif($null -eq $next -or -not [IO.Directory]::Exists($current) -or -not(Test-Path -LiteralPath $next)){throw 'Authentication ancestor lost its protected next component.'}
        if($null -ne $nextAcl -and $nextAcl.AreAccessRulesProtected){$templatesCanReachLeaf=$false}
        if($current -ieq [IO.Path]::GetPathRoot($target)) {
            if(-not [FluxVault.Commissioning.PostgreSqlAncestorAcl]::IsVolumeRoot($current)){throw 'PostgreSQL volume root is not the verified native NTFS mount.'}
            # DELETE cannot remove a verified volume root. DELETE_CHILD and
            # security control remain dangerous and are still checked.
            $danger=$danger -band (-bnot [Security.AccessControl.FileSystemRights]::Delete)
        }
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if($rule.AccessControlType -eq 'Allow' -and -not($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
                $rule.IdentityReference.Value -notin $componentTrusted -and ($rule.FileSystemRights -band $danger)){throw 'PostgreSQL component permits an untrusted writer or replacement.'}
            $creatorOwnerIsTrusted=$rule.IdentityReference.Value -eq 'S-1-3-0' -and
                ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
                $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -in $componentTrusted -and
                $null -ne $nextAcl -and $nextAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -in $trusted
            if($templatesCanReachLeaf -and $current -ne $target -and $rule.AccessControlType -eq 'Allow' -and
                $rule.IdentityReference.Value -notin $componentTrusted -and -not $creatorOwnerIsTrusted -and
                ($rule.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ObjectInherit) -and ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)){throw 'PostgreSQL ancestor propagates an untrusted writer.'}
        }
        $next=$current;$nextAcl=$acl
    }
}

function Write-CommissionAuthenticationTransition {
    param([hashtable]$Context,[string]$Name,[IO.FileStream]$Stream,[byte[]]$Bytes)
    if($Bytes.Length -gt 1048576 -or $Stream.Length -gt 1048576){throw 'Authentication transition exceeds its bound.'}
    if($Context.NormalInstallation -or ($Context.ContainsKey('CleanupRequired') -and $Context.CleanupRequired)) {
        $old=[byte[]]::new([int]$Stream.Length);$Stream.Position=0;$Stream.ReadExactly($old)
        $id=[guid]::NewGuid().ToString('N');$files=@{}
        foreach($entry in @(@('Old',$old),@('New',$Bytes))) {
            $leaf='write-'+$id+'-'+$entry[0]+'.bin';$path=Join-Path $Context.WorkRoot $leaf
            $record=[IO.FileStream]::new($path,'CreateNew','Write','None')
            try{$record.Write([byte[]]$entry[1]);$record.Flush($true)}finally{$record.Dispose()}
            $files[$entry[0]+'File']=$leaf;$files[$entry[0]+'Sha256']=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$entry[1]));$files[$entry[0]+'Length']=$entry[1].Length
        }
        $files.ContextSha256=$Context.IdentitySha256;$files.File=$Name
        $temporary=Join-Path $Context.WorkRoot ('write-'+$id+'-intent.json')
        Write-CommissionAuthenticationReceipt $temporary $files
        [IO.File]::Move($temporary,(Join-Path $Context.WorkRoot 'authentication-write-intent.json'),$true)
    }
    $Stream.Position=0
    # Sequential bounded writes define the only prefix/suffix interruption state.
    for($offset=0;$offset -lt $Bytes.Length;$offset+=64){$count=[Math]::Min(64,$Bytes.Length-$offset);$Stream.Write($Bytes,$offset,$count)}
    $Stream.SetLength($Bytes.Length);$Stream.Flush($true)
}

function Test-CommissionInterruptedWrite {
    param([hashtable]$Context,[string]$Name,[byte[]]$Observed,[string[]]$KnownHashes)
    $path=Join-Path $Context.WorkRoot 'authentication-write-intent.json'
    if(-not(Test-Path -LiteralPath $path)){return $false}
    $intent=Read-CommissionAuthenticationReceipt $path 4096
    if($intent.ContextSha256 -cne $Context.IdentitySha256 -or $intent.File -cne $Name -or
        $intent.OldSha256 -cnotin $KnownHashes -or $intent.NewSha256 -cnotin $KnownHashes){return $false}
    $sequences=@{}
    foreach($key in @('Old','New')) {
        $leaf=$intent[$key+'File'];$length=[int]$intent[$key+'Length']
        if($leaf -cnotmatch '^write-[a-zA-Z0-9-]+\.bin$' -or $length -lt 0 -or $length -gt 1048576){return $false}
        $source=Join-Path $Context.WorkRoot $leaf;Assert-VaultFixtureTrustedPath $source -AdditionalTrustedOwnerSid $Context.OperatorSid
        if((Get-Item -LiteralPath $source).Length -ne $length -or (Get-FileHash -LiteralPath $source).Hash -cne $intent[$key+'Sha256']){return $false}
        $sequences[$key]=[IO.File]::ReadAllBytes($source)
    }
    $old=$sequences.Old;$new=$sequences.New
    if($Observed.Length -lt $old.Length -or $Observed.Length -gt [Math]::Max($old.Length,$new.Length)){return $false}
    $limit=[Math]::Min($Observed.Length,$new.Length);$newPrefix=0
    while($newPrefix -lt $limit -and $Observed[$newPrefix] -eq $new[$newPrefix]){$newPrefix++}
    if($Observed.Length -gt $old.Length) {if($newPrefix -ne $Observed.Length){return $false}}
    else {
        $requiredPrefix=0
        for($index=0;$index -lt $old.Length;$index++){if($Observed[$index] -ne $old[$index]){$requiredPrefix=$index+1}}
        if($requiredPrefix -gt $newPrefix){return $false}
    }
    # Preserve the exact interrupted bytes before retiring the owned transition.
    $preserved=Join-Path $Context.WorkRoot ('interrupted-'+$Name)
    $record=[IO.FileStream]::new($preserved,'CreateNew','Write','None')
    try{$record.Write($Observed);$record.Flush($true)}finally{$record.Dispose()}
    return $true
}

function Set-CommissionAuthenticationBytes {
    param([hashtable]$Context,[hashtable]$Pins,[byte[]]$Hba,[byte[]]$Ident)
    Assert-CommissionPostmaster $Context
    # Retire/deny the map first. A pin excludes writers/replacement without
    # altering the existing PostgreSQL ACL, and readers can inspect flushed bytes.
    foreach($name in @('pg_ident.conf','pg_hba.conf')) {
        [byte[]]$bytes=if($name -eq 'pg_ident.conf'){$Ident}else{$Hba}
        Write-CommissionAuthenticationTransition $Context $name $Pins[$name] $bytes
    }
    $reload=Invoke-CommissionTool $Context $Context.PgCtlPath @('-D',$Context.DataDirectory,'reload') $null
    if($reload.ExitCode -ne 0){throw 'Authentication reload failed; runtime must stay blocked.'}
    Assert-CommissionPostmaster $Context
}

function Read-CommissionLog {
    param([hashtable]$Context,[long]$Offset)
    $stream=[IO.FileStream]::new($Context.LogPath,'Open','Read','ReadWrite')
    try {
        if($stream.Length -lt $Offset -or $stream.Length-$Offset -gt 1048576){throw 'Correlated PostgreSQL log rotated or exceeded its bound.'}
        $stream.Position=$Offset;$bytes=[byte[]]::new([int]($stream.Length-$Offset));$stream.ReadExactly($bytes)
        return [Text.UTF8Encoding]::new($false,$true).GetString($bytes)
    } finally {$stream.Dispose()}
}

function Assert-CommissionSspiObservation {
    param([hashtable]$Context,[string]$Window)
    $pattern='no match in usermap "'+[regex]::Escape($Context.AdminMap)+'" for user "'+[regex]::Escape($Context.Role)+'" authenticated as "([^"\r\n]+)"'
    $matches=[regex]::Matches($Window,$pattern)
    if($matches.Count -ne 1 -or $matches[0].Groups[1].Value -cne 'SYSTEM@NT AUTHORITY'){throw 'Normal PostgreSQL did not establish the exact SYSTEM SSPI principal; no administrator map is admitted.'}
    return $matches[0].Value
}

function Assert-CommissionSessionRetired {
    param([hashtable]$Context,[string]$Connection,[int]$BackendPid,[switch]$Final)
    # Each invocation creates and joins a new non-pooled psql connection. Its
    # fresh transaction avoids a cached pg_stat_activity snapshot.
    $app=$Context.ApplicationName.Replace("'","''");$legacy=$Context.LegacyRole.Replace("'","''");$admin=$Context.Role.Replace("'","''")
    $sql="SELECT json_build_object('administrator_absent',NOT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=$BackendPid OR application_name='$app'),'admin_pids',COALESCE((SELECT json_agg(pid) FROM pg_stat_activity WHERE usename='$admin'),'[]'::json),'legacy_pids',COALESCE((SELECT json_agg(pid) FROM pg_stat_activity WHERE usename='$legacy' AND pid<>pg_backend_pid()),'[]'::json),'database',current_database(),'role',current_user,'port',inet_server_port());"
    $reply=Invoke-CommissionQuery $Context $Connection $sql
    $state=$reply.Output.Trim() | ConvertFrom-Json -AsHashtable -Depth 4
    if(-not $state.administrator_absent -or @($state.legacy_pids).Count){throw 'Administrator or legacy sessions survive; retain state and keep runtime blocked.'}
    if(@($state.admin_pids).Count -gt 64 -or @($state.admin_pids | Where-Object {$_ -notin $Context.AdministratorBaseline}).Count){throw 'An unaccounted administrator session survives; preserve unrelated sessions and keep runtime blocked.'}
    if($Final -and ($state.database -cne $Context.ServiceDatabase -or $state.role -cne $Context.ServiceRole -or [int]$state.port -ne $Context.Port)){throw 'Fresh service binding does not match the reviewed database/account.'}
    return $state
}

function Assert-CommissionSystemWorker {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{if($identity.User.Value -ne 'S-1-5-18' -or $identity.ImpersonationLevel -ne 'None'){throw 'Authentication commissioning requires the owned SYSTEM process without impersonation.'}}
    finally{$identity.Dispose()}
}

function Write-CommissionAuthenticationReceipt {
    param([string]$Path,[hashtable]$Receipt)
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Receipt | ConvertTo-Json -Depth 8))
    $stream=[IO.FileStream]::new($Path,'CreateNew','Write','None')
    try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}

function Invoke-CommissionAuthentication {
    param([Parameter(Mandatory)][hashtable]$Context)
    Assert-CommissionSystemWorker
    if($Context.NormalInstallation) {
        if($Context.Port -ne 5432 -or $Context.Role -cne 'postgres' -or $Context.Database -cne 'postgres' -or
            $Context.ServiceDatabase -cne 'fluxvault_single' -or $Context.ServiceRole -cne 'fluxvault_service' -or $Context.LegacyRole -cne 'fluxvault' -or
            $Context.OperatorSid -cne 'S-1-5-21-136112424-624261118-1239521417-1001' -or
            $Context.PsqlPath -ine 'D:\Program Files\PostgreSQL\18\bin\psql.exe' -or $Context.PgCtlPath -ine 'D:\Program Files\PostgreSQL\18\bin\pg_ctl.exe' -or
            $Context.SqlSha256 -cne 'DA258197F8308B476084A212BFAC7656BC8049C27383F0B5AB0505565BA743B7'){throw 'Normal commissioning differs from the exact prepared target.'}
    } elseif($Context.Port -eq 5432 -or $Context.DataDirectory -ieq 'D:\Program Files\PostgreSQL\18\data'){throw 'A disposable commissioning context cannot address normal PostgreSQL.'}
    Assert-VaultFixtureTrustedPath $Context.WorkRoot -AdditionalTrustedOwnerSid $Context.OperatorSid
    foreach($path in @($Context.PsqlPath,$Context.PgCtlPath)){Assert-CommissionPostgresqlPath $Context $path}
    if($Context.NormalInstallation) {
        if($Context.PsqlPath -ine 'D:\Program Files\PostgreSQL\18\bin\psql.exe' -or
            $Context.PgCtlPath -ine 'D:\Program Files\PostgreSQL\18\bin\pg_ctl.exe'){throw 'Normal PostgreSQL executable selection changed.'}
        foreach($dependency in Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($Context.PsqlPath)) -File | Where-Object {$_.Extension -iin @('.exe','.dll')}) {
            Assert-CommissionPostgresqlPath $Context $dependency.FullName
        }
    }
    foreach($path in @($Context.SqlPath,$Context.EmptyPasswordFile)){Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $Context.OperatorSid}
    if((Get-FileHash -LiteralPath $Context.SqlPath).Hash -cne $Context.SqlSha256){throw 'Reviewed SQL changed before any authentication transition.'}
    Assert-CommissionPostmaster $Context
    $prepared=@{}
    foreach($entry in $Context.Prepared.GetEnumerator()) {
        Assert-VaultFixtureTrustedPath $entry.Value.Path -AdditionalTrustedOwnerSid $Context.OperatorSid
        if((Get-Item -LiteralPath $entry.Value.Path).Length -gt 1048576){throw 'Prepared authentication exceeds its bound.'}
        if((Get-FileHash -LiteralPath $entry.Value.Path).Hash -cne $entry.Value.Sha256){throw 'Prepared authentication changed before commissioning.'}
        $prepared[$entry.Key]=[IO.File]::ReadAllBytes($entry.Value.Path)
    }
    foreach($name in @('FinalHba','FinalIdent','TemporaryHba','TemporaryIdent','ProbeIdent')){if(-not $prepared.ContainsKey($name)){throw 'The complete reviewed authentication set is required.'}}
    $pins=@{};$original=@{};$changed=$false;$success=$false;$administrator=$null
    try {
        foreach($name in @('pg_hba.conf','pg_ident.conf')) {
            $path=Join-Path $Context.DataDirectory $name;Assert-CommissionAuthenticationPath $Context $path
            $pins[$name]=[IO.FileStream]::new($path,'Open','ReadWrite','Read')
            $stream=$pins[$name]
            if($stream.Length -gt 1048576){throw 'Original authentication exceeds its bound.'}
            $bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes)
            $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            if($hash -cne $Context.OriginalHashes[$name]){throw 'Original authentication changed; refusing to replace it.'}
            $original[$name]=$bytes
            $backup=[IO.FileStream]::new((Join-Path $Context.WorkRoot ('original-'+$name)),'CreateNew','Write','None')
            try{$backup.Write($bytes);$backup.Flush($true)}finally{$backup.Dispose()}
        }
        # Retained recovery instructions precede any temporary admission.
        $recovery=[IO.FileStream]::new((Join-Path $Context.WorkRoot 'authentication-recovery.txt'),'CreateNew','Write','None')
        try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes('Keep FluxVault stopped. Join the owned job. Verify target/postmaster and exact originals; restore original-pg_ident.conf before original-pg_hba.conf, preserve ACLs, reload only, and verify no temporary map/backend survives. Never retry SQL or delete partial data automatically.');$recovery.Write($bytes);$recovery.Flush($true)}finally{$recovery.Dispose()}
        $adminRole=$Context.Role.Replace("'","''")
        $baselineReply=Invoke-CommissionQuery $Context $Context.LegacyConnection ("SELECT json_build_object('admin_pids',COALESCE((SELECT json_agg(pid) FROM pg_stat_activity WHERE usename='$adminRole'),'[]'::json));")
        $baseline=$baselineReply.Output.Trim() | ConvertFrom-Json -AsHashtable -Depth 3
        if(@($baseline.admin_pids).Count -gt 64 -or @($baseline.admin_pids | Where-Object {$_ -isnot [int] -and $_ -isnot [long] -or $_ -le 0}).Count){throw 'Administrator baseline is invalid or exceeds its bound; no access change is allowed.'}
        $Context.AdministratorBaseline=@($baseline.admin_pids)
        Write-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'administrator-baseline.json') @{Role=$Context.Role;BackendPids=$Context.AdministratorBaseline;Postmaster=$Context.Postmaster}
        if($Context.NormalInstallation -or ($Context.ContainsKey('CleanupRequired') -and $Context.CleanupRequired)){Assert-CommissionCleanupReady $Context}
        $changed=$true
        Set-CommissionAuthenticationBytes $Context $pins $prepared.TemporaryHba $prepared.ProbeIdent
        $offset=(Get-Item -LiteralPath $Context.LogPath).Length
        $probe=Invoke-CommissionQuery $Context $Context.Connection 'SELECT 1;' -AllowRefusal
        if($probe.ExitCode -eq 0){throw 'Administrator unexpectedly opened before principal confirmation.'}
        $observation=$null;$deadline=[Diagnostics.Stopwatch]::StartNew()
        while($null -eq $observation) {
            $window=Read-CommissionLog $Context $offset
            if($window.Contains('no match in usermap "'+$Context.AdminMap+'"')){$observation=Assert-CommissionSspiObservation $Context $window;break}
            if($deadline.Elapsed.TotalSeconds -ge 3){throw 'Correlated SYSTEM SSPI observation was not flushed within its deadline.'}
            Start-Sleep -Milliseconds 25
        }
        Set-CommissionAuthenticationBytes $Context $pins $prepared.TemporaryHba $prepared.TemporaryIdent
        $administrator=Invoke-CommissionAdministrator $Context
        $before=Assert-CommissionSessionRetired $Context $Context.LegacyConnection $administrator.Backend.backend_pid
        Set-CommissionAuthenticationBytes $Context $pins $prepared.FinalHba $prepared.FinalIdent
        $denied=Invoke-CommissionQuery $Context $Context.Connection 'SELECT 1;' -AllowRefusal
        if($denied.ExitCode -eq 0){throw 'Fresh administrator login remains admitted after retirement.'}
        $after=Assert-CommissionSessionRetired $Context $Context.ServiceConnection $administrator.Backend.backend_pid -Final
        Assert-CommissionPostmaster $Context
        $receipt=@{Version=1;Accepted=$true;PrincipalObservation=$observation;Administrator=$administrator;
            BeforeRetirement=$before;AfterRetirement=$after;FreshAdministratorDenied=$true;Postmaster=$Context.Postmaster;ObservedUtc=[DateTime]::UtcNow.ToString('o')}
        Write-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'authentication-completed.json') $receipt
        $success=$true;return $receipt
    } finally {
        try {
            if($changed -and -not $success){
                # Preserve SQL effects; retire admission by restoring exact original
                # bytes. A failed restore is a hard block, never a successful setup.
                Set-CommissionAuthenticationBytes $Context $pins $original['pg_hba.conf'] $original['pg_ident.conf']
            }
        } finally {foreach($stream in $pins.Values){$stream.Dispose()}}
    }
}

function Read-CommissionAuthenticationReceipt {
    param([string]$Path,[ValidateRange(1,65536)][int]$MaximumBytes=16384)
    $stream=[IO.FileStream]::new($Path,'Open','Read','Read')
    try {
        if($stream.Length -eq 0 -or $stream.Length -gt $MaximumBytes){throw 'Authentication receipt size is invalid.'}
        $bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes)
        return [Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 8
    } finally {$stream.Dispose()}
}

function Assert-CommissionReceiptPostmaster {
    param([hashtable]$Context,[hashtable]$Recorded)
    foreach($key in $Context.Postmaster.Keys) {
        if(-not $Recorded.ContainsKey($key)){throw 'Cleanup receipt has an incomplete postmaster binding.'}
        if($key -eq 'StartedUtc') {
            if(([DateTimeOffset]$Recorded[$key]).UtcDateTime -ne ([DateTimeOffset]$Context.Postmaster[$key]).UtcDateTime){throw 'Cleanup postmaster start changed.'}
        } elseif([string]$Recorded[$key] -ine [string]$Context.Postmaster[$key]){throw 'Cleanup postmaster binding changed.'}
    }
}

function Complete-CommissionAuthenticationCleanup {
    param([hashtable]$Context)
    Assert-CommissionSystemWorker
    Assert-VaultFixtureTrustedPath $Context.WorkRoot -AdditionalTrustedOwnerSid $Context.OperatorSid
    $job=[FluxVault.Fixtures.OwnedWindowsJob]::Open($Context.WorkerJob.KernelName,$Context.WorkerJob.OwnerSid)
    try{if($null -ne $job){$job.StopAndJoin()}}finally{if($null -ne $job){$job.Dispose()}}
    Assert-CommissionPostmaster $Context
    $baselinePath=Join-Path $Context.WorkRoot 'administrator-baseline.json'
    $Context.AdministratorBaseline=@()
    if(Test-Path -LiteralPath $baselinePath) {
        $baseline=Read-CommissionAuthenticationReceipt $baselinePath
        Assert-CommissionReceiptPostmaster $Context $baseline.Postmaster
        if($baseline.Role -cne $Context.Role -or @($baseline.BackendPids).Count -gt 64 -or
            @($baseline.BackendPids | Where-Object {$_ -isnot [int] -and $_ -isnot [long] -or $_ -le 0}).Count){throw 'Cleanup administrator baseline is invalid.'}
        $Context.AdministratorBaseline=@($baseline.BackendPids)
    }
    $pins=@{};$observed=@{};$original=@{};$final=$true;$completed=$null
    try {
        foreach($entry in @(@('pg_hba.conf','Hba'),@('pg_ident.conf','Ident'))) {
            $name=$entry[0];$suffix=$entry[1];$path=Join-Path $Context.DataDirectory $name
            Assert-CommissionAuthenticationPath $Context $path
            $stream=[IO.FileStream]::new($path,'Open','ReadWrite','Read');$pins[$name]=$stream
            if($stream.Length -gt 1048576){throw 'Authentication exceeds the cleanup bound.'}
            $bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes)
            $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));$observed[$name]=$hash
            $known=@($Context.OriginalHashes[$name],$Context.Prepared['Final'+$suffix].Sha256,$Context.Prepared['Temporary'+$suffix].Sha256)
            if($suffix -eq 'Ident'){$known+=$Context.Prepared.ProbeIdent.Sha256}
            if($hash -cnotin $known -and -not(Test-CommissionInterruptedWrite $Context $name $bytes $known)){throw 'Authentication has unaccounted bytes; preserve them and keep runtime blocked.'}
            if($hash -cne $Context.Prepared['Final'+$suffix].Sha256){$final=$false}
            $backup=Join-Path $Context.WorkRoot ('original-'+$name)
            if(Test-Path -LiteralPath $backup) {
                Assert-VaultFixtureTrustedPath $backup -AdditionalTrustedOwnerSid $Context.OperatorSid
                if((Get-Item -LiteralPath $backup).Length -gt 1048576 -or (Get-FileHash -LiteralPath $backup).Hash -cne $Context.OriginalHashes[$name]){throw 'Durable cleanup originals changed.'}
                $original[$name]=[IO.File]::ReadAllBytes($backup)
            } elseif($hash -cne $Context.OriginalHashes[$name]){throw 'Durable original is missing after an access change.'}
        }
        $backendPid=0;$completionPath=Join-Path $Context.WorkRoot 'authentication-completed.json'
        if($final) {
            # Keep a known final state final even if its acknowledgement was lost.
            # An unconfirmed final state still cannot activate runtime.
            if(Test-Path -LiteralPath $completionPath) {
                $completed=Read-CommissionAuthenticationReceipt $completionPath
                Assert-CommissionReceiptPostmaster $Context $completed.Postmaster
                if(-not $completed.Accepted -or [int]$completed.Administrator.Backend.backend_pid -le 0){throw 'Final authentication acknowledgement is invalid.'}
                $backendPid=[int]$completed.Administrator.Backend.backend_pid
            }
        } else {
            if(Test-Path -LiteralPath $completionPath){throw 'Completed authentication no longer has its exact final bytes.'}
            $needsRestore=@($observed.Keys | Where-Object {$observed[$_] -cne $Context.OriginalHashes[$_]}).Count -gt 0
            if($needsRestore) {
                if($original.Count -ne 2 -or -not(Test-Path -LiteralPath $baselinePath)){throw 'Cleanup cannot restore without both bound originals and the pre-admission baseline.'}
                Set-CommissionAuthenticationBytes $Context $pins $original['pg_hba.conf'] $original['pg_ident.conf']
            }
            $administratorPath=Join-Path $Context.WorkRoot 'administrator-session.json'
            if(Test-Path -LiteralPath $administratorPath){$administrator=Read-CommissionAuthenticationReceipt $administratorPath;$backendPid=[int]$administrator.Backend.backend_pid}
        }
        $connection=if($final){$Context.ServiceConnection}else{$Context.LegacyConnection}
        $retirement=[Diagnostics.Stopwatch]::StartNew()
        while($true) {
            try{$state=Assert-CommissionSessionRetired $Context $connection $backendPid -Final:$final;break}
            catch {if($_.Exception.Message -notmatch '^(Administrator or legacy sessions survive|An unaccounted administrator session survives)' -or $retirement.Elapsed.TotalSeconds -ge 15){throw};Start-Sleep -Milliseconds 100}
        }
        $denied=Invoke-CommissionQuery $Context $Context.Connection 'SELECT 1;' -AllowRefusal
        if($denied.ExitCode -eq 0){throw 'Administrator is still admitted after cleanup.'}
        Assert-CommissionPostmaster $Context
        $receipt=@{Version=1;ContextSha256=$Context.IdentitySha256;OwnedJobJoined=$true;AdmissionRetired=$true;
            FinalRetained=$final;CanActivate=($final -and $null -ne $completed);State=$state;Postmaster=$Context.Postmaster;
            ObservedUtc=[DateTime]::UtcNow.ToString('o')}
        Write-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'cleanup-completed.json') $receipt
        return $receipt
    } finally {foreach($stream in $pins.Values){$stream.Dispose()}}
}

function Test-CommissionExactProcess {
    param([hashtable]$Identity)
    $process=Get-Process -Id $Identity.ProcessId -ErrorAction SilentlyContinue
    if($null -eq $process){return $false}
    try {
        if($process.HasExited){return $false}
        $actual=Get-VaultFixtureProcessIdentity $process
        return $actual.Executable -ieq $Identity.Executable -and
            ([DateTimeOffset]$actual.StartedUtc).UtcDateTime -eq ([DateTimeOffset]$Identity.StartedUtc).UtcDateTime
    } catch [InvalidOperationException] {return $false}
    catch [ComponentModel.Win32Exception] {if($process.HasExited){return $false};throw}
    finally {$process.Dispose()}
}

function Assert-CommissionCleanupReady {
    param([hashtable]$Context)
    $ready=Read-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'cleanup-ready.json') 4096
    if($ready.ContextSha256 -cne $Context.IdentitySha256 -or $ready.WindowsSid -cne 'S-1-5-18' -or
        $ready.Job -cne $Context.WorkerJob.KernelName -or -not(Test-CommissionExactProcess $ready.Process)){throw 'Independent cleanup is not bound and alive; no temporary administrator access is allowed.'}
    $native=Get-CimInstance Win32_Process -Filter ("ProcessId="+[int]$ready.Process.ProcessId)
    $owner=Invoke-CimMethod -InputObject $native -MethodName GetOwnerSid
    if($owner.ReturnValue -ne 0 -or $owner.Sid -cne 'S-1-5-18'){throw 'Independent cleanup does not have the native SYSTEM process identity.'}
}

function Invoke-CommissionAuthenticationCleanupWatch {
    param([hashtable]$Context,[ValidateRange(15,180)][int]$TimeoutSeconds=120)
    Assert-CommissionSystemWorker
    Assert-VaultFixtureTrustedPath $Context.WorkRoot -AdditionalTrustedOwnerSid $Context.OperatorSid
    if($Context.IdentitySha256 -cnotmatch '^[0-9A-F]{64}$' -or -not(Test-CommissionExactProcess $Context.OperatorIdentity)){throw 'The independent cleanup attempt/operator binding is invalid.'}
    $heldJob=[FluxVault.Fixtures.OwnedWindowsJob]::Open($Context.WorkerJob.KernelName,$Context.WorkerJob.OwnerSid)
    if($null -eq $heldJob){throw 'Owned authentication job is missing before cleanup readiness.'}
    if($PID -in $heldJob.ProcessIds()){$heldJob.Dispose();throw 'Cleanup is inside authentication containment; no temporary access is allowed.'}
    $self=Get-Process -Id $PID
    try {$identity=Get-VaultFixtureProcessIdentity $self} finally {$self.Dispose()}
    $cleanupAttempted=$false
    try {
        Write-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'cleanup-ready.json') @{ContextSha256=$Context.IdentitySha256;
            WindowsSid='S-1-5-18';Process=$identity;Job=$Context.WorkerJob.KernelName}
        $deadline=[Diagnostics.Stopwatch]::StartNew();$finished=$false
        while($deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds -and (Test-CommissionExactProcess $Context.OperatorIdentity)) {
            if(Test-Path -LiteralPath (Join-Path $Context.WorkRoot 'authentication-completed.json')){$finished=$true;break}
            $started=Join-Path $Context.WorkRoot 'authentication-worker.json'
            if(Test-Path -LiteralPath $started) {
                $worker=Read-CommissionAuthenticationReceipt $started 4096
                if($worker.ContextSha256 -cne $Context.IdentitySha256 -or $worker.WindowsSid -cne 'S-1-5-18'){throw 'Authentication worker binding changed during supervision.'}
                if(-not(Test-CommissionExactProcess $worker.Process)){break}
            }
            Start-Sleep -Milliseconds 100
        }
        if($finished) {
            # Permit the worker to finish its own final pin disposal/publication.
            # The watchdog must not depend on its operator to close the job.
            $grace=[Diagnostics.Stopwatch]::StartNew()
            while($heldJob.ProcessIds().Count -gt 0 -and $grace.Elapsed.TotalSeconds -lt 2){Start-Sleep -Milliseconds 25}
        }
        $cleanupAttempted=$true
        return Complete-CommissionAuthenticationCleanup $Context
    } catch {
        # Always join containment even when receipt validation fails; never stop
        # an unrelated process/service or rewrite unaccounted authentication.
        $failure=$_.Exception.Message
        $heldJob.StopAndJoin()
        if(-not $cleanupAttempted) {
            $cleanupAttempted=$true
            try{$null=Complete-CommissionAuthenticationCleanup $Context}catch{$failure+=' Cleanup failed: '+$_.Exception.Message}
        }
        if($failure.Length -gt 2048){$failure=$failure.Substring(0,2048)}
        Write-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'cleanup-failed.json') @{ContextSha256=$Context.IdentitySha256;CanActivate=$false;Error=$failure}
        throw
    } finally {$heldJob.Dispose()}
}

Export-ModuleMember -Function Invoke-CommissionAuthentication,Assert-CommissionPostgresqlPath,Complete-CommissionAuthenticationCleanup,Invoke-CommissionAuthenticationCleanupWatch
