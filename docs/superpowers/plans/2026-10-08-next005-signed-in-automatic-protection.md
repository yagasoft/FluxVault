# NEXT-005 signed-in automatic protection: design and implementation plan

**Status, 14 October 2026:** the bounded creator-signed-in slice is installed and live-validated as corrective 1.0.11. Its real save → automatic capture → joined service restart → history → independently verified recovery workflow passes, including pending work and displayed Pause/Resume states. The authorised native sign-out/re-entry check passes all 21 cases, including old-authority retirement, retained work and recovery without another save. [Evidence](../../verification/2026-10-08-next005-automatic/README.md) pins the exact candidate, exercised manual-only 1.0.9 rollback, hosted CI and joined cleanup. Final independent branch/live review accepts the bounded slice; source integration is tracked in PR #36. Broader NEXT-005 logged-off/reboot support remains open. [NEXT-004](2026-10-08-next004-protection-controls.md) is separately delivered as 1.0.9 and squash-merged through PR #35 at `41222b6`. The staging plan uses this PC and existing owned fixtures without VMs.

**Goal:** with the creator signed in, save a supported file → durable automatic capture → restart FluxVault → find its version → independently verify recovery, without keeping the desktop open.

**Architecture:** retain one Windows service and one logical vault using PostgreSQL. Add bounded durable scheduling and creator-session source authority to the installed composition. Keep WPF, multiple protected folders/file types/mirror destinations, local-first operation and creator ownership with explicit grants. No broker service, distributed queue, additional database engine, credential store or per-user vault is introduced.

**Contract:** [improvement roadmap](../../improvement-roadmap.md), the [delivered security contract](../specs/2026-10-03-next002-and-protection-save-design.md), and this bounded design. It delivers part of NEXT-005; full logged-off/reboot support and NEXT-006 disaster recovery remain separate.

**Design gate:** [independent Astra review](../../verification/2026-10-08-next004-next005-planning/independent-design-review.md) accepts the corrected extension-table/rollback design. The execution record now supplies implementation, migration, exact-candidate, rollback and installed/native proof for the bounded signed-in slice.

**Staging scope:** use the installed Windows service, WPF client, PostgreSQL and
existing owned process/database fixtures on this PC. FluxVault staging captures,
history and metadata, together with explicitly owned test sources and recovery
outputs, are disposable and may be reset or recreated when a test needs it. Check
the exact owned targets and join their users before cleanup. Retain expected
bytes and evidence until each integrity/recovery/interruption assertion is
resolved; disposability does not turn an unverified outcome into success.
Unrelated files, source repositories, Windows accounts, working-tree changes and
databases are outside this permission. Never restart normal PostgreSQL. No
virtual machine, hypervisor, evaluation image or second Windows environment is a
dependency of this slice. The prior VM/environment decision is superseded.

**Desktop control, 11 October:** the owner withholds mouse, keyboard and desktop
UI automation until explicit consent. Do not take over the desktop, sign out or
reboot Windows under the routine staging authority. Continue repository work and
non-interactive checks; retain completed UI evidence within its unchanged scope.

On 14 October the owner explicitly approved the prepared native sign-out/re-entry
check for the current creator Windows session. This permits that bounded
disruption and its independently owned observer, preserving exact target,
single-use submission, review, process-join and acceptance gates. Broader desktop
control and reboot remain outside this approval. The execution record retains a
refused unsubmitted attempt and a completed observation-only diagnostic hold;
neither is actual sign-out proof.

The subsequent authorised sign-out verified native authority loss, stale-token
refusal, joined old readers and retained pending work. Re-entry recovery timed
out because the owner returned after the observation window. Cleanup and
installation preservation pass. The owner renewed consent for the session-2
retry, which passes actual re-entry capture and independently verified recovery.
All test processes and tasks are cleared. Both single-use approvals are consumed;
broader desktop control and another sign-out remain unauthorised.

## Design-time implementation gaps

The table records the gaps at planning time. Delivered outcomes are above and
in the execution record; it does not describe the current installed runtime.

