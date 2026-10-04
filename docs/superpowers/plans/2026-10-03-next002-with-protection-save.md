# NEXT-002 with the bounded protection-save batch

Status: authorised implementation continues in `codex/next002-planning-20261003`, based on `afdfe1f`. Scope revised by the user on 4 October 2026 to exactly one logical vault per Windows installation. This replaces the earlier multi-vault design and dedicated validation requirements. Existing completed code/evidence remains preserved. No normal installation rollout, merge or PostgreSQL restart is authorised by this plan.

Goal: the authorised single-vault save → backup → history → verified file/folder recovery workflow, with creator ownership, safe source/output access and truthful interruption results. Multiple protected folders, file types and mirror destinations remain supported.

Architecture and roadmap sequence remain Windows/WPF/.NET/service/PostgreSQL, local-first. Remove product profiles, switching, additional-vault creation, duplication, discovery and multiple runtime management. Keep the minimum immutable repository identity, physical storage binding, metadata namespace, configuration revision and operation receipts for correctness and recovery. See the [revised acceptance matrix](../specs/2026-10-03-next002-and-protection-save-design.md); S03 is retired, S02 retains wrong-repository binding refusal. All other applicable gates retain their strength.

## Fixed constraints

- Reuse this task worktree and preserve unrelated primary-checkout index edits and local main history.
- The creating Windows user owns the single vault. Other users/groups need explicit grants. Never auto-share, accept a claimed owner SID or infer ownership of staging backups.
- Use the existing Windows toolchain. Temporary processes/resources must be journalled, joined and verified gone. Preserve unrelated databases; do not restart normal PostgreSQL.
- Fresh private storage is provisioned with SYSTEM/Administrators owner/access and safe inherited child permissions. Do not adopt populated storage by changing its root ACL.
- Missing/wrong bootstrap, repository marker, namespace, endpoint or physical binding fails closed before repository work. No legacy privileged/profile-manager fallback.
- Existing staging data remains recoverable with the retained v1.0.4 installation/assets. Normal rollout needs an exact target, reviewed evidence and rollback followed by the separately required operational approval.

## Completed capability and retained evidence

Batch C fixes both configuration defects and retains actual view-model command tests through both real store routes: [red/green and native evidence](../../verification/2026-10-03-protection-save/README.md). Untouched MetadataStore, RepositoryMaintenancePolicy, Sync, DiagnosticsPolicy and IsEnabled survive protection saves. Failed/cancelled/unknown saves retain drafts, explain the result and prevent dependent backup. Do not replace these tests with source-string assertions.

Completed, bounded prerequisites remain evidence rather than a claim of full service composition:

- [Owned Windows SSPI fixture](../../verification/2026-10-03-next002-windows-fixture/README.md): SYSTEM admission, ordinary-user direct DB/file denial, unchanged normal installation and complete teardown.
- [Authenticated bounded pipe](../../verification/2026-10-04-next002-pipe/README.md), [transactional admission/receipts](../../verification/2026-10-04-next002-authorisation/README.md), [bound metadata](../../verification/2026-10-04-next002-metadata/README.md), [repository markers/manifests](../../verification/2026-10-04-next002-repository/README.md).
- [Caller-bound source handles](../../verification/2026-10-04-next002-source/README.md), [verified caller-authorised recovery](../../verification/2026-10-04-next002-output/README.md), [source inventory/live capture](../../verification/2026-10-04-next002-source-inventory/README.md): unavailable/disappearing roots preserve history, confirmed missing children may tombstone, verified outputs preserve existing destinations. Latest completed combined suite: 865 passing tests and zero-warning Release build.
- The [single-vault execution milestone](../../verification/2026-10-04-next002-execution/README.md) completes the bounded S1 product path: protected bootstrap/service composition, one catalogue/runtime/mutation gate, full-record saves, effective retention permissions, native creator save → backup → history → verified file/folder recovery and ungranted-user refusal. Fresh ordinary suites pass 1,029 tests; the Release build has no warnings/errors. Independent review approved this bounded scope. S2, full G01 and rollout remain open. Older two-vault results are historical evidence; no further dedicated multi-vault runs are planned.

## Remaining implementation sequence

### S1 — First authorised single-vault round trip

