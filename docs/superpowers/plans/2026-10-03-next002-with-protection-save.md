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
- The [single-vault execution milestone](../../verification/2026-10-04-next002-execution/README.md) completes the bounded S1 product path: protected bootstrap/service composition, one catalogue/runtime/mutation gate, full-record saves, effective retention permissions, native creator save → backup → history → verified file/folder recovery and ungranted-user refusal. Fresh ordinary suites pass 929 tests; the Release build has no warnings/errors. Independent review approved this bounded scope. S2, full G01 and rollout remain open. Older two-vault results are historical evidence; no further dedicated multi-vault runs are planned.

## Remaining implementation sequence

### S1 — First authorised single-vault round trip

1. Stop multi-vault work; verify no affected temporary resources remain. Update only affected intent/docs and current code. Use one installation binding, one runtime, one mutation gate and one bounded SSPI metadata pool. Remove product create/duplicate/discovery/switch routes and profile controls. Preserve binding/revision/receipt refusal.
2. Add meaningful red regressions for untrusted inherit-only storage grants and overly broad trusted-root principals; fix those before native executor exposure. Fresh roots must inherit only protected SYSTEM/Administrators rights.
3. Compose the authenticated service path and client binding/revision/operation flow. The accepted configuration is a full record; the VM retains pending edits until a confirmed save. Saving cannot overtake an active mutation. Retention triggered by backup requires the corresponding destructive permission, recorded in its receipt.
4. Through the real pipe, native creator A saves selections, captures actual caller-readable files, lists their history and restores file/nested-folder outputs with independently compared bytes. Ungranted B cannot inspect/operate; forged/wrong bindings fail before storage. Use the owned fixture and actual catalogue/executor, with no test-only allowlist standing in for product authorisation.
5. Effort checkpoint: demonstrate this executable path and identify any remaining gate. Do not expose an incomplete privileged path in the normal installation.

### S2 — Applicable command surface and interruption semantics

- Complete the total command-policy table; unsupported/unknown commands fail before admission. Read/recovery/configuration/capture/access/maintenance/destructive permissions remain distinct. Multiple-vault lifecycle commands are removed.
  The [atomic toggle/sync-status batch](../../verification/2026-10-04-next002-pause-sync/README.md) enables the existing protection toggle as a catalogue-only full-record change with revision and completed receipt committed together, and sync status as an authorised read without activation. Real-store held/lost acknowledgement, replay, cancellation and stale/payload refusal checks retain the command contracts. Native creator pause → unchanged history → resume → capture → independently verified recovery passes; ungranted B is denied both commands. All 1,178 ordinary tests and the zero-warning Release build pass. Both private fixtures and 24 captured process identities are gone, with normal services/data unchanged. The activity control remains disabled while background/VSS is unavailable; explicit pause/resume UI remains in NEXT-004. Conflict handling and the other S2/final gates remain open.
  The [definite-refusal batch](../../verification/2026-10-04-next002-command-refusal/README.md) fixes unsupported pre-admission saves being mistaken for uncertain operations. Actual Protect/Options flows retain edits and prevent dependent backup while retiring only the matching refused attempt. Historical receipt lookup and post-admission uncertainty remain unchanged. Independent review approved the correction; the final ordinary suite passed 1,042 tests with no skips and the Release build has zero warnings/errors. Remaining command capability work stays open.
- Complete caller-authorised source/output handling for remaining commands, including preview/export and background/VSS capture. Logged-off/reboot paths must report blocked protection when caller authority is unavailable, never escalate to LocalSystem reads.
  The [file-preview batch](../../verification/2026-10-04-next002-preview/README.md) is complete: fresh caller-authorised verified publication, read-only user-context preparation, durable replay, warnings retained after viewer failure and bounded private-cache expiry configured in Options. Native creator A passed 18 checks, SYSTEM passed 12 and ungranted B was denied. The 978 ordinary tests and zero-warning Release build passed; independent review accepted the bounded scope and teardown. Export/background/VSS and the broader gates remain open.