| Area | Verified current source | Remaining work |
| --- | --- | --- |
| Installed runtime | `Worker` runs the authenticated pipe server and joins its lifetime; `WindowsSingleVaultService` opens the fixed protected binding. Status explicitly says automatic protection unavailable. | Compose a supervised automatic worker without loosening manual request authority or disposal order. |
| Discovery/cadence | `FileSystemProtectionLoop`, `UsnCatchUpService`, Windows journal reader and defaulted `CaptureCadencePolicy` exist. | Pending paths are memory-only; checkpoints are saved before capture/admission. Native watcher error handling, rename reconciliation and root reappearance need closure before activation. |
| Source authority | `WindowsCallerCaptureProvider` and `WindowsProtectionSourceAccess` use validated caller handles on local NTFS. Tokens are request-owned; the executor explicitly does not retain callers. | Obtain and supervise an ordinary creator logon token independently of a desktop request. Never reuse a request after token disposal. |
| Integrity/recovery | Verified publication/reads, reference-safe maintenance, cross-process leases, PostgreSQL identity checks and independently hashed manual recovery are delivered. The reviewed, unexposed foundation adds reserved capture identity, live-source stability guards, durable work/consent and runtime fencing. | Compose these existing primitives with discovery, session ownership, publication/claim reconciliation and supervised shutdown; preserve dirty ranges and prove the new scheduler interleavings. |
| Export repair | Reviewed unexposed code validates existing export payloads, retires pending exports atomically with deletion and drains old orphan work in bounded batches under existing coordination. | Wire retry independent of a subsequent capture and truthful lag into the supervisor. Reuse the completed real-store regressions. This is not a portable disaster-recovery bundle. |

## Signed-in authority and support boundary

Automatic protection is an explicit creator-enabled setting, default **off** when its extension record is absent. `IsEnabled` remains the persistent global pause gate; a new typed, defaulted `AutomaticProtectionPolicy.Enabled` controls automatic operation. Store this policy in a separate additive control-table record, not as a new field in the predecessor's strict `FluxVaultConfiguration` JSON. Expose it through the new release's request/status contracts and Options, which explains “Protect automatically while you are signed in”; saved-source consent is explicit. Pausing does not forget that preference. Add default/load/save/runtime-policy tests in the same change. Existing cadence values remain the defaults; expose only meaningful controls with a working runtime effect. Internal batch/retry/session-observation bounds belong in validated defaulted policy models, not unexplained literals or dormant Options fields.

Use a service-owned Windows creator-session provider, reusing the existing caller-context and handle-validation abstractions. Discover a logged-on interactive session, verify the token SID equals the catalogue's immutable creator SID, and identify the token by session ID plus logon authentication ID. Use the ordinary filtered token, never a more privileged linked token. If an ordinary effective token cannot be established, report **Blocked: creator session unavailable**. Do not manufacture authority from a PID, username, JSON claim, service token or stored credentials.

Support an authenticated creator who is active or locked/disconnected but still logged on, subject to successful native validation; sign-out ends authority. Select one eligible session deterministically and retain that session until it ends; do not combine credentials from multiple sessions. Closing WPF changes nothing. Enumerations, watcher handles and file opens use the verified creator context; journal data is only a discovery hint and cannot authorise a read. Recheck session generation, current selection and authority at dispatch/open/publication boundaries, and join old readers before releasing the token or starting a replacement generation.

Creator consent binds a server-computed fingerprint of source selection/exclusion rules and the vault binding, retained transactionally with the extension policy and configuration revision. An explicit creator save/enable approves that source scope. A granted user's change cannot silently widen work executed under the creator's token: invalidate automatic approval for a changed source scope, stop its generation and show **Blocked: creator approval required**. Recheck the fingerprint against the current authoritative base configuration on every startup/dispatch, including after a legacy client save. Existing explicit grants continue to govern manual actions and history access. Background work cannot infer consent from a general ManageProtection grant.

