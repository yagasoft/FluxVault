# Internal staging operator helper. Importing this module changes no authentication
# and launches no process. The normal supervisor must admit its SYSTEM worker to
# the existing owned Windows job before calling this function.
#Requires -Version 7.2
Set-StrictMode -Version Latest

function Write-CommissionAdministratorReceipt {
    param([string]$Path,[hashtable]$Receipt)
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Receipt | ConvertTo-Json -Depth 5))
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {$stream.Write($bytes);$stream.Flush($true)} finally {$stream.Dispose()}
}

function Invoke-CommissionAdministrator {
    param([Parameter(Mandatory)][hashtable]$Context,
        [Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None,
        [ValidateRange(1,10)][int]$PublicationTimeoutSeconds=5,
        [ValidateRange(1,120)][int]$ExecutionTimeoutSeconds=60)
    $CancellationToken.ThrowIfCancellationRequested()
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Context.WorkRoot))
    $operatorSid=if($Context.ContainsKey('OperatorSid')){$Context.OperatorSid}else{$null}
    Assert-VaultFixtureTrustedPath $root -AdditionalTrustedOwnerSid $operatorSid
    Assert-VaultFixtureTrustedPath $Context.PsqlPath -AdditionalTrustedOwnerSid $operatorSid
    Assert-VaultFixtureTrustedPath $Context.SqlPath -AdditionalTrustedOwnerSid $operatorSid
    if((Get-FileHash -LiteralPath $Context.SqlPath).Hash -cne $Context.SqlSha256){throw 'Reviewed commissioning SQL changed; no administrator was opened.'}
    $handshake=Join-Path $root 'administrator-backend.json'
    $receipt=Join-Path $root 'administrator-session.json'
    if((Test-Path -LiteralPath $handshake) -or (Test-Path -LiteralPath $receipt)){throw 'An administrator attempt already exists; reconcile it instead of retrying.'}
    $sql=[IO.File]::ReadAllText($Context.SqlPath)
    $start=[Diagnostics.ProcessStartInfo]::new($Context.PsqlPath)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$root
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($key in @($start.Environment.Keys | Where-Object {$_ -like 'PG*' -or $_ -like 'NPGSQL*'})){$null=$start.Environment.Remove($key)}
    $start.Environment['PGCONNECT_TIMEOUT']='3'
    $start.Environment['PGOPTIONS']='-c statement_timeout=10000 -c lock_timeout=3000'
    $start.Environment['PGAPPNAME']=$Context.ApplicationName
    $start.Environment['PGPASSFILE']=$Context.EmptyPasswordFile
    # Private fixture credentials stay in memory. The normal supervisor supplies
    # no password environment and uses only the reviewed SYSTEM SSPI mapping.
    if($Context.ContainsKey('Environment')){foreach($key in $Context.Environment.Keys){$start.Environment[$key]=$Context.Environment[$key]}}
    if($Context.ContainsKey('PrefixArguments')){foreach($argument in $Context.PrefixArguments){$start.ArgumentList.Add($argument)}}
    foreach($argument in @('-X','-w','-A','-t','-v','ON_ERROR_STOP=1','--dbname',$Context.Connection)){$start.ArgumentList.Add($argument)}
    $process=$null;$stdout=$null;$stderr=$null;$identity=$null;$backend=$null;$exitCode=$null
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    try {
        $CancellationToken.ThrowIfCancellationRequested()
        $process=[Diagnostics.Process]::Start($start)
        if($null -eq $process){throw 'Administrator worker did not launch.'}
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $identity=Get-VaultFixtureProcessIdentity $process
        $process.StandardInput.AutoFlush=$true
        $path=$handshake.Replace('\','/')
        if($path.Contains("'")){throw 'Administrator publication path cannot be represented safely.'}
        # Close the psql output file before inspecting it. No DDL is sent until
        # the exact backend/binding has been validated and durably receipted.
        $process.StandardInput.WriteLine(('\o '+"'"+$path+"'"))
        $process.StandardInput.WriteLine("SELECT json_build_object('backend_pid',pg_backend_pid(),'role',current_user,'database',current_database(),'data_directory',current_setting('data_directory'),'port',current_setting('port')); ")
        $process.StandardInput.WriteLine('\o')
        while($null -eq $backend) {
            $CancellationToken.ThrowIfCancellationRequested()
            if($process.HasExited){throw 'Administrator did not publish a confirmed backend; DDL was withheld.'}
            if($deadline.Elapsed.TotalSeconds -ge $PublicationTimeoutSeconds){throw 'Administrator backend publication exceeded its deadline; DDL was withheld.'}
            if(Test-Path -LiteralPath $handshake) {
                $publicationStream=$null
                try {
                    # Exclude any still-open writer, and bound publication before
                    # parsing. The trusted query determines the exact JSON shape.
                    $publicationStream=[IO.FileStream]::new($handshake,'Open','Read','Read')
                    if($publicationStream.Length -gt 4096){throw 'Administrator backend publication is oversized.'}
                    if($publicationStream.Length) {
                        $bytes=[byte[]]::new([int]$publicationStream.Length);$publicationStream.ReadExactly($bytes)
                        $backend=[Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 3
                    }
                } catch [IO.IOException] {
                    # An output file still being closed is not a new connection
                    # attempt; wait on the same owned process and publication.
                } finally {if($null -ne $publicationStream){$publicationStream.Dispose()}}
            }
            if($null -eq $backend){Start-Sleep -Milliseconds 25}
        }
        $expected=@('backend_pid','role','database','data_directory','port')
        $publishedData=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($backend.data_directory))
        $wantedData=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Context.DataDirectory))
        if($backend -isnot [hashtable] -or $backend.Count -ne $expected.Count -or
            @($backend.Keys | Where-Object {$_ -notin $expected}).Count -or
            $backend.backend_pid -isnot [long] -and $backend.backend_pid -isnot [int] -or $backend.backend_pid -le 0 -or
            $backend.role -cne $Context.Role -or $backend.database -cne $Context.Database -or
            $publishedData -ine $wantedData -or [string]$backend.port -cne [string]$Context.Port){throw 'Administrator published another backend binding; DDL was withheld.'}
        Write-CommissionAdministratorReceipt $receipt @{Version=1;ApplicationName=$Context.ApplicationName;Process=$identity;
            Backend=$backend;SqlSha256=$Context.SqlSha256;PublishedUtc=[DateTime]::UtcNow.ToString('o')}
        $CancellationToken.ThrowIfCancellationRequested()
        $process.StandardInput.WriteLine($sql);$process.StandardInput.Close()
        while(-not $process.WaitForExit(25)) {
            $CancellationToken.ThrowIfCancellationRequested()
            if($deadline.Elapsed.TotalSeconds -ge $ExecutionTimeoutSeconds){throw 'Administrator SQL exceeded its finite deadline; retained state requires reconciliation.'}
        }
        $exitCode=$process.ExitCode
    } finally {
        if($null -ne $process) {
            try {
                if(-not $process.HasExited){$process.Kill($true)}
                if(-not $process.WaitForExit(5000)){throw 'Owned administrator worker did not exit; activation must remain blocked.'}
                if($null -ne $stdout){$null=$stdout.GetAwaiter().GetResult()}
                if($null -ne $stderr){$null=$stderr.GetAwaiter().GetResult()}
            } finally {$process.Dispose()}
        }
        foreach($key in @($start.Environment.Keys | Where-Object {$_ -like 'PG*' -or $_ -like 'NPGSQL*'})){$null=$start.Environment.Remove($key)}
    }
    if($exitCode -ne 0){throw ('Administrator SQL did not succeed (exit '+$exitCode+'); retain the receipt and reconcile owned state without retrying.')}
    return @{WorkerJoined=$true;ExitCode=$exitCode;Process=$identity;Backend=$backend;ReceiptPath=$receipt}
}

Export-ModuleMember -Function Invoke-CommissionAdministrator