- Preserve full history semantics through bounded paging, finite framing/admission/concurrency limits, joined cancellation/shutdown and disposal of all caller tokens, streams and child processes.
  The [native pipe resource checks](../../verification/2026-10-05-next002-resource-boundary/README.md) prove saturated-frame expiry, bounded active handlers, absolute slow-input deadlines, stalled-output/missing-receipt token retirement and joined native I/O shutdown through the actual Windows transport. All 147 Windows tests pass without skips; independent review accepts this bounded same-user verification. No product-runtime change was needed. SYSTEM/A/B, packaged identity, sustained-load fairness, whole-service shutdown and final S06/G01 acceptance remain open.
  The bounded Overview batch removes its unnecessary complete-history read: the existing fifty-version limit is applied in PostgreSQL with exact UTC ticks and ordinal ID ties, selected-manifest validation and unchanged status-cache generation checks. That batch introduced fresh schema v2 and retained schema refusal evidence. [Batch evidence](../../verification/2026-10-04-next002-recent-status/README.md).
  The [recovery-history paging batch](../../verification/2026-10-04-next002-history-paging/README.md) adds scoped bidirectional keysets, transactional generation refusal, bounded header/wire responses and lazy immutable folder contents through authenticated reads. The actual WPF/view-model flow retains failed/stale pages, refuses browsing without verified binding, resolves recorded child identities outside the visible page and joins reads before closing. Options persists a defaulted page-size policy. Fresh metadata is now schema v3; no automatic schema conversion/adoption occurs. Real-store, actual-flow, native verified recovery and owned integrity evidence are retained. Current-file inventory paging and the broader scale/performance, concurrency, lifecycle, accessibility and final review/rollout gates remain open.
  Before current-file transport paging, the bounded [current-entry correctness batch](../../verification/2026-10-04-next002-current-projection/README.md) fixes canonical path/kind identity, exact winner ordering and atomic retained-history fallback. Fresh schema v4 does not adopt or upgrade existing metadata. Real-store red/green cases cover casing, sub-microsecond precision, replay, tombstones, deletion rollback and pointer refusal; 93 metadata checks per loopback, the native verified recovery workflow, 40 integrity cases and 1,209 ordinary tests pass with complete owned cleanup. This is the concrete integrity prerequisite for paging, not a new roadmap programme.
  The [current-file transport batch](../../verification/2026-10-04-next002-current-paging/README.md) now adds forward path keysets, coherent generation/pointer validation and bounded SQL/authenticated wire headers. Protected status omits the complete tracked payload with explicit capability. The actual dashboard binds every page to the initial status identity/revision and only replaces browser entries after a complete validated sweep; failed/cancelled/stale/malformed reads preserve entries and edits, and shutdown joins manual/startup/automatic refreshes. The existing RepositoryBrowse policy controls page counts. A reproduced real file-store reader/publication race is fixed using the existing atomic replacement pattern, with meaningful snapshot/failed/cancelled publication regressions. Native creator A's 122 checks, B's 19 denials and 125 metadata checks per loopback pass; the ordinary suite passes 1,235 cases and builds have zero warnings/errors. The separate owned integrity suite passes 40/40, all five private roots and 72 captured owned processes are gone, and normal services/data and unrelated edits are unchanged. O(n) client accumulation, rendering, wider scale/performance/accessibility and remaining S2/S3 gates stay open.
  The [automatic current-file refresh correction](../../verification/2026-10-04-next002-auto-inventory/README.md) adds a shared runtime cache epoch so another request cannot hide invalidation by warming recent history. Only a complete applied current sweep accepts it; failed pages and status-only save reconciliation retain retry. Fresh physical entries reconcile already loaded folders and selected files without losing pending rules, address, selection or loaded/expanded descendants. Actual Windows filesystem/view-model reds and green regressions, 1,247 ordinary tests, native creator A's 124 checks and ungranted B's 19 denials pass. All forty fresh owned PostgreSQL integrity cases pass; the census removes all four private fixtures, 72 captured processes and 38 database intents, preserving the normal installation. O(n) client accumulation and the existing broader scale/lifecycle/accessibility, unsent-draft, background/VSS, full G01 and separately approved rollout gates remain open.