The initial automatic envelope is stable, ordinary saved files on accessible local NTFS paths already supported by the caller access layer. Multiple folders, filters and configured mirrors remain supported. Unsupported/reparse paths, missing roots, denied/locked files and unavailable volumes are visible with a reason and bounded retry/reconciliation. No VSS or application-consistency claim is added. Missing/unavailable roots or access denial must not become file-deletion records; only confirmed file absence under an authoritatively inspected parent may reconcile deletion, retaining historical versions. Verify opened-file identity, length and write time before/after reading and before publication; a detected change stays pending. Retain honest consistency metadata: these checks alone do not upgrade a best-effort live read to an application-consistent snapshot.

Microsoft documents the privileged service/token-closure requirements for [WTSQueryUserToken](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken) and distinguishes logged-on disconnected sessions in [WTS_CONNECTSTATE_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ne-wtsapi32-wts_connectstate_class). These support this proposed service adapter; native security review and tests must prove the actual implementation. A UI-held token would require the desktop to remain running; a new logon helper would add a process/install lifecycle. Neither is necessary if the bounded service adapter passes its gates. Do not silently adopt a different authority model if it fails.

## Durable work and runtime contracts

1. **Input → durable record.** Watcher/journal hints and restart reconciliation enter one admission path in the bound PostgreSQL database. A transaction records pending work or a durable scope-reconciliation marker together with the covered journal cursor. Never advance a cursor because enumeration alone succeeded. One journal reader per volume routes to accepted scopes. Overflow/reset/wrap/rename of directories records reconciliation, retaining both rename identities; unavailable roots retain uncovered work.
2. **Record → dispatch.** Add a bounded work store in the existing control catalogue, with vault/scope/revision, dirty generation, due time, attempt/result, owner generation and stable capture/version identity. Coalesce queued changes; a newer change while running remains pending rather than being overwritten by that completion. An exclusive repository/runtime owner and transactional claims prevent duplicate generations. Keep the existing cadence/backlog limits; saturation becomes a durable reconciliation marker, never silent loss or an unbounded queue. Paused work is retained, not reported captured.
3. **Dispatch → verified publication.** Check current consent, selection, pause and creator session immediately before work. Use the existing authorisation/binding guards and verified repository writer. Reserve the capture identity before source work and bind it to the immutable version, so restart can distinguish an unstarted attempt from a committed capture whose acknowledgement was lost. A source that changes during reading is retryable, not current/protected. Retain the prior good version.
4. **Publication → visible result.** Complete work only against the committed version/reference result. If publication and work completion cannot share a transaction, reconcile by reserved identity and validate its manifest/content before accepting completion; do not infer success from path/mtime or blindly recapture an uncertain attempt. A newer dirty generation remains due. Report queue delay, last successful version, verification scope, mirror warnings and outbox lag separately.
5. **Restart/shutdown.** Fresh runtime generation recovers persisted due work and reconciles interrupted claims before new dispatch. Cancel and join discovery, capture, retry/export and accepted IPC tasks before disposing watchers, tokens, connections or repository handles. First unexpected loop fault cancels/joins siblings and surfaces a failed runtime. Pause/stop confirmation requires quiescence; a host deadline or client cancellation is not proof that writing stopped. No replacement generation overlaps the old one, and no writes occur after acknowledged stop.

The new store is justified by demonstrated memory-only work and early cursor advancement. Reuse PostgreSQL transactions, operation receipts, immutable version validation, repository leases and existing fixtures. Do not generalise this into a job platform. Keep lock order explicit: control admission transaction ends before repository lease acquisition; result reconciliation uses the same ordering, with no transaction waiting for a worker that needs its locks. Manual backup, configuration, retention, purge, mirror work and automatic work share the same effective mutation ordering/binding rules. Prefer serial safety over inventing fine-grained cross-process locks.

Typed status separates **Paused**, **Waiting for changes**, **Pending/Capturing**, **Blocked** (session/source/storage/approval, actionable retry), **Failed** (unexpected fault), **Outcome uncertain** (effects need reconciliation) and **Reconciling** (coverage incomplete). Include reason, last successful version/time, pending scope/count and last observation. Service disconnected is not Paused, and saved configuration is not protected data. Use existing `GetStatus`, history, activity and receipt surfaces; an acknowledged automatic version must be findable after restart.

