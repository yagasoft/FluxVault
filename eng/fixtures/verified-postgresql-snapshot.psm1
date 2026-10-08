#Requires -Version 7.2
Set-StrictMode -Version Latest

function Copy-VaultFixtureVerifiedFile {
    param([string]$Source,[string]$Destination,[string]$ExpectedHash,[long]$ExpectedBytes)
    if($ExpectedBytes -lt 0 -or $ExpectedBytes -gt 1073741824){throw 'Reference file size exceeds its bound.'}
    $input=[IO.File]::Open($Source,'Open','Read','Read')
    $output=$null
    try {
        # FileShare.Read prevents a writer changing the checked length while this handle is open.
        if($input.Length -ne $ExpectedBytes){throw 'Installed payload size differs from the independently authenticated reference.'}
        $output=[IO.File]::Open($Destination,'CreateNew','Write','None')
        $buffer=[byte[]]::new(65536);$remaining=$ExpectedBytes
        while($remaining -gt 0) {
            $count=$input.Read($buffer,0,[int][Math]::Min($buffer.Length,$remaining))
            if($count -eq 0){throw 'Installed payload ended before the reference size.'}
            $output.Write($buffer,0,$count);$remaining-=$count
        }
        if($input.ReadByte() -ne -1){throw 'Installed payload exceeded the reference size.'}
        $output.Flush($true)
    } finally {$input.Dispose();if($null -ne $output){$output.Dispose()}}
    # The protected destination is the publication boundary; do not trust a pre-copy source hash.
    if((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $ExpectedHash -or (Get-Item -LiteralPath $Destination).Length -ne $ExpectedBytes){throw 'Installed payload differs from the independently authenticated reference.'}
}

function Get-VaultFixturePostgreSqlEntries {
    param([Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive)
    # The measured exact-build archive has 22,026 entries, mostly bundled pgAdmin files.
    if($Archive.Entries.Count -gt 32768){throw 'Reference archive metadata entry bound exceeded.'}
    $selected=[Collections.Generic.List[IO.Compression.ZipArchiveEntry]]::new();$expanded=0L
    foreach($entry in $Archive.Entries) {
        $name=$entry.FullName.Replace('\','/')
        if($name -notmatch '^pgsql/(bin|lib|share)/' -or $name.EndsWith('/')){continue}
        $relative=$name.Substring('pgsql/'.Length)
        if($relative -match '(^|/)\.\.?(/|$)|[:\x00-\x1f]' -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000){throw 'Reference entry is not an ordinary bounded file.'}
        $expanded+=$entry.Length
        if($expanded -gt 1073741824 -or $selected.Count -ge 20000){throw 'Reference server payload expansion or entry bound exceeded.'}
        $selected.Add($entry)
    }
    return $selected.ToArray()
}

function New-VaultFixtureSnapshotFromManifest {
    param([string]$Root,[string]$InstalledRoot,[string]$ManifestPath,[string]$ExpectedManifestSha256)
    Assert-VaultFixtureTrustedPath $Root
    Assert-VaultFixtureTrustedPath $ManifestPath
    if($ExpectedManifestSha256 -notmatch '^[A-Fa-f0-9]{64}$'){throw 'A trusted fixed manifest digest is required.'}
    $input=[IO.File]::Open($ManifestPath,'Open','Read','Read')
    try {
        if($input.Length -gt 1048576){throw 'Retained reference manifest exceeds its bound.'}
        $bytes=[byte[]]::new([int]$input.Length);$input.ReadExactly($bytes)
    }finally{$input.Dispose()}
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $ExpectedManifestSha256){throw 'Retained authenticated manifest does not match its trusted digest.'}
    # Parse precisely the bytes verified above, never a later reopen.
    $reference=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json -AsHashtable -Depth 6
    if(@($reference.Keys|Where-Object{$_ -notin @('Distribution','ReferenceUri','ReferenceAuthentication','ArchiveSha256','Files')}).Count -or
        $reference.Distribution -ne '18.6-4' -or $reference.ReferenceUri -ne 'https://get.enterprisedb.com/postgresql/postgresql-18.6-4-windows-x64-binaries.zip' -or
        $reference.ReferenceAuthentication -ne 'HTTPS with default certificate validation; redirects forbidden' -or $reference.ArchiveSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        $reference.Files.Count -lt 1 -or $reference.Files.Count -gt 20000){throw 'Retained reference metadata is outside the reviewed scope.'}
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$expanded=0L
    foreach($row in $reference.Files) {
        if(@($row.Keys|Where-Object{$_ -notin @('Path','Sha256','Bytes')}).Count -or $row.Path -notmatch '^(bin|lib|share)/' -or
            $row.Path -match '(^|/)\.\.?(/|$)|[:\x00-\x1f\\]' -or -not $paths.Add($row.Path) -or $row.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            $row.Bytes -lt 0 -or $row.Bytes -gt 1073741824){throw 'Retained reference file is invalid.'}
        $expanded+=[long]$row.Bytes;if($expanded -gt 1073741824){throw 'Retained server payload expansion bound exceeded.'}
    }
    foreach($required in @('bin/postgres.exe','bin/pg_ctl.exe','bin/initdb.exe','bin/psql.exe','bin/libpq.dll','share/postgres.bki','lib/plpgsql.dll')) {
        if(-not $paths.Contains($required)){throw "Required verified payload is missing: $required"}
    }
    $destination=Join-Path $Root 'postgresql'
    New-VaultFixtureProtectedDirectory $destination -ReadSids @('S-1-5-32-545')
    foreach($row in $reference.Files) {
        $staged=[IO.Path]::GetFullPath((Join-Path $destination $row.Path))
        if(-not $staged.StartsWith($destination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Snapshot entry escaped its root.'}
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($staged))|Out-Null
        Copy-VaultFixtureVerifiedFile (Join-Path $InstalledRoot $row.Path) $staged $row.Sha256 ([long]$row.Bytes)
        Assert-VaultFixtureTrustedPath $staged
    }
    $reference.ReusedManifestSha256=$ExpectedManifestSha256
    $reference.ReferenceAuthentication='Reused pinned manifest from the prior authenticated HTTPS exact-build archive; no fresh download.'
    $reference|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $Root 'postgresql-provenance.json')
    return (Join-Path $destination 'bin')
}

function New-VaultFixturePostgreSqlSnapshot {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$InstalledRoot)
    $version=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\PostgreSQL\Installations\postgresql-x64-18' -Name Version).Version
    if($version -ne '18.6-4' -or [IO.Path]::GetFullPath($InstalledRoot) -ne 'D:\Program Files\PostgreSQL\18'){throw 'Snapshot requires the reviewed installed distribution 18.6-4.'}
    Assert-VaultFixtureTrustedPath $Root
    $destination=Join-Path $Root 'postgresql'
    New-VaultFixtureProtectedDirectory $destination -ReadSids @('S-1-5-32-545')
    $archivePath=Join-Path $Root 'postgresql-reference.zip'
    $uri='https://get.enterprisedb.com/postgresql/postgresql-18.6-4-windows-x64-binaries.zip'
    $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
    # The measured exact-build transfer exceeded three minutes; retain a finite ten-minute ceiling.
    $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(600)
    try {
        $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if($response.StatusCode -ne [Net.HttpStatusCode]::OK -or $response.Content.Headers.ContentLength -le 0 -or $response.Content.Headers.ContentLength -gt 536870912){throw 'Authenticated exact-build archive is unavailable or exceeds its bound.'}
            $input=$response.Content.ReadAsStream();$output=[IO.File]::Open($archivePath,'CreateNew','Write','None')
            $deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(600))
            try {
                $buffer=[byte[]]::new(65536);$received=0L
                while(($count=$input.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0){$received+=$count;if($received -gt 536870912){throw 'Reference archive exceeded its transfer bound.'};$output.Write($buffer,0,$count)}
                if($received -ne $response.Content.Headers.ContentLength){throw 'Reference archive transfer was incomplete.'};$output.Flush($true)
            } finally {$input.Dispose();$output.Dispose();$deadline.Dispose()}
        } finally {$response.Dispose()}
    } finally {$client.Dispose();$handler.Dispose()}
    $archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
    $manifest=[Collections.Generic.List[object]]::new()
    try {
        foreach($entry in Get-VaultFixturePostgreSqlEntries $archive) {
            $name=$entry.FullName.Replace('\','/')
            $relative=$name.Substring('pgsql/'.Length)
            $staged=Join-Path $destination $relative
            $source=Join-Path $InstalledRoot $relative
            $resolved=[IO.Path]::GetFullPath($staged)
            if(-not $resolved.StartsWith($destination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Snapshot entry escaped its root.'}
            $referenceStream=$entry.Open()
            try {$referenceHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($referenceStream))} finally {$referenceStream.Dispose()}
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolved)) | Out-Null
            # No source execution. The protected final bytes must match the independent authenticated archive.
            Copy-VaultFixtureVerifiedFile $source $resolved $referenceHash $entry.Length
            $hash=(Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
            Assert-VaultFixtureTrustedPath $resolved
            $manifest.Add(@{Path=$relative;Sha256=$hash;Bytes=$entry.Length})
        }
    } finally {$archive.Dispose()}
    foreach($required in @('bin/postgres.exe','bin/pg_ctl.exe','bin/initdb.exe','bin/psql.exe','bin/libpq.dll','share/postgres.bki','lib/plpgsql.dll')) {
        if(@($manifest | Where-Object {$_.Path -eq $required}).Count -ne 1){throw "Required verified payload is missing: $required"}
    }
    @{Distribution=$version;ReferenceUri=$uri;ReferenceAuthentication='HTTPS with default certificate validation; redirects forbidden';ArchiveSha256=(Get-FileHash -LiteralPath $archivePath).Hash;Files=$manifest.ToArray()} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root 'postgresql-provenance.json')
    Remove-Item -LiteralPath $archivePath
    return (Join-Path $destination 'bin')
}

Export-ModuleMember -Function New-VaultFixturePostgreSqlSnapshot,New-VaultFixtureSnapshotFromManifest,Copy-VaultFixtureVerifiedFile,Get-VaultFixturePostgreSqlEntries