- Verify durable operation identity/result reconciliation: duplicate requests, payload conflicts, disconnect after commit, process death and restart. Uncertain destructive effects are not automatically replayed.
  The [backup restart batch](../../verification/2026-10-04-next002-client-recovery/README.md) is complete: one per-user record is flushed before dispatch, checked against the original repository/operation/revision after reopening and conditionally cleared under a shared file gate. Completed failures are distinguished from unavailable receipts; corrupt/busy/wrong-binding records retain evidence and block retry. All 263 App tests pass. Save-draft persistence and other interruption cases remain open.
  The [protection-save restart batch](../../verification/2026-10-04-next002-save-recovery/README.md) now retains the full dispatched snapshot and purge intent before IPC, reopens it after verified binding and reconciles only the original receipt. Historical/current-setting differences and purge failures retain a durable review gate. Cross-session discard, strict JSON and optional-telemetry interruption regressions passed; all 296 App tests and the zero-warning Release build passed. Independent review approved this bounded scope.
  The [Options recovery batch](../../verification/2026-10-04-next002-options-recovery/README.md) extends the same ledger to dispatched Options saves, preserves its existing filenames and defaults omitted origins to Protect. Original receipt/current-status checks, explicit reviewed reload, cross-session/newer-edit refusal and protection-draft preservation are implemented. The 324-case App run and zero-warning Release build pass; bounded independent review approved the bounded scope. Never-dispatched Protect retention is completed below; unsent Options drafts and other interruption cases remain open.
  The [unsent Protect draft batch](../../verification/2026-10-04-next002-unsent-draft/README.md) retains complete editable snapshots in a bounded per-user atomic store, with an Options-configured coalescing delay. Exact draft/receipt association precedes IPC; original and acknowledged baselines reconcile only through the original authoritative receipt. Newer edits survive interrupted receipt retirement, conditional reviewed discard and failed Exit. Actual file-store/view-model regressions pass with 1,313 ordinary tests and a zero-warning Release build; native creator A passes 132 checks, B is denied nineteen operations, and all forty fresh integrity cases pass. All six private fixtures, 111 captured process identities and 38 database intents are gone; normal services/data and unrelated edits are unchanged. This completes never-dispatched Protect retention, not never-dispatched Options editing or the remaining lifecycle/concurrency/performance and final rollout gates.
