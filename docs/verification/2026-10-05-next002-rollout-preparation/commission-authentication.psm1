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
    # This extra existing writer belongs to the observed PostgreSQL daemon only.
    # It must never become a trusted writer for executable/commissioning/vault paths.
    if($Context.NormalInstallation) {
        if($Context.DataDirectory -ine 'D:\Program Files\PostgreSQL\18\data' -or
            $Path -inotmatch '^D:\\Program Files\\PostgreSQL\\18\\data\\pg_(hba|ident)\.conf$'){throw 'Normal authentication path is outside the reviewed target.'}
        $service=Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-18'"
        if($service.State -ne 'Running' -or $service.StartName -ine 'NT AUTHORITY\NetworkService'){throw 'The observed PostgreSQL daemon authority changed.'}
        if($Context.OperatorSid -notin @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object {$_.SID.Value})){throw 'The intended operator is no longer a direct administrator.'}
    } else {Assert-VaultFixtureTrustedPath $Path -AdditionalTrustedOwnerSid $Context.OperatorSid;return}
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-20',$Context.OperatorSid,
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $target=[IO.Path]::GetFullPath($Path);$next=$null
    for($current=$target;$current;$current=[IO.Path]::GetDirectoryName($current)) {
        $item=Get-Item -LiteralPath $current -Force
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Authentication path contains a reparse point.'}
        $acl=Get-Acl -LiteralPath $current
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Authentication component has an untrusted owner.'}
        $danger=[Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
        if($current -eq $target){$danger=$danger -bor [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::WriteAttributes}
        elseif($null -eq $next -or -not [IO.Directory]::Exists($current) -or -not(Test-Path -LiteralPath $next)){throw 'Authentication ancestor lost its protected next component.'}
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if($rule.AccessControlType -eq 'Allow' -and -not($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
                $rule.IdentityReference.Value -notin $trusted -and ($rule.FileSystemRights -band $danger)){throw 'Authentication component permits an untrusted writer or replacement.'}
            if($current -ne $target -and $rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and
                ($rule.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ObjectInherit) -and ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)){throw 'Authentication ancestor propagates an untrusted writer.'}
        }
        $next=$current
    }
}

function Set-CommissionAuthenticationBytes {
    param([hashtable]$Context,[hashtable]$Pins,[byte[]]$Hba,[byte[]]$Ident)
    Assert-CommissionPostmaster $Context
    # Retire/deny the map first. A pin excludes writers/replacement without
    # altering the existing PostgreSQL ACL, and readers can inspect flushed bytes.
    foreach($name in @('pg_ident.conf','pg_hba.conf')) {
        [byte[]]$bytes=if($name -eq 'pg_ident.conf'){$Ident}else{$Hba}
        $stream=$Pins[$name];$stream.Position=0;$stream.Write($bytes);$stream.SetLength($bytes.Length);$stream.Flush($true)
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
    if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){throw 'Authentication commissioning requires the owned SYSTEM worker.'}
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
    foreach($path in @($Context.PsqlPath,$Context.PgCtlPath,$Context.SqlPath,$Context.EmptyPasswordFile)){Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $Context.OperatorSid}
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

Export-ModuleMember -Function Invoke-CommissionAuthentication
