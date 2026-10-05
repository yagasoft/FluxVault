# FluxVault developer quickstart

FluxVault has one logical vault per Windows installation, with an authenticated
creator and explicit access grants. Installing binaries does not create a vault
or give every Windows account access. Current NEXT-002 staging packages require
the separately reviewed commissioning procedure before ordinary runtime use.

1. Build a checked package in a fresh output directory. Preserve any existing
   installation, data, authentication and rollback material before an approved
   cutover; do not overwrite an existing staging installation with this guide.
2. The MSI registers a stopped, demand-start LocalSystem service. It creates no
   ProgramData vault directory and does not start runtime before provisioning.
   For developer registration, first place the complete checked payload under
   protected `Program Files\FluxVault`, then run `install-service.ps1` elevated.
   That script refuses an existing service and untrusted payload/dependency ACLs.
3. The reviewed commissioning procedure prepares the restricted local PostgreSQL
   database/account, protected once-only ticket and bounded SYSTEM setup process.
   As the intended native creator, run
   `FluxVault.Cli.exe setup-confirm --instance <installation UUID N>`.
   The optional `--timeout-seconds` defaults to 120 and accepts 1–600 seconds.
   No owner, storage, configuration or pipe selector is accepted.
4. A confirmed response identifies provisioning success. The orchestrator must
   separately join the setup process, verify protected activation, then enable
   delayed automatic runtime start/recovery and start the ordinary service.
   Failed, cancelled or uncertain confirmation requires protected-bootstrap and
   ordinary authorised-status reconciliation; never retry setup automatically.
5. Open the app as the creator or an explicitly granted user. Add multiple
   protected folders and file types, save, run backup, review history and recover
   to an alternate path. Verify recovered bytes independently. Repository/storage
   binding is fixed by commissioning; there is no vault switcher or second vault.
6. A failed or cancelled protection save retains pending edits, explains the
   failure and prevents dependent backup. Resolve that save before starting
   dependent work.

Missing bootstrap fails closed. Starting an uncommissioned service or elevating
the dashboard does not create or adopt vault state. Uninstall preserves vault
state by default; destructive cleanup requires a separate explicit operation.

Logs and diagnostics remain local. Service lifecycle, warning and fatal recovery
events use the Windows Application log under `FluxVaultService`. The current
authenticated runtime is manual-only: scheduling and VSS capture are unavailable.
Locked or inaccessible caller files produce an explained failed capture without
privileged fallback; retry with a new operation after resolving the source issue.