- Retain repository leases, retention/purge, repair/rebalance/drain, outbox/lineage and corruption/last-good-copy invariants against the single verified binding. No extra abstractions for hypothetical future multi-vault support.
  The [committed save/purge batch](../../verification/2026-10-04-next002-save-purge/README.md) enables confirmed selection removal through catalogue CAS and the authorised executor without a second save. Malformed scopes fail before CAS; full reference closure cannot delete preserved history. Saved-but-failed purge retains the actual UI review gate and blocks dependent backup; cancellation remains uncertain and replay returns the retained receipt. Native creator A passed 32 checks with independent recovery; teardown preserved the normal installation. The 1,055-case ordinary suite and zero-warning Release build pass, with bounded independent review. Other applicable maintenance/interruption commands and final gates remain open.
  The [authorised maintenance batch](../../verification/2026-10-04-next002-maintenance/README.md) enables health, retention, scrub, protected rehearsal, repair/placement previews and execution, and drain preview through the same binding/permissions/receipts. Malformed mirror selection fails before admission; health publication supports open snapshot readers and cleans failed/cancelled output; retention cannot leave stale cached history after partial failure. Native creator A passed 56 checks with exact receipts and independent recovery after pruning; ungranted B was denied all ten maintenance commands. All 1,077 ordinary tests and the zero-warning Release build pass; independent review accepted runtime and complete resource cleanup. Drain execution follows below; remaining applicable command/background/interruption and final gates stay open. No roadmap or acceptance gate is expanded.
  The [mirror-drain batch](../../verification/2026-10-04-next002-drain/README.md) publishes the verified repository result, selected mirror disablement, configuration revision and receipt atomically through catalogue CAS. Incomplete/failed drains keep the mirror enabled; stale/cancelled/unconfirmed completion stays uncertain and never automatically repeats repository effects. The actual view-model retains pending selections and cannot regress a newer accepted configuration. Real-store checks pass 73 cases per loopback; native creator A passes 63 workflow checks with subsequent backup/history/independent recovery, ungranted B is denied eleven maintenance commands, and SYSTEM verifies physical mirror effects. The 1,099-case ordinary suite and zero-warning Release build pass; all four owned fixtures and 46 captured processes are gone, with the normal installation unchanged. Remaining command/background/interruption, combined G01, performance/accessibility and separately approved rollout gates remain open.

  The [diagnostics export batch](../../verification/2026-10-04-next002-diagnostics/README.md) adds history-authorised publication through the existing caller output target, bound versioned JSON and retained receipt/warnings. Invalid directory syntax is refused before admission; cancel dismisses the picker without dispatch; lost/malformed acknowledgements remain uncertain, with pending protection edits retained. Native real-store checks pass 83 cases per loopback, creator A passes 72 checks and ungranted B is denied twelve commands. All 1,118 ordinary tests and the zero-warning Release build pass; both fixtures and temporary workers are gone, with the installed services unchanged. Remaining S2/background/interruption and final rollout gates stay open.

  The [selection-recovery batch](../../verification/2026-10-04-next002-selection-recovery/README.md) completes caller-authorised selection preview and latest file/folder recovery. Preview creates no output or receipt; execution preserves new-destination and no-folder-merge gates, publishes one bounded fallback tree and returns actual verification/warnings through durable receipts. The real store rejects invalid paths before admission; the actual view-model retains drafts and refuses false verified success. Native checks pass 97 catalogue cases per loopback, 91 creator checks and fourteen ungranted-user denials. All 1,168 ordinary tests and forty owned PostgreSQL integrity cases pass without skips; builds have zero warnings/errors. Four fixtures and 59 captured processes are gone, with normal services/data unchanged. Remaining pause/conflict/sync, background/VSS, paging/concurrency/lifecycle, unsent drafts and other interruption cases, full G01, performance/accessibility and separately approved rollout remain unchanged and open.

### S3 — Combined verification and independent closeout

- Focused real-store and actual VM regressions first; relevant combined suite, zero-warning Release build and full owned PostgreSQL integrity suite afterward. Adapt the legacy full-suite runner to the protected owned fixture before using it; its old elevated trust/unowned-process path is not suitable.
- The [G01 runner adaptation and authorised native run](../../verification/2026-10-04-next002-integrity-runner/README.md) passed the original 37 database cases and three fixture/recovery regressions, without skips. Independent review accepted complete teardown and unchanged normal installation. The broader G01 final-implementation gate and normal installation/rollout authority remain separate.
- Native standard/elevated/packaged identities, direct DB/storage refusal, source/output ACL/race cases, allowed save/recovery and interruption/resource checks remain applicable. No identity/database case may silently skip.
  The [native access extension](../../verification/2026-10-05-next002-native-access/README.md) passes forty actual elevated-user/ordinary-user assertions. Direct-user read access is replaced by group-only access, with denial before membership and success from a fresh member token. Five stronger command classes and the elevated actor's real receipt remain denied; group grants persist across protected service-object reopen, and revocation denies new history work. Existing A132/B19 native recovery checks pass. Independent review accepted runtime and teardown: all 27 captured process identities and fixture resources are removed, with the normal installation unchanged. This is not packaged identity, process restart or completion of S01/G01.