1. Stop multi-vault work; verify no affected temporary resources remain. Update only affected intent/docs and current code. Use one installation binding, one runtime, one mutation gate and one bounded SSPI metadata pool. Remove product create/duplicate/discovery/switch routes and profile controls. Preserve binding/revision/receipt refusal.
2. Add meaningful red regressions for untrusted inherit-only storage grants and overly broad trusted-root principals; fix those before native executor exposure. Fresh roots must inherit only protected SYSTEM/Administrators rights.
3. Compose the authenticated service path and client binding/revision/operation flow. The accepted configuration is a full record; the VM retains pending edits until a confirmed save. Saving cannot overtake an active mutation. Retention triggered by backup requires the corresponding destructive permission, recorded in its receipt.
4. Through the real pipe, native creator A saves selections, captures actual caller-readable files, lists their history and restores file/nested-folder outputs with independently compared bytes. Ungranted B cannot inspect/operate; forged/wrong bindings fail before storage. Use the owned fixture and actual catalogue/executor, with no test-only allowlist standing in for product authorisation.
5. Effort checkpoint: demonstrate this executable path and identify any remaining gate. Do not expose an incomplete privileged path in the normal installation.

### S2 — Applicable command surface and interruption semantics

- Complete the total command-policy table; unsupported/unknown commands fail before admission. Read/recovery/configuration/capture/access/maintenance/destructive permissions remain distinct. Multiple-vault lifecycle commands are removed.
- Complete caller-authorised source/output handling for remaining commands, including preview/export and background/VSS capture. Logged-off/reboot paths must report blocked protection when caller authority is unavailable, never escalate to LocalSystem reads.
- Preserve full history semantics through bounded paging, finite framing/admission/concurrency limits, joined cancellation/shutdown and disposal of all caller tokens, streams and child processes.
- Verify durable operation identity/result reconciliation: duplicate requests, payload conflicts, disconnect after commit, process death and restart. Uncertain destructive effects are not automatically replayed.
  The [backup restart batch](../../verification/2026-10-04-next002-client-recovery/README.md) is complete: one per-user record is flushed before dispatch, checked against the original repository/operation/revision after reopening and conditionally cleared under a shared file gate. Completed failures are distinguished from unavailable receipts; corrupt/busy/wrong-binding records retain evidence and block retry. All 263 App tests pass. Save-draft persistence and other interruption cases remain open.
- Retain repository leases, retention/purge, repair/rebalance/drain, outbox/lineage and corruption/last-good-copy invariants against the single verified binding. No extra abstractions for hypothetical future multi-vault support.

### S3 — Combined verification and independent closeout

- Focused real-store and actual VM regressions first; relevant combined suite, zero-warning Release build and full owned PostgreSQL integrity suite afterward. Adapt the legacy full-suite runner to the protected owned fixture before using it; its old elevated trust/unowned-process path is not suitable.
- The [G01 runner adaptation and authorised native run](../../verification/2026-10-04-next002-integrity-runner/README.md) passed the original 37 database cases and three fixture/recovery regressions, without skips. Independent review accepted complete teardown and unchanged normal installation. The broader G01 final-implementation gate and normal installation/rollout authority remain separate.
- Native standard/elevated/packaged identities, direct DB/storage refusal, source/output ACL/race cases, allowed save/recovery and interruption/resource checks remain applicable. No identity/database case may silently skip.
- Record C01–C03, S01/S02/S04–S08, D01 and G01 with exact evidence; S03 is explicitly retired. Obtain independent consequential security/integrity review, resolve blockers and rerun affected checks only.
- Prepare exact normal installation target, preserved data/rollback and live probes. Request the separately gated rollout approval only after preparation and technical review; no PostgreSQL restart.

## UI scope

The completed [Overview → Protect → Recover concepts](../../ui-concepts/2026-10-03-next002/README.md) remain historical evidence. Their vault selectors and multi-vault concepts are superseded. Stop further multi-vault exploration. The existing UI should expose one vault, saved/draft configuration, busy/blocked/error/unknown outcomes and verified recovery results tied to the actual command contracts. Existing keyboard, Narrator, DPI, high-contrast and responsiveness gates remain for implemented UI; the broader redesign retains its roadmap position.

## Verification commands

Use `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`, `--disable-build-servers -p:UseSharedCompilation=false -nodeReuse:false`. Run the narrowest affected test family before the combined solution build/tests. Keep raw failure/green/native evidence and verify every launched process has exited. A successful helper test is not full S1 or rollout proof.

The user's earlier model-switch planning pause ended with implementation approval. This scope change needs no routine planning approval. Continue authorised work; ask only for a material unresolved decision or the separately gated installation rollout.
