# Read PE import names without executing SDK binaries.
$ErrorActionPreference='Stop'
function Read-PeImports([string]$Path) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    if($bytes.Length -gt 16777216){throw 'PE inspection bound exceeded.'}
    $pe=[BitConverter]::ToInt32($bytes,60)
    $sections=[BitConverter]::ToUInt16($bytes,$pe+6)
    $optional=$pe+24
    $optionalSize=[BitConverter]::ToUInt16($bytes,$pe+20)
    $table=$optional+$(if([BitConverter]::ToUInt16($bytes,$optional) -eq 0x20b){112}else{96})
    $importRva=[BitConverter]::ToUInt32($bytes,$table+8)
    $ranges=@(for($i=0;$i -lt $sections;$i++) {
        $offset=$optional+$optionalSize+$i*40
        @{Rva=[BitConverter]::ToUInt32($bytes,$offset+12);Size=[BitConverter]::ToUInt32($bytes,$offset+16);File=[BitConverter]::ToUInt32($bytes,$offset+20)}
    })
    function Resolve-Rva([uint32]$Rva) {
        foreach($range in $ranges){if($Rva -ge $range.Rva -and $Rva -lt ($range.Rva+$range.Size)){return [int]($range.File+$Rva-$range.Rva)}}
        throw 'PE import RVA lies outside bounded sections.'
    }
    $base=Resolve-Rva $importRva
    for($i=0;$i -lt 128;$i++) {
        $rva=[BitConverter]::ToUInt32($bytes,$base+$i*20+12)
        if($rva -eq 0){return}
        $offset=Resolve-Rva $rva
        $length=0;while($length -lt 256 -and $bytes[$offset+$length] -ne 0){$length++}
        if($length -eq 256){throw 'PE import name bound exceeded.'}
        [Text.Encoding]::ASCII.GetString($bytes,$offset,$length)
    }
    throw 'PE import count bound exceeded.'
}
foreach($name in @('mt.exe','makeappx.exe','signtool.exe','appxpackaging.dll','appxsip.dll','mssign32.dll','wintrust.dll','mrmsupport.dll','opcservices.dll')) {
    @{Name=$name;Imports=@(Read-PeImports (Join-Path 'E:\Windows Kits\10\bin\10.0.28000.0\x64' $name))}|ConvertTo-Json -Compress
}