- Record C01–C03, S01/S02/S04–S08, D01 and G01 with exact evidence; S03 is explicitly retired. Obtain independent consequential security/integrity review, resolve blockers and rerun affected checks only.
  The [native process-replacement checks](../../verification/2026-10-05-next002-service-restart/README.md) verify completed save/backup receipts, a retained admitted unknown outcome without re-execution after its obstruction is removed, complete configuration, explicit group access and independently hashed recovery across actual clean SYSTEM server-process replacement. All 53 added native checks, the existing A132/B19 workflow and 59 focused tooling tests pass; the Release solution build has no warnings/errors. Independent review accepts runtime and teardown: all four servers, 58 captured processes and 112 journal resources across both rehearsals are retired, with private PostgreSQL unchanged during each replacement and the normal installation unchanged throughout. This completes bounded clean-restart cases; interrupted effects, reboot, packaged identity, full S07/G01 and normal rollout remain open.
  The [native interrupted-effect extension](../../verification/2026-10-05-next002-interrupted-effect/README.md) proves actual owned SYSTEM process death after durable admission and independently verified recovery publication, before receipt completion. Native A5/A7 verify exact unknown reconciliation after reopen, original-ID replay without repeated publication after deleting the owned output, and successful independently verified recovery under a new operation. The existing A132/B19 workflow and final 63 focused checks pass; the complete Release build has zero warnings/errors. Independent review accepts bounded delivery and teardown: all 104 captured identities and 135 journal resources across three retained attempts are retired; private PostgreSQL remains unchanged during reopen and the normal installation is unchanged. Whole-branch G01 remains separate. This supplies the bounded in-flight death case alongside existing WAL, acknowledgement-loss and client-ledger proofs; it does not add a reboot/crash matrix or authorise rollout.
  The [packaged desktop identity preparation](../../verification/2026-10-05-next002-packaged-identity/README.md) passes 56 focused checks and a zero-warning Release build. A reproduced SignTool key-persistence leak is corrected with flushed private import intent, silent native enumeration and same-object deletion only for new matching RSA fingerprints. Actual signing and interrupted import prove fixture-key removal and preservation of unrelated keys; all six fixture signing keys, seven roots and 59 captured processes are absent. The separately approved integrity refresh passes all forty cases. Independent review accepts the bounded preparation and correction. Native A/B registration, temporary per-user public-certificate trust and Windows profile creation await their bounded resource approval. S01 packaged behaviour and full G01 remain open; no normal rollout or PostgreSQL restart is authorised by this preparation.
- Prepare exact normal installation target, preserved data/rollback and live probes. Request the separately gated rollout approval only after preparation and technical review; no PostgreSQL restart.

The [manual source/startup continuation](../../verification/2026-10-05-next002-manual-source/README.md)
closes bounded S04: actual locked-source backup fails without capture/deletion
or history loss, original replay remains failed after unlocking, and a new
operation recovers independently matching bytes. Native service replacement
with enabled protection and no owner request leaves durable state unchanged;
explicit manual backup then captures the pending note. Existing denied-source
evidence and reviewed scheduler-free composition remain applicable. Native
A141/B19 and restart A9/A19 pass, all 148 Windows/63 focused tests pass, and
Release has zero warnings/errors. All 55 captured identities/56 resources are
retired; normal installation is unchanged. No scheduling, VSS, reboot capability,
rollout authority or wider roadmap scope is added. Final G01 remains separate.

The [installation preparation](../../verification/2026-10-05-next002-rollout-preparation/README.md)
now implements the independently reviewed once-only native-creator provisioning
contract and passes its real component/private-pipe setup → joined host → ordinary
open → save/backup/history/hash-verified recovery checkpoint. Full target/both
namespace checks precede effects, creation is exclusive/protected, partial
attempts remain latched and require-new bootstrap publication activates the vault.
Native setup36/A143/B19, Core105/Windows154/tooling63 and the zero-warning Release
build pass; independent review accepts the checkpoint. Both owned attempts retire
85 captured identities/80 resources, preserving normal services/data and unrelated
edits. The shipping creator confirmation command and installer ordering now pass
75 combined CLI/registration/compiled-MSI checks and the real native
CLI → setup → joined host → ordinary save/backup/history/hash-recovery workflow.
The fresh CLI run retires all 44 identities/43 resources, preserving normal
services/authentication/configuration and unrelated edits. Next prepare the
exact candidate/authentication delta/rollback and separately approved rollout.
Fixed-path Service.exe execution, packaged identity, D01/scale and final G01 stay
open. The overall roadmap and architecture are unchanged.