## Only necessary NEXT-003 prerequisites

Before enabling automatic dispatch, prove: work-to-version identity and commit-loss reconciliation; stable authorised source observation with change detection; lease coverage for the new automatic/manual/restore/retention/purge interleavings; and outbox retries that validate existing export contents before marking exported. Preserve chunk/manifest publication, wrong binding, last healthy copy, bounded decode and destination-preservation tests. Fix only a reproduced or directly exposed gap in these paths.

Use the existing outbox, adding bounded draining and failure visibility; export failure must not erase acknowledged work or claim independent mirror recovery. No new checkpoint format, encryption, full storage redesign or professional capture set is required here. In particular, interrupted deletion/retention must not resurrect removed versions through outbox retry: cover export/delete ordering under the existing repository coordination.

## Implementation sequence and acceptance

### Current interface notes for the first batch

Targeted inspection during NEXT-004's idle measurement confirms that the shipping
`Program`/`Worker` composes only the authenticated pipe runtime. The legacy
`FileSystemProtectionLoop` is not a safe shortcut to automatic operation:
pending changes are memory-only and `UsnCatchUpService.CatchUpAsync` persists
checkpoints before work admission. Retain their existing cadence/journal tests
while replacing only those unsafe admission paths for the new runtime.

`AuthenticatedFluxVaultRequestHandler` holds a single mutation semaphore through
admission and execution and now accepts a shared gate. Wire automatic dispatch
to that existing gate so acknowledged Pause and changed selections cannot race
a source publication. `WindowsFluxVaultCallerContextProvider` already owns a duplicated
effective token and joins its users before disposal; extract/reuse that bounded
ownership implementation for the service session adapter rather than retaining
a pipe caller or adding a helper process. Native ordinary-token verification
remains required.

The reviewed foundation already lets `FileSystemChunkRepository.CommitAsync`
accept a reserved version identity and a pre-publication guard. Wire durable
claims and creator-session validity into those interfaces. Reserved-result
verification validates identity and content; reconciliation must use it rather
than infer success from a filename. The existing 1,000-row outbox drain now
validates prior exports and excludes deleted versions, including older orphaned
work. Wire its repository-coordinated entry point into the supervisor. Reuse
these completed NEXT-003 prerequisite tests; do not rebuild their machinery.

Do not add fields to the predecessor's strict configuration JSON, bypass the
catalogue binding checks or run legacy source enumeration as LocalSystem. The
extension schema, new policy/status contracts and meaningful Options control
ship together after NEXT-004's separate installed acceptance.

| Batch | Affected areas and deliverable | Acceptance through real interfaces |
| --- | --- | --- |
| 1. First automatic round trip | Compose a bounded Windows session provider with the reviewed PostgreSQL work/consent store and existing security/service types; wire `WindowsSingleVaultService`, `Worker`, shared mutation gate, capture/repository identity, Options/status and focused tests. Initially one owned local folder with default serial dispatch; integrate the real discovery event, durable admission and visible result together. | Creator enables → saves file without manual backup → PostgreSQL work/version → history → independent recovery. Close WPF and repeat. Capture ACL denial, wrong creator/binding, default-off upgrade and no privileged fallback. Required authority/identity invariants pass before exposing the path. |
| 2. Restart and accountable coverage | `FileSystemProtectionLoop`, `UsnCatchUpService`, checkpoint reader/store, `ProtectionRuntimeCoordinator`, repository metadata/outbox and supervisor. Preserve cadence tests and replace only unsafe in-memory/early-ack behaviour. | Kill after admission and after publication; restart and recover correct bytes without false completion/duplicate acknowledged version. Newer save during running work survives. Pause/resume; sign-out and creator re-entry; native overflow, rename, missing/reappearing root, journal reset/wrap; controlled source change/lock, DB/mirror failure and retry without another save. Prove no post-stop writes, leaked handles or overlapping generations. |
| 3. Combined, responsive and installed | Existing WPF views/view-model status, native/process/PG fixtures, performance observations, candidate/update tooling, help and tracker. | End-to-end **save → automatic capture → FluxVault restart → find version → independently verify recovery** on the installed staging candidate. Also exercise a pending save across restart, grants/denial, retained earlier test versions, overlapping maintenance/restore and joined teardown. Independent final candidate review and zero-warning build required. |

