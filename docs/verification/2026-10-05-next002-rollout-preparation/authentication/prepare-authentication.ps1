# Generates candidate inputs only. Never writes normal PostgreSQL files or reloads it.
#Requires -Version 7.2
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$data='D:\Program Files\PostgreSQL\18\data'
$expected=@{ 'pg_hba.conf'='C37D219F5AEE60D710ABCD9628A1F3D6C94F5C9861B4E31A52290DE995D97379'
    'pg_ident.conf'='2151E28F0BF5A8BB69109F91DBE61EBFFE48C6F9B71620F6A7B0B02B410E7DE5' }
$utf8=[Text.UTF8Encoding]::new($false,$true)
$original=@{}
foreach($name in $expected.Keys){
    $path=Join-Path $data $name
    if((Get-FileHash -LiteralPath $path).Hash -ne $expected[$name]){throw 'Normal authentication changed; preserve it and reconcile before generating a replacement.'}
    $original[$name]=$utf8.GetString([IO.File]::ReadAllBytes($path))
}
$newline=if($original['pg_hba.conf'].Contains("`r`n")){"`r`n"}else{"`n"}
# Literal multiline blocks avoid PowerShell array/join precedence turning all
# intended host rules into a single comment, as the native parser reproduced.
$service=@'
# FluxVault single installation 7871ff7f8d1b404db20771f2e742364f BEGIN
host fluxvault_single fluxvault_service 127.0.0.1/32 sspi map=fv_service_7871ff7f8d1b404db20771f2e742364f include_realm=1
host fluxvault_single fluxvault_service ::1/128 sspi map=fv_service_7871ff7f8d1b404db20771f2e742364f include_realm=1
host fluxvault_single all 127.0.0.1/32 reject
host fluxvault_single all ::1/128 reject
host all fluxvault_service 127.0.0.1/32 reject
host all fluxvault_service ::1/128 reject
# FluxVault single installation 7871ff7f8d1b404db20771f2e742364f END
'@ -replace '\r?\n',$newline
$admin=@'
# FluxVault temporary bootstrap 7871ff7f8d1b404db20771f2e742364f BEGIN
host postgres postgres 127.0.0.1/32 sspi map=fv_bootstrap_7871ff7f8d1b404db20771f2e742364f include_realm=1
host postgres postgres ::1/128 sspi map=fv_bootstrap_7871ff7f8d1b404db20771f2e742364f include_realm=1
# FluxVault temporary bootstrap 7871ff7f8d1b404db20771f2e742364f END
'@ -replace '\r?\n',$newline
$legacy='(?m)^# FluxVault PostgreSQL local trust BEGIN\r?\nhost fluxvault_metadata fluxvault 127\.0\.0\.1/32 trust\r?\nhost fluxvault_metadata fluxvault ::1/128 trust\r?\n# FluxVault PostgreSQL local trust END\r?\n'
if([regex]::Matches($original['pg_hba.conf'],$legacy).Count -ne 1){throw 'Exact old FluxVault trust block is missing or duplicated.'}
$baseHba=[regex]::Replace($original['pg_hba.conf'],$legacy,'')
$baseIdent=$original['pg_ident.conf']
$serviceIdent=$baseIdent+$newline+'# FluxVault single installation 7871ff7f8d1b404db20771f2e742364f'+$newline+
    'fv_service_7871ff7f8d1b404db20771f2e742364f "SYSTEM@NT AUTHORITY" fluxvault_service'+$newline
$files=@{
    'final-pg_hba.conf'=$service+$newline+$baseHba
    # Retain only the unchanged legacy trust during finite, fresh read-only
    # retirement polls. Final publication removes it; this is not read-only access.
    'temporary-pg_hba.conf'=$admin+$newline+$service+$newline+$original['pg_hba.conf']
    'final-pg_ident.conf'=$serviceIdent
    'temporary-pg_ident.conf'=$serviceIdent+'# FluxVault temporary bootstrap 7871ff7f8d1b404db20771f2e742364f'+$newline+
        'fv_bootstrap_7871ff7f8d1b404db20771f2e742364f "SYSTEM@NT AUTHORITY" postgres'+$newline
    'probe-pg_ident.conf'=$baseIdent+$newline+'# No FluxVault service/bootstrap principal admitted before normal-instance observation.'+$newline
}
foreach($name in $files.Keys){[IO.File]::WriteAllText((Join-Path $PSScriptRoot $name),$files[$name],$utf8)}
# Generation does not authorise use; PostgreSQL's actual parsed rows are the
# acceptance check for these exact bytes inside the existing owned fixture.
foreach($name in $expected.Keys){if((Get-FileHash -LiteralPath (Join-Path $data $name)).Hash -ne $expected[$name]){throw 'Normal authentication changed during preparation.'}}
@{ObservedUtc=[DateTime]::UtcNow.ToString('o');Applied=$false;Original=$expected;
    Proposed=@(foreach($name in @($files.Keys | Sort-Object)){
        @{Name=$name;Sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name)).Hash;
            Bytes=(Get-Item -LiteralPath (Join-Path $PSScriptRoot $name)).Length}
    })} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'prepared-hashes.json')