## UI scope

The [Protection failure visibility correction](../../verification/2026-10-05-next002-desktop-observation/README.md)
adds full wrapped save/local-draft explanations and useful native primary-control
names. Three actual rendered-control regressions reproduce the defects before
the correction; all 478 App tests and the zero-warning Release build pass.
Two disposable desktop observations retain one failed save, zero dependent
backups and pending drafts, then join their processes and remove their roots.
Native automation confirms the corrected states/names. Stale desktop capture
pixels remain an unresolved observation, so this does not close visual D01,
Narrator/DPI/high-contrast/scale or G01. No command/security contract or overall
roadmap changes.

The [manual maintenance Options correction](../../verification/2026-10-05-next002-manual-options/README.md)
removes an unavailable scheduling promise and preserves its exact saved fields
through the real file store and actual view-model command. All 475 App checks
pass, the complete Release build has zero warnings/errors, and independent
review accepts the bounded correction and rendering. Owned test/build processes
are absent and the normal installation is unchanged. This keeps the current
slice manual-only; the source/startup and interrupted-effect continuations above
now supply their bounded proofs. Packaged identity, implemented-screen accessibility/scale, final G01 and rollout
gates remain applicable. It does not add a scheduler or broaden NEXT-004.

The completed [Overview → Protect → Recover concepts](../../ui-concepts/2026-10-03-next002/README.md) remain historical evidence. Their vault selectors and multi-vault concepts are superseded. Stop further multi-vault exploration. The existing UI should expose one vault, saved/draft configuration, busy/blocked/error/unknown outcomes and verified recovery results tied to the actual command contracts. Existing keyboard, Narrator, DPI, high-contrast and responsiveness gates remain for implemented UI; the broader redesign retains its roadmap position.

## Verification commands

Use `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`, `--disable-build-servers -p:UseSharedCompilation=false -nodeReuse:false`. Run the narrowest affected test family before the combined solution build/tests. Keep raw failure/green/native evidence and verify every launched process has exited. A successful helper test is not full S1 or rollout proof.

The user's earlier model-switch planning pause ended with implementation approval. This scope change needs no routine planning approval. Continue authorised work; ask only for a material unresolved decision or the separately gated installation rollout.

## Completion critical path

Use the existing C01–G01 acceptance matrix as the checklist. The creator/installer
checkpoint `9815673` and its accepted native evidence remain valid; `02d0485`
only normalises Git text staging and pins native bytes. Candidate preparation
`2ce19df` produces one frozen unsigned v1.0.5 package with 208 payload files.
Its actual MSI tables pass stopped/demand-start/no state creation checks.

Complete the pending S01 packaged A/B rehearsal within its existing profile/trust
approval; finish D01 implemented-screen keyboard/Narrator/DPI/high-contrast/scale
and resolve stale capture; complete S05 exact authentication, supervised setup,
activation/join and checked legacy rollback; then consolidate final combined
checks and whole-branch independent G01 review. Present the concrete candidate,
exact changes, rollback and live probes for separate rollout approval only after
preparation is complete. No second plan or verification programme is needed.

C01–C03, S02, manual-only S04, S06, S07 and S08 retain their existing evidence
while relevant code/dependencies/environment remain unchanged. S03 is retired.
Normal-instance setup/fixed-path execution must still be proven after approved
rollout. Packaged resource approval is pending; commissioning implementation
and remaining observations are unfinished work, not reasons to restart the audit.

