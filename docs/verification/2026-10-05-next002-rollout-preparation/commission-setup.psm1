# Once-only SYSTEM setup launcher. Import performs no operational change.
#Requires -Version 7.2
Set-StrictMode -Version Latest

function Assert-CommissionSetupNativeState {
    param([hashtable]$Context)
    $auth=Get-Module commission-authentication -ErrorAction Stop
    & $auth {
        param($Context)
        Assert-CommissionSystemWorker
        Assert-CommissionPostmaster $Context
        foreach($entry in @(@('pg_hba.conf','FinalHba'),@('pg_ident.conf','FinalIdent'))) {
            $path=Join-Path $Context.DataDirectory $entry[0]
            Assert-CommissionAuthenticationPath $Context $path
            if((Get-FileHash -LiteralPath $path).Hash -cne $Context.Prepared[$entry[1]].Sha256){throw 'Final authentication changed; setup is blocked.'}
        }
    } $Context
    Assert-CommissionSetupNativeService $Context
}

function Assert-CommissionSetupNativeService {
    param([hashtable]$Context)
    $service=Get-CimInstance Win32_Service -Filter "Name='FluxVaultService'"
    if($service.State -ne 'Stopped' -or $service.StartName -cne 'LocalSystem' -or $service.StartMode -ne 'Manual' -or
        $service.PathName -ine '"C:\Program Files\FluxVault\service\FluxVault.Service.exe"'){throw 'Setup requires the exact stopped/demand-start LocalSystem installation.'}
}

function Get-CommissionSetupAssemblyRoot {param([hashtable]$Context) 'C:\Program Files\FluxVault\service'}

function Assert-CommissionSetupExecutable {
    param([hashtable]$Context)
    if($Context.Setup.ServiceExe -ine 'C:\Program Files\FluxVault\service\FluxVault.Service.exe'){throw 'Setup executable is outside the fixed installation.'}
    Assert-VaultFixtureTrustedPath $Context.Setup.ServiceExe -AdditionalTrustedOwnerSid $Context.OperatorSid
    if((Get-FileHash -LiteralPath $Context.Setup.ServiceExe).Hash -cne $Context.Setup.ServiceExeSha256){throw 'Installed setup executable changed.'}
}

function Invoke-CommissionSetupHost {
    param([hashtable]$Context,[string]$TicketPath)
    & (Get-Module commission-authentication -ErrorAction Stop) {
        param($Context,$TicketPath)
        Invoke-CommissionTool $Context $Context.Setup.ServiceExe @('--provision-installation',$TicketPath) $null -TimeoutSeconds 120
    } $Context $TicketPath
}

function Read-CommissionSetupBootstrap {
    param($Ticket)
    $bootstrap=[FluxVault.Windows.Security.WindowsVaultInstallation]::Open($Ticket.BootstrapPath)
    try {@{Matches=$bootstrap.Configuration.Equals($Ticket.Installation);Sha256=(Get-FileHash -LiteralPath $Ticket.BootstrapPath).Hash}}
    finally {$bootstrap.Dispose()}
}