**Effort checkpoint:** after two substantive batches, the private real-service/DB/UI round trip and restart result must exist. If not, identify the exact authority/durability blocker and reduce the affected path, without spending another batch on broad fixture conversion or UI scaffolding. Do not enable an unsafe shortcut to produce a demonstration.

**11 October execution refinement:** the first real-service/Options/DB round trip
and native discovery/owner-interruption barriers pass on IPv4 and IPv6; see the
existing verification ledger. On this host `EnableLUA=0`. Independent Astra review
accepts a native LUA-filtered creator token only after verifying exact WTS
SID/session/AuthenticationId, no elevation or UI access, at most Medium integrity,
only traverse privilege and preservation of native denial groups. No host UAC
change or elevated source fallback is permitted.

The next bounded storage change uses extension version 2 in the same five owned
tables, with transactional v1 migration and unchanged base configuration/schema.
Persist first/latest event, last attempt, queued observation, claimed deletion
intent/lineage and the existing USN checkpoint JSON. Reuse the cadence scheduler:
quiet/hot deadlines survive restart, the minimum same-file interval takes
precedence, and duplicate observations neither postpone work nor invent new
dirty generations. A newer observation during capture remains pending. An
unchanged observation skips completed work only after its retained version is
verified under the repository lease; purged history cannot suppress recapture.

Use one native journal read per volume under creator authority. Atomically mark
all accepted scopes on an advanced/reset volume dirty with its cursor, including
records the reader cannot resolve to a current path. An unavailable journal uses
bounded full reconciliation without privileged fallback. Full-scan saturation
retains uncovered scope state; observation deduplication allows subsequent passes
to progress. Missing roots and denied paths remain unresolved. Only confirmed
file absence may claim a deletion; persist its exact intent/lineage and validate
them when reconciling its reserved manifest. Recheck absence and authority before
publication. A reappearing file remains capture-pending, and a no-op deletion
never invents a successful reserved result. Independent integrity review accepts
this refinement subject to its focused real-store and native acceptance checks.