Completion update: the scoped normal PostgreSQL guard and independent finite
SYSTEM authentication cleanup now have functional/native evidence in the existing
[preparation record](../../verification/2026-10-05-next002-rollout-preparation/README.md).
Forced operator exit, mid-write and before-truncation interruption recover exact
original authentication and block activation; successful final state stays final.
The combined commissioning checks pass 101/101. Retained v1.0.4 MSI tables match
the captured rollback service policy, so use that installer and verify its result.
S01 permission, complete D01 observations/scale, exact normal setup/activation
and rollback execution preparation, final G01 and separately approved rollout/live
validation remain. The native Computer Use surface now initialises, but window enumeration and capture return inconsistent handles; its single recovery retry fails. Guided manual observations await the user. Preserve existing gates and evidence; do not substitute
logical WPF renders for those outstanding desktop observations.

Completion update: one executable fixed-candidate installation/rollback procedure
now connects stopped installation, independent authentication retirement, native
creator confirmation, joined SYSTEM setup/verified bootstrap and service
activation. Recovery joins owned tasks from pre-registration intents even after
lost launch acknowledgements; unmatched installer intents block further installer
work until external MSI state is reconciled. The retained v1.0.4 installer is
copied by exact hash into the protected commissioning root before execution.
The exact native rollback worker restores original authentication and retires
administrator access in the existing private fixture, with joined resources and
normal state unchanged. The read-only procedure matches all 208 candidate/all 204
legacy files and its 4789-byte worker context fits the existing 64 KiB bound.

Pending S01 resource approval/proof, D01 actual desktop/accessibility/scale,
whole-branch G01 and separate normal rollout/live acceptance remain. This closes
coherent executable preparation, subject to its consolidated technical review;
fixed installed entry/SCM effects and legacy reinstall still need the approved
normal rollout. No new plan, gate or roadmap scope is introduced.

Full-branch review found a C02/S07 response interruption defect. The production
client now classifies incomplete/invalid response frames as transport uncertainty;
actual-client/real-store Main/Options save, backup and original-receipt regressions
retain records and newer edits without dependent backup or blind retry. The
replacement unsigned candidate is `51b103b3c75645dfaa36600e2cbe07a5`, source
`e473636`, with exact hashes and read-only installation preflight in the existing
preparation record. Combined ordinary checks and the existing native workflow
pass. Independent Astra review accepts the code/preparation portion of G01 and
closes the C02/S07 blocker. Unaffected creator/installer, authentication/path and
interruption checkpoints stay valid. S01 permission/proof, D01 actual-desktop/scale
observations and separately approved normal installation/live workflow remain
the critical path; the matrix and scope are unchanged.

The 5 October approval was exercised for the exact packaged A/B profiles and
per-user trust. Native registration reaches `Add-AppxPackage` but fails with
`0x800B0109`; all owned resources are retired. The existing package module/runner
prepares one default-off machine TrustedPeople public-leaf fallback, requiring
separate resource approval, and an independently reopened per-user publication
diagnostic. D01's approved bounded manual session closed/joined without human
observations; a ready guided session or repaired pixels is its concrete
dependency. These fixture-only corrections leave the candidate, C01–G01 matrix,
single-vault architecture, commissioning procedure and roadmap unchanged.

7 October delivery checkpoint: the approved fixed-fixture machine-leaf rehearsal
now closes S01's actual packaged A/B proof, with independently verified creator
recovery and complete trust/resource retirement. Independent Astra review accepts
the bounded correction, native evidence and refreshed normal process pins; the
candidate, security/configuration contracts and rollback ordering are unchanged.
Current read-only Prepare matches 208 candidate/204 legacy files. Historical
normal baselines are preserved after the external 6 October service restart.
D01 awaits a ready actual desktop session with the required accessibility/display
and non-empty inventory observations. Final G01 consolidation, the separately
approved normal installation/S05 and installed live workflow remain the critical
path. Do not rerun accepted checkpoints or expand the roadmap.
