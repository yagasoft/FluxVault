param(
    [Parameter(Position = 0)]
    [string]$Command = "doctor",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProfilePath = Join-Path $ScriptDir "fluxvault-index.json"
$CentralScript = "D:\Codex\code-indexer\code-indexer.ps1"

if ($env:FLUXVAULT_CODEX_RUNTIME_ROOT) {
    & $CentralScript -Profile $ProfilePath -RuntimeRoot $env:FLUXVAULT_CODEX_RUNTIME_ROOT $Command @RemainingArgs
    exit $LASTEXITCODE
}

& $CentralScript -Profile $ProfilePath $Command @RemainingArgs
exit $LASTEXITCODE