The 11 October staging decision removes the previous environment acquisition
block. The two foundation review findings are closed at `1b2caff`; resume from
that checkpoint without repeating unchanged verification. The next batch must
join the real session adapter, supervisor, discovery admission and existing
capture/work interfaces into the first observable staging round trip. The
[verification ledger](../../verification/2026-10-08-next005-automatic/README.md#native-authority-gate)
keeps the narrower native sign-out/re-entry acceptance item open. It is not a VM
dependency or a reason to stop all implementation.

Extend existing `ProtectionLoopUsnTests`, cadence/checkpoint tests, native caller source tests, service lifetime tests, repository process/PostgreSQL integrity cases and actual view-model contracts. Add focused real-store tests for each distinct new durability, consent, concurrency and migration invariant. Use barriers/crash points at admission, publication, completion and shutdown; component fakes alone cannot close these gates. The existing resource-intent journal, isolated authenticated PostgreSQL fixtures and native creator/ungranted-user probes remain the tooling boundary. Join every temporary process; do not ask the owner to perform validation.

Use this concrete staging verification sequence:

1. Exercise real WTS acquisition and ordinary-token SID, session ID and
   AuthenticationId validation against the current signed-in creator, then read
   owned disposable sources through the existing caller access layer.
2. Use deterministic authority-loss and replacement barriers to prove refusal
   at admission/open/publication, retained pending work, joined readers before
   token disposal and stale-generation rejection. Record these as injected
   transitions, separately from actual Windows sign-out evidence.
3. Use the existing owned PostgreSQL/process fixtures and scoped FluxVault
   restarts for durable admission/publication interruption, takeover,
   reconciliation, migration/rollback and the real save-to-recovery workflow.
4. Retain actual native sign-out/re-entry as a specific candidate acceptance
   check. A separately owned staging session can be used if available; the
   current session plus injected transitions does not establish that result.
   If testing would require signing out the operator or rebooting Windows,
   identify the concrete disruption and obtain that separate authority only
   then. Do not infer it from disposable FluxVault data, request a VM instead,
   or claim the unresolved check passed.

The remaining native check uses a bounded extension of the existing owned
Windows/PostgreSQL fixture, independently reviewed on 11 October. A scheduled
SYSTEM supervisor owns its child jobs independently of the desktop launcher and
pins the real creator SID, protected payload and installed product assemblies.
`AbortRehearsal` verifies held-reader cancellation/join, retained pending work,
absent reserved publication and joined cleanup without a Windows transition.
`ObserveSignOut` requires real WTS identity loss and runtime cancellation before
fixture cancellation, separate stale-probe refusal, joined old readers, a fresh
AuthenticationId and capture/recovery without another source write. Observation
has a finite deadline plus cleanup grace. Preparation and rehearsal do not
authorise operator logoff and cannot satisfy native sign-out acceptance.

The implemented headless rehearsal now passes 16 native checks, including
separate held readers, pending/uncertain preservation and absent reserved
publication. Its worker, task, child jobs and private fixture are joined/removed;
installed FluxVault and normal PostgreSQL are unchanged. The existing ledger
retains the permission-boundary corrections, 24 focused trust checks and
374 ordinary Integration checks. Actual operation
`f0d1d52eb0064d1fb9cda2913f76be89` is pinned and prepared without a task or worker,
with a 600-second observation allowance plus finite cleanup grace. The
[prepared check record](../../verification/2026-10-08-next005-automatic/README.md#prepared-native-session-check-11-october)
identifies its exact request, payload, installed assemblies and required native
proofs. Only explicit operator-session-disruption consent can authorise starting
that check and signing out the operator; the desktop-control restriction remains
in force.

## Migration, release and recovery

**11 October mirror refinement:** retain a constant-size interval on each
existing automatic work row, ordered by the existing metadata-outbox ordinal.
Successful primary completion extends the interval atomically, preserving its
oldest unfinished position. Newer primary captures remain eligible. Retry only
retained file manifests for the exact path/scope, verify the primary graph and
chunks, and advance conditionally under the existing repository lease/fence.
The PostgreSQL path copies chunks only; independent metadata recovery remains
NEXT-006. Export acknowledgement cannot clear mirror intent. Purged versions
retire only after authoritative absence, with no resurrection. Unavailable or
corrupt mirrors retain a reason and bounded retry; Pause/automatic-off stop
writes. Consent/source changes do not discard existing mirror obligations.
Strictly pin primary/state. For automatic operation and healthy manual recovery,
omit inaccessible mirror roots and report them; never access an unpinned root.

Ship separately after NEXT-004 acceptance, with its own source commit/version/candidate manifest, independent architecture/concurrency/migration review and pre-deployment candidate/rollback review. Add versioned, transactional, idempotent extension tables in the installed vault's existing database. Preserve the predecessor-readable base configuration shape and control identity schema version 1: `PostgreSqlVaultCatalogue` currently uses `JsonUnmappedMemberHandling.Disallow` and pins that identity version. New optional fields alone are not backwards compatibility. Give the automatic-policy/consent/work extension its own version record; unsupported extension versions block automatic operation without changing the base binding or preventing manual recovery. Leave unrelated databases and normal PostgreSQL service untouched.

Verify all five extension relations are permanent, ordinary tables owned only by
the service role before evaluating the extension identity record. General outbox
draining must also reconcile pre-existing pending version exports whose version
has already been deleted. Retire obsolete work and select live work in a bounded,
short metadata transaction under the existing mutation lock; hold the repository
lease before that transaction and through export/acknowledgement. Preserve
unrelated pending records and already exported journal files. These are the two
bounded foundation corrections from independent integrity review, not a new
migration framework or portable-recovery contract.

Keep automatic protection off through migration and until explicit creator consent. Preserve pending work and source-approval records across compatible updates; restart reclaims work only after owning/reconciling its generation. Before rollback, disable/quiesce the new runtime and transactionally mark the extension as requiring renewed creator approval, retaining the saved preference and all work/history/receipts. Then restore the immediately previous accepted NEXT-004 release. Its base configuration reads/saves must succeed while leaving extension records untouched. On re-upgrade, retain the queue but dispatch nothing until fresh creator approval of the current scope; old work outside that scope is reconciled as superseded, never captured under stale consent. A scope-fingerprint mismatch also blocks dispatch independently of the rollback marker.

Rehearse actual forward → rollback → old-binary read/save (including changed source scope) → re-upgrade → fresh consent in an owned fixture. Assert preserved policy/work/history/receipts, readable verified recovery throughout, no capture before renewed approval, correct exclusion of obsolete queued scope and no duplicate completion. Test interrupted migration and interrupted rollback-marker publication; do not downgrade before durable quiescence/invalidation is established. Never drop the queue or restore a stale DB over later versions. Retain packages outside the task worktree and clearly report the rollback release's manual-only operation.

Use the NEXT-004 candidate/rollback procedure and existing standing authority; no repeated routine staging rollout approval is needed. Apply the disposable-data scope above and preserve unrelated resources. Installed validation uses this staging PC, owned synthetic data and scoped FluxVault restarts. An operator sign-out or Windows reboot is a separate host disruption; neither is assumed here. Keep the actual native sign-out/session-replacement acceptance result explicit alongside the current-session and injected-transition results. No additional machine is required by the plan, and the removed environment approval does not block implementation. Completion still requires applicable candidate, recovery, authority and live-validation gates; do not ask the owner to perform tests or claim broader support from incomplete evidence.

## Following priorities and bounded UI exploration

**NEXT-006 remains next:** independently retained metadata and content, consistent retained checkpoints versus GC, local recovery tool, fresh-environment recovery without original source/primary/DB, and repeatable replay after retention. The 1.0.7 manual restore and this slice's restart are not disaster-recovery evidence.

**NEXT-007 design only alongside these slices:** reuse [existing concepts and state contracts](../../ui-concepts/2026-10-03-next002/README.md); their multi-vault selectors are retired. Explore one bounded single-vault Overview → Protect → Recover flow, without implementing a shell rewrite here. Overview shows observed protection/coverage and last verified recovery; Protect shows draft versus saved scope, Pause/Resume and Stop keeping history; Recover exposes retained removed-scope history, verified restore results and separate deletion. Compare a compact overview with a guided first-protection entry using the same real command/state matrix. Validate the chosen direction against captured current WPF screens during implementation, including unknown/offline/blocked states. No theme/accessibility work is resumed.

**NEXT-009 measurements apply now:** retain its proposed warm shell 2 s, ordinary feedback/navigation 100 ms, first indexed history page 500 ms p95, no UI task over 100 ms, cancellation acknowledgement 1 s and idle CPU below 1% over ten minutes as stated workstation targets, not measured guarantees. Record real service/DB/UI timings, queue versus capture latency, source bytes read, memory/handles and foreground file-save interference with cold/warm trials. Report hardware/corpus/sample size and regressions; do not extrapolate synthetic results to professional workloads or invent a throughput target. Ratify workload-specific limits from observed baseline before a broader support claim.

**NEXT-010 retains VSS/professional consistency:** linked capture sets, writer/application consistency, Windows fidelity and real Office/CAD/Adobe reopen validation. Logged-off and reboot requirements remain open within NEXT-005 until safe source authority and recovery behaviour are implemented and verified; a persisted queue alone does not satisfy them. This plan neither removes those requirements nor expands the programme.