function Invoke-CommissionSetup {
    param([Parameter(Mandatory)][hashtable]$Context)
    Assert-VaultFixtureTrustedPath $Context.WorkRoot -AdditionalTrustedOwnerSid $Context.OperatorSid
    if(Test-Path -LiteralPath (Join-Path $Context.WorkRoot 'setup-completed.json')){throw 'Setup already has a receipt; reconcile instead of retrying.'}
    if(Test-Path -LiteralPath (Join-Path $Context.WorkRoot 'cleanup-failed.json')){throw 'Independent cleanup failed; setup cannot activate.'}
    $cleanupPath=Join-Path $Context.WorkRoot 'cleanup-completed.json'
    Assert-VaultFixtureTrustedPath $cleanupPath -AdditionalTrustedOwnerSid $Context.OperatorSid
    if((Get-Item -LiteralPath $cleanupPath).Length -gt 16384){throw 'Cleanup receipt exceeds its bound.'}
    $cleanup=Get-Content -LiteralPath $cleanupPath -Raw|ConvertFrom-Json -AsHashtable -Depth 8
    if($cleanup.ContextSha256 -cne $Context.IdentitySha256 -or -not $cleanup.CanActivate -or -not $cleanup.AdmissionRetired -or
        -not $cleanup.OwnedJobJoined -or -not $cleanup.FinalRetained -or $cleanup.State.database -cne $Context.ServiceDatabase -or
        $cleanup.State.role -cne $Context.ServiceRole -or [int]$cleanup.State.port -ne $Context.Port){throw 'Retired final admission is not confirmed; setup is blocked.'}
    foreach($key in $Context.Postmaster.Keys) {
        if(-not $cleanup.Postmaster.ContainsKey($key)){throw 'Cleanup postmaster binding is incomplete.'}
        if($key -eq 'StartedUtc') {
            if(([DateTimeOffset]$cleanup.Postmaster[$key]).UtcDateTime -ne ([DateTimeOffset]$Context.Postmaster[$key]).UtcDateTime){throw 'Cleanup postmaster start changed.'}
        }elseif([string]$cleanup.Postmaster[$key] -ine [string]$Context.Postmaster[$key]){throw 'Cleanup postmaster binding changed.'}
    }
    Assert-CommissionSetupNativeState $Context
    Assert-CommissionSetupExecutable $Context
    $assemblyRoot=Get-CommissionSetupAssemblyRoot $Context
    foreach($name in @('FluxVault.Abstractions.dll','FluxVault.Core.dll','FluxVault.Windows.dll')) {
        $path=Join-Path $assemblyRoot $name;Assert-VaultFixtureTrustedPath $path -AdditionalTrustedOwnerSid $Context.OperatorSid
        $null=[Reflection.Assembly]::LoadFrom($path)
    }
    Assert-VaultFixtureTrustedPath $Context.Setup.TicketPath -AdditionalTrustedOwnerSid $Context.OperatorSid
    $source=[IO.FileStream]::new($Context.Setup.TicketPath,'Open','Read','Read')
    try {
        if($source.Length -eq 0 -or $source.Length -gt 1048576){throw 'Prepared ticket size is invalid.'}
        $bytes=[byte[]]::new([int]$source.Length);$source.ReadExactly($bytes)
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $Context.Setup.TicketSha256){throw 'Prepared ticket changed.'}
    } finally {$source.Dispose()}
    $options=[Text.Json.JsonSerializerOptions]::new();$options.UnmappedMemberHandling='Disallow';$options.MaxDepth=32
    $ticket=[Text.Json.JsonSerializer]::Deserialize([Text.UTF8Encoding]::new($false,$true).GetString($bytes),[FluxVault.Core.Security.FluxVaultProvisioningTicket],$options)
    $null=$ticket.Validate()
    $root=Join-Path $Context.WorkRoot 'setup-intents'
    New-VaultFixtureProtectedDirectory $root
    $ticketPath=Join-Path $root 'installation-ticket.json'
    $copy=[IO.FileStream]::new($ticketPath,'CreateNew','Write','None')
    try{$copy.Write($bytes);$copy.Flush($true)}finally{$copy.Dispose()}
    if($Context.ContainsKey('SetupWorkerIdentity')) {
        & (Get-Module commission-authentication -ErrorAction Stop) {
            param($Context)
            Write-CommissionAuthenticationReceipt (Join-Path $Context.WorkRoot 'setup-ready.json') @{
                ContextSha256=$Context.IdentitySha256;Process=$Context.SetupWorkerIdentity;WindowsSid='S-1-5-18';TicketSha256=$Context.Setup.TicketSha256}
        } $Context
    }
    # The native SYSTEM process creates the final owner/DACL before the host
    # opens this exact ticket. Existing roots/tickets are never adopted.
    $hostResult=Invoke-CommissionSetupHost $Context $ticketPath
    if($hostResult.ExitCode -ne 0 -or -not $hostResult.Joined){throw 'Setup host failed or is not joined; retain state and do not retry automatically.'}
    $verified=Read-CommissionSetupBootstrap $ticket
    if(-not $verified.Matches){throw 'Published bootstrap differs from the prepared creator/storage binding.'}
    Assert-CommissionSetupNativeState $Context
    $receipt=@{ContextSha256=$Context.IdentitySha256;SetupHost=$hostResult;BootstrapVerified=$true;BootstrapSha256=$verified.Sha256;
        TicketSha256=$Context.Setup.TicketSha256;CreatorSid=$ticket.Installation.CreatorSid;InstanceId=$ticket.Installation.Endpoint.InstanceId.ToString('N');
        VaultId=$ticket.Installation.Binding.Id.Value.ToString('N');CreatorConfirmationRequired=$true;CanActivate=$false}
    $encoded=[Text.UTF8Encoding]::new($false).GetBytes(($receipt|ConvertTo-Json -Depth 8))
    $record=[IO.FileStream]::new((Join-Path $Context.WorkRoot 'setup-completed.json'),'CreateNew','Write','None')
    try{$record.Write($encoded);$record.Flush($true)}finally{$record.Dispose()}
    return $receipt
}

Export-ModuleMember -Function Invoke-CommissionSetup
