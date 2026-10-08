# Native interruption after recovery publication

Bounded S07 continuation of the authorised single-vault manual workflow,
based on `40f63f1`. The default-off `-RunInterruptedEffectTests` extends the
existing owned A/B, SYSTEM task, contained jobs and private SSPI cluster. It
introduces no product hook, package/profile/trust change or normal installation
operation. The overall roadmap and security gates remain unchanged.

The fixture records intent before creating an exact-operation completion
trigger/function in its private control schema. After durable admission, the
matching completion UPDATE sleeps for ten seconds and raises rather than
committing late. SYSTEM records the admitted row and the unique completion
backend in PgSleep. Creator A independently hashes the published require-new
output while its original request remains pending and reads the original
unknown receipt. Its bounded checkpoint is assertion data, never privileged
stop or cleanup authority.

Only protected SYSTEM PID/start/executable journals, native owner SID and
owned-job membership authorise termination. The runner retains the native
process handle before stopping that process, confirms its nonzero exit and
records actual death before the query-start-plus-eight-second deadline. It
joins the disconnected creator, wrapper/task and contained job before reopen.
The replacement waits for the exact completion backend to disappear, verifies
state admitted with no response, matches the recorded object definition hashes
and removes only that trigger/function before ordinary product open.

The [accepted native run](native/87bf46aeb2384923be5158174c949f73/result.json)
passes A5 before/A7 after, alongside the existing A132 creator workflow and
nineteen ungranted-user command denials. SYSTEM process 16252, started at
03:42:11.9621841Z on 5 October 2026, exits with `-1` at 03:42:15.7399252Z.
Its completion query began at 03:42:15.174154Z; the recorded deadline was
03:42:23.174154Z. After reopen, surviving bytes and the original unknown
receipt match. A deletes only its verified output, making duplicate execution
observable: exact original-ID replay cannot recreate it. An explicitly new
operation then recovers independently matching bytes. Private PostgreSQL
identity remains unchanged through the interruption and reopen.

Two failed attempts remain evidence, not acceptance. The first exposed
PowerShell callback locals shadowing the SYSTEM dispatch context; retaining
that context fixes the identity lookup. The second actually stopped the owned
server in time and passed A5, but its PID-only observer returned a null exit
code and correctly failed correlation. Retaining the native handle fixes exit
collection; an actual owned-process regression proves both observers receive
the matching exit and join. Both failed attempts completed teardown.

The executable invalid-batch regression retains its original failure. Actual
stop-gate checks refuse expired/wrong-operation evidence without terminating
a process. All 63 final focused tooling/configuration checks pass without skips;
the complete Release solution builds with zero warnings/errors and both
PowerShell entry points parse. The first final-test command incorrectly selected
two unchanged PostgreSQL-only cases outside their owned fixture. Both refused
at the ownership guard; `incorrect-filter.trx/log` retains this diagnostic.
The corrected focused filter includes `Category!=RequiresPostgreSql`; those
database cases retain their separately executed 40/40 owned-integrity evidence.
The independent Astra reviewer accepts source, runtime, final checks and
teardown without a blocking finding. This does not replace whole-branch G01.

The [read-only census](process-census.json) covers all three attempts: all 104
captured process identities are absent, all 135 ownership resources are
retired, and roots, accounts, group and task are gone. Normal service identities,
PostgreSQL authentication files, installed configuration and unrelated primary
edits remain unchanged. Every fixture-owned actor and tool is joined.

This proves one controlled process death after an observable recovery effect,
not a reboot, PostgreSQL outage or every possible command interruption.
Existing WAL, acknowledgement-loss and client-ledger evidence remains
applicable. Packaged identity, source-denial/no-background checks, implemented
UI accessibility/scale, final G01 and separately approved rollout remain open.

```powershell
./eng/test-windows-database-boundary.ps1 -FixtureId 87bf46aeb2384923be5158174c949f73 `
  -RunSingleVaultTests -RunInterruptedEffectTests `
  -EvidenceDirectory ./docs/verification/2026-10-05-next002-interrupted-effect/native/87bf46aeb2384923be5158174c949f73
```
