# Owned PostgreSQL integrity suite preparation

Status: implementation prepared; native execution awaits the scoped resource extension below. G01 has not passed. This extends the existing disposable Windows fixture, not the installed PostgreSQL or FluxVault service.

The old `eng/test-repository-integrity.ps1` elevated/unprotected trust runner has been replaced by an entry point to `eng/test-windows-database-boundary.ps1 -RunIntegrityTests`. No new toolchain or persistent environment is created. Prebuilt Integration/TestHost binaries run from protected copies, with absolute trusted dotnet, private working/TEMP/results paths and a fresh parent-held SYSTEM process job. Startup admission, finite deadlines, failure recovery, joined descendants and unchanged-installation checks remain.

## Concrete extension requiring operational authorisation

Existing approval covers `FVGateA_261003`, `FVGateB_261003`, `FVGate_261003`, the one-off `FluxVault-NEXT002-261003-SYSTEM` task and fresh GUID roots under `C:\ProgramData\FluxVault.Tests\NEXT002`. These remain unchanged. The following additions are restricted to the generated private cluster on a non-5432 port:

- Generated `fv_test_<32 lowercase hexadecimal UUID>` databases, owned by the fixed `fv_gate_service` role. This role has temporary `CREATEDB` in integrity-suite mode only; it remains NOSUPERUSER/NOCREATEROLE/NOREPLICATION, with no added memberships. A durable create intent precedes SQL. Cleanup verifies the generated name, owner and fixture-specific comment before DROP. Interrupted creation before the comment requires verified whole-cluster teardown; no speculative individual DROP occurs.
- SSPI HBA admission for only those generated names and the fixed role, on the two loopback addresses, through the independently observed exact SYSTEM map. Every repository/maintenance connection requires SSPI. Public database privileges are revoked. Other users retain real direct database/storage denial probes.
- A separate empty `fv_gate_trust_control` database and identically named dummy role solely for the retained real trust-rejection regression. The database belongs to the disabled bootstrap owner. The dummy role has CONNECT only, no memberships, no TEMP/CREATE, no repository access and no elevated attributes. Only the exact dummy database/role pair has intentional loopback trust rules. Fixed identity queries establish the actual control, then an SSPI-required client must reject it before its verifier runs. The initial bootstrap still uses SCRAM; its login/password are disabled/cleared immediately after provisioning. No repository uses trust.
- Negative privilege probes attempt `SET ROLE` to bootstrap/control and `CREATE ROLE fv_test_unapproved_role`; all must fail. No additional Windows account or lasting PostgreSQL role is created. The verified whole private cluster is removed on every result, including failure, and the normal services/HBA/ident/configuration must remain unchanged.

The default suite deadline is 300 seconds, bounded to 60–600 by the runner. The scheduled task and actor deadlines derive from it. No SDK build runs as SYSTEM. Test scratch and results remain inside the protected GUID root. Evidence is copied out before owned teardown.

The runner invokes the validated absolute installed .NET 10 `vstest.console.dll` through `dotnet exec`, bypassing SDK/global.json resolution. A writable ancestor cannot select a privileged SDK. The generated-name HBA rules use PostgreSQL 18's documented [database regular-expression matching](https://www.postgresql.org/docs/18/auth-pg-hba-conf.html).

Prepared invocation, using a fresh generated fixture UUID and an evidence child of this directory:

```powershell
pwsh -NoProfile -File eng/test-repository-integrity.ps1 `
  -FixtureId <fresh-32-hex-UUID> `
  -EvidenceDirectory <absolute-worktree-evidence-directory>
```

Rollback is owned fixture teardown from its protected journal, followed by exact process/resource absence and original service/configuration checks. No PostgreSQL restart, normal database write, installed ACL edit or deployment is permitted by this extension.

## Verification to date

- The direct metadata authentication regression [failed before the fix](direct-auth-red/direct-auth-red.trx), then all four focused metadata cases [passed](metadata-auth-green/metadata-auth-green.trx). Direct metadata connections now require SSPI and `pg_catalog`; no insecure authentication setting is exposed in Options.
- Six owned-helper admission cases [failed before the fix](owned-admission-red/owned-admission-red.trx). The [ordinary adapter suite](ordinary-adapters-green/ordinary-adapters-green.trx) then passed all 163 cases with no skips. Unrelated/invalid database names and the permissive old account are refused.
- Release solution and affected Integration builds passed with zero warnings/errors. All affected PowerShell scripts parse; the diff passes whitespace checks.
- The original 37 PostgreSQL-dependent cases are retained. Three additional native cases cover durable database intent/public admission, refused cleanup after identity tampering, and the limit of CREATEDB privileges. The runner requires at least 40 cases, all passing with no skips. These cases remain **unexecuted pending authorisation**.
- Read-only discovery through the actual absolute VSTest DLL found exactly 40 matching cases, including the retained real trust-rejection and process-death cases. Discovery is not execution evidence. Its temporary processes exited; only the original installed service/PostgreSQL processes remain.

The [independent Astra review](independent-review.md) approved concrete preflight after resolving SDK-selection, reused-task deadline and partial-initialisation cleanup blockers. Native execution still awaits the scoped resource extension requested from the user; technical review is not operational authorisation.
