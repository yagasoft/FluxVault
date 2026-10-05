param(
    [string]$ServiceName = 'FluxVaultService',
    [string]$PublishRoot = '',
    [string]$EventLogSource = 'FluxVaultService'
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell session.'
    }
}

function Resolve-PublishRoot([string]$RequestedPublishRoot) {
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $expected = [IO.Path]::GetFullPath((Join-Path $programFiles 'FluxVault')).TrimEnd('\')
    $requested = if ([string]::IsNullOrWhiteSpace($RequestedPublishRoot)) { $expected } else { [IO.Path]::GetFullPath($RequestedPublishRoot).TrimEnd('\') }
    if (-not [string]::Equals($requested, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SYSTEM service registration requires the protected Program Files\FluxVault payload. A published workspace is not an installation.'
    }
    return $requested
}

function Assert-ProtectedPayloadItem([string]$Path, [bool]$Ancestor) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Service payload contains an untrusted reparse point.' }
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new((Get-Acl -LiteralPath $Path).Sddl)
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if ($null -eq $descriptor.Owner -or $descriptor.Owner.Value -notin $trusted -or $null -eq $descriptor.DiscretionaryAcl) {
        throw 'Service payload ownership or access policy is untrusted.'
    }
    foreach ($entry in $descriptor.DiscretionaryAcl) {
        if ($entry -isnot [Security.AccessControl.QualifiedAce] -or $entry.IsCallback) { throw 'Service payload access policy is untrusted.' }
        if ($entry.AceQualifier -ne [Security.AccessControl.AceQualifier]::AccessAllowed) { continue }
        $inheritOnly = [bool]($entry.AceFlags -band [Security.AccessControl.AceFlags]::InheritOnly)
        if ($inheritOnly -and ($Ancestor -or $entry.SecurityIdentifier.Value -eq 'S-1-3-0')) { continue }
        if ($entry.SecurityIdentifier.Value -in $trusted) { continue }
        $rights = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$entry.AccessMask), 0)
        if ($rights -band 0x10000000) { $rights = $rights -bor 0x1F01FF } # GENERIC_ALL
        if ($rights -band 0x40000000) { $rights = $rights -bor 0x120116 } # GENERIC_WRITE
        $forbidden = if ($Ancestor) { 0xD0150 } else { 0xD0156 }
        if ($rights -band $forbidden) { throw 'Service payload permits untrusted write or replacement.' }
    }
}

function Assert-ProtectedServicePayload([string]$ServiceDirectory) {
    $directory = [IO.DirectoryInfo]::new($ServiceDirectory)
    for ($parent = $directory.Parent; $null -ne $parent; $parent = $parent.Parent) {
        Assert-ProtectedPayloadItem -Path $parent.FullName -Ancestor $true
    }
    Assert-ProtectedPayloadItem -Path $ServiceDirectory -Ancestor $false
    # Recurse does not follow reparse links. Every discovered entry is checked before registration.
    foreach ($item in Get-ChildItem -LiteralPath $ServiceDirectory -Force -Recurse) {
        Assert-ProtectedPayloadItem -Path $item.FullName -Ancestor $false
    }
}

function Register-FluxVaultService([string]$RequestedPublishRoot, [string]$Name, [string]$Source) {
    if ($Name -notmatch '^[A-Za-z0-9_-]{1,80}$' -or $Source -notmatch '^[A-Za-z0-9_-]{1,80}$') {
        throw 'Service name and event source must be simple Windows identifiers.'
    }
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        throw "Service '$Name' already exists. Use the separately reviewed commissioning/rollback procedure for an existing installation."
    }
    $resolved = Resolve-PublishRoot -RequestedPublishRoot $RequestedPublishRoot
    $serviceDirectory = Join-Path $resolved 'service'
    $serviceExe = Join-Path $serviceDirectory 'FluxVault.Service.exe'
    if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) { throw 'Protected service executable is missing.' }
    Assert-ProtectedServicePayload -ServiceDirectory $serviceDirectory

    # No data creation, old-service replacement or start occurs in this registration step.
    sc.exe create $Name binPath= "`"$serviceExe`"" start= demand obj= LocalSystem DisplayName= 'FluxVault Service' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Service registration failed with exit code $LASTEXITCODE. Reconcile the service registration before another attempt." }
    if (-not [Diagnostics.EventLog]::SourceExists($Source)) { New-EventLog -LogName Application -Source $Source }
    $service = Get-Service -Name $Name -ErrorAction Stop
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw 'Fresh registration is not stopped. Reconcile registration before commissioning.'
    }
}

Assert-Administrator
Register-FluxVaultService -RequestedPublishRoot $PublishRoot -Name $ServiceName -Source $EventLogSource
Write-Host 'FluxVault service registered, stopped, with demand start. Complete authenticated single-vault commissioning before enabling runtime.'
