# NEXT-005 signed-in automatic protection execution

The owner resumed the [agreed plan](../../superpowers/plans/2026-10-08-next005-signed-in-automatic-protection.md).
NEXT-004 was independently installed/live-accepted as 1.0.9 and isolated in
[PR #35](https://github.com/yagasoft/FluxVault/pull/35), squash-merged at
`41222b625b772e53f02972f76674440713d3884b`. Fresh hosted CI
[37796903281](https://github.com/yagasoft/FluxVault/actions/runs/37796903281)
passed at exact reviewed head `ef60231ead15a9d2141fa80753a816b86577db42`.
The test-only hosted-fixture correction has separate independent Astra/high
approval; it did not reopen unchanged installed/product gates. Work reuses
the same dedicated task worktree on `codex/next005-signed-in-protection`.

## Current verification, 11 October

### Corrective 1.0.11 installed workflow

Candidate `de279a32e6fc4d7b9b86e02150bd98cd`, source
`ee7ca8d9901884d4f4993a982f1cf4793d09445c`, is installed on this staging PC.
Its manifest SHA-256 is
`35B669189863F0CE265BE622383B3749DB00BCBD6528A749680109125115EECA`;
`installed-1.0.11/candidate-retained.json` pins both retained installers and all
five joined successful build processes. Independent Astra/high approved the
exact 1.0.9→1.0.11 update and exercised 1.0.9 fallback. The update and subsequent
joined FluxVault-only restart verified all 208 payload hashes and preserved
PostgreSQL postmaster 13748, bootstrap, authentication and storage ACLs.

Fresh exact-source hosted CI [38147096910](https://github.com/yagasoft/FluxVault/actions/runs/38147096910)
passed. The installed real-pipe workflow in `installed-1.0.11` passes:

- WPF closed: save a random 1 MiB file; capture automatically without manual backup.
- Save changed bytes, retain the observation pending, acknowledge Pause with
  immediate Paused status, then join a service restart.
- Find and independently SHA-256 verify the first version, Resume, capture the
  pending save without another write, and independently verify its recovery.
- Displayed dashboard/Options show Paused correctly; resumed dashboard shows
  Waiting for changes. Repository displays both versions and permits recovery.
  Screenshots and associated control text are retained in the same directory.

The first source write took **0.799 ms**, automatic publication **6.628 s**, Pause
acknowledgement **55.99 ms**, and twenty history requests **11.66–40.42 ms**.
Two fresh observer processes using all **63 hash-matched installed App files**
rendered in **0.825–0.869 s**; their longest dispatcher operations were
**450–466 ms**, still above NEXT-009's proposed 100 ms limit. Both processes exited
0 and were joined. Cache state was uncontrolled; these synthetic observations
do not establish cold-cache or professional-workload performance.

The automatic-enabled idle observation ran **601.022 s** with no FluxVault input
or Sky captures: **0.02307% App / 0.03141% service machine CPU** over 24 logical
processors. App working set changed 242.9→237.0 MB and handles 616→641; service
working set 148.1→163.7 MB and handles 536→554. Process IO and shared PostgreSQL
cohort snapshots are retained; shared-cluster activity is not attributable solely
to FluxVault. This single uncontrolled sample is not a leak, regression or full
NEXT-009 certification. Startup dispatcher work remains above its proposed limit.

The actual Options view-model restored automatic-off and the complete original
four-scope configuration, checked for exact JSON equality. All **22 earlier
history entries** remain, including the prior 1.0.10 trial; both new owned file
versions and their folder snapshots are retained. The final read-only check passes
the exact registration and 208 payload hashes with unchanged PostgreSQL lifetime,
bootstrap, authentication and ACLs. The owned installed desktop was terminated
after restoration and joined (exit -1); tray Exit was not exposed by the supported
Computer Use window inventory. This is not claimed as graceful tray-exit proof.
All temporary processes are joined. The two joined startup observers' diagnostic
folders and copied runtime are deliberately retained after automatic approval
review rejected removal with the generic reason “blocked by policy”; there was
no deletion or retry through another mechanism.

Final independent GPT-6-Astra/high review (`review_protection_design`, 11 October)
accepts the bounded signed-in branch/live workflow with
no additional product blocker. **Full candidate/slice closeout remains blocked by
actual native Windows sign-out/re-entry.** Current-session and injected authority
transitions do not close that gate. [PR #36](https://github.com/yagasoft/FluxVault/pull/36)
remains draft while it is unresolved.

### Prior checkpoints and corrective regression evidence

The supervised signed-in runtime, native creator adapter, durable v2 work/cadence
and volume reconciliation, Options preference/status and mirror retry are
implemented. **The first 1.0.10 staging installation and automatic recovery passed;
its live status defect was corrected and live-accepted in 1.0.11 above.** Earlier prerequisite
sections below are historical checkpoints, superseded within their checked scope
by these results. Independent Astra/high review accepts the current product code
gate without another requirement-backed blocker.

- Combined suites: **608 Core, 514 App, 173 Windows and 348 Integration tests pass,
  zero skipped**. The corrective App/Windows/Integration receipts are
  `green/app-fixture-complete.trx`, `green/windows-final-corrective.trx` and
  `green/integration-corrective-combined.trx`.
  `green/*-combined*.trx` retain final results and the initial two App failures
  (missing tooltips and a tab-selector assumption), corrected before the green run.
  `final-build.log` confirms a zero-warning/error solution build.
- `native-pause`: 39 native checks per endpoint, including a held native capture
  whose cancellation/reader join precedes Pause acknowledgement and whose saved
  bytes reach history after Resume without another save.
- `native-mirrors-green`: 45 native checks per endpoint. Two successive primary
  captures continue while a configured mirror is offline; restart preserves both
  and their lag, manual primary recovery matches SHA-256, and the restored mirror
  receives both retained versions' verified bytes without another save.
  The preceding `native-mirrors` failure was the fixture metadata reader omitting
  the required `pg_catalog` search path; product security correctly refused it.
- `store-mirrors`: **227 catalogue, 134 metadata and 10 repository checks on each
  IPv4/IPv6**. Includes v1→v2 migration/failed migration rollback, durable cadence,
  newer work, volume dirty/checkpoint atomicity and mirror interval extension,
  delayed retry, export independence and purge/no-resurrection.
- `native-rollback`: **56 native checks per endpoint**. The actual frozen 1.0.9
  service/pipe reads the forward store, independently recovers retained bytes and
  saves a changed source scope. Interrupted rollback-marker publication leaves
  records unchanged; a committed marker preserves the enabled preference and
  held reserved identity. Old binaries leave extension records unchanged.
  Re-upgrade captures nothing before fresh creator approval, then captures only
  the current scope without another save or duplicate completion and independently
  recovers old/new bytes. The frozen predecessor's 63 files match its accepted
  manifest (`rollback-predecessor-copy.json`).

All completed owned fixtures above exited 0 and were joined, with root removed,
owned jobs joined and installation unchanged. `native-final-green` passes **61
checks on each IPv4/IPv6**, including explicit original-receipt preservation,
default-off offline-mirror visibility and execution of the exact operational
rollback SQL. A wholly absent first-start extension is safe to downgrade without
creating consent; partial/untrusted extensions remain refused; v2 invalidation
preserves durable work. This closes the independent operational review finding
about migration failure before extension creation. `native-final` retains the
failed test assumption that a warning contains a path; warnings correctly
identify the configured mirror by label. Native USN volume access is denied to the ordinary creator
on this host, so the runtime truthfully uses durable bounded reconciliation.
No native USN-access pass is claimed.

`installed-before.json` freshly observes the original installed vault/revision 14
and four saved scopes. `installed-baseline.json` verifies all 208 accepted 1.0.9
payload hashes, unchanged bootstrap/HBA/ident/storage ACLs, zero grants and zero
pending history deletion using a joined read-only SYSTEM/psql job. Windows rebooted
on 10 October: the old sealed postmaster PID is correctly refused. The bounded
operator wrapper verifies current service/data/port/parent ancestry and records
postmaster 13748, started `2026-10-10T08:12:14.0315058Z`; it does not restart
PostgreSQL or rewrite old seals. Initial wrapper checks caught forward-slash path
spelling and dot-sourced parameter shadowing before any installation effects.

The frozen 1.0.10 candidate is `ed7e1a6bf10b4b5a9c10a40c65a2c8e3`, source
`b584398161959d5b4311eceaad551d126c5b7d40`. Independent Astra/high accepted its
concrete deployment and retained 1.0.9 fallback. `candidate-retained.json` and
`candidate-readonly-check.json` retain the exact package/manifest pins. The joined
update verified all 208 payload hashes and bootstrap/authentication/ACL/process
preservation. `installed-1.0.10` records the real installed workflow: desktop
closed, 1 MiB save, automatic capture in 3.971 s, newer pending save, Pause in
64.50 ms, joined FluxVault service restart, history and independently equal SHA-256
recovery of both versions without another save. The source write took 0.814 ms;
twenty history reads took 13.87–43.10 ms. These are one synthetic trial, not a
professional workload or full performance pass.

The immediate post-Pause status still reported the preceding runtime cycle's
Pending state despite saved `IsEnabled=false`. Capture had joined; the dashboard's
saved pause display was correct, but this service/Options inconsistency prevents
acceptance. Focused regressions reproduce the stale Pause/off/approval and hidden
Failed/OutcomeUncertain states. The correction reads one runtime snapshot,
projects committed controls immediately, preserves failed/uncertain diagnostics,
and reports Reconciling until resumed authority is observed. A revision mismatch
between admission and the automatic-state read refuses mixed control projection
and preference changes, while preserving receipt revision/configuration.
Independent Astra/high approves this bounded approach.

The held native capture regression additionally reproduces a rapid Resume reusing
stale Capturing/Pending after its source readers have joined. The runtime now
invalidates that cached activity under the existing mutation gate while source
suspension remains held. `native-rapid-red` retains the precise failure;
`native-rapid-green` passes **63 checks on each IPv4/IPv6**, including immediate
Paused and resumed Reconciling status while dispatch is held, then capture without
another save. Both runs joined, removed their roots and preserved installation.
Independent Astra/high approves the correction and regression. The final source
build (`frozen-source-build.log`) has zero warnings/errors.

The complete original four-scope configuration/default-off policy and all eighteen
prior versions were restored, retaining the two owned automatic versions.
Independent review approved exercising the exact 1.0.10→1.0.9 rollback. Its joined
transition, durable approval invalidation and 208 fallback payload hashes passed;
`predecessor-verified.json` and both `recovery-rollback-*.json` prove actual old-service
pipe/history/configuration and independent recovery of the new versions. PostgreSQL
postmaster 13748 and bootstrap/authentication/ACLs remained unchanged. Corrective
1.0.11 subsequently passed its own frozen candidate gate, displayed WPF and
measurements in the current verification section above.

Hosted run `38144602250` failed four fixture cases: the new Options tab displaced
an ordinal selector; a test enumerated a collection while refresh mutated it;
and a supposedly single watcher burst raced the loop's consumption. Repairs select
Advanced by header, await collection notification and join reads, and hold the
existing journal-reader fixture until all burst hints are admitted. No assertions
or product scheduling policy are weakened. Seven focused Integration checks and
all 514 App/173 Windows checks pass. The first App fixture repair used the wrong
event item type and timed out; that diagnostic result is retained, then corrected
to `VersionRow`. All 348 ordinary Integration checks passed; fresh hosted CI
for the corrective commit subsequently passed as recorded above.

Native sign-out/re-entry and NEXT-009's existing baseline misses remain explicitly
open; no broader readiness claim is made.

`integrity-final` (fixture `e9c659f927e84a86b833501b98132156`) freshly passes all
**40 real PostgreSQL integrity cases, zero skipped**, exits 0 and is joined.
Failure/location are null; root removal, owned-job joining and installation
preservation are all true. Independent Astra/high review confirms the absent
extension correction and the 61-check native result, and finds no blocker in the
bounded installed acceptance helper. Concrete candidate approval remains separate.

`green/rollout-tooling-green.trx` passes all 18 affected existing joined-service,
rollback-context and cleanup checks. `update-automatic.ps1` reuses these helpers
and the guarded MSI/service/job procedure for the exact 1.0.9→1.0.10 candidate.
Rollback requires a joined stopped/manual service, the transactionally obtained
automatic owner lock and the durable approval marker, or verified total extension
absence. It never drops history/queue or restarts normal PostgreSQL. The owned
installed acceptance temporarily selects only its synthetic source, uses the
existing cadence model to hold a newer observation across Pause/restart, then
restores the complete original saved configuration and default-off preference.

## First automatic round trip

The private actual-service round trip passes on both IPv4 and IPv6 in
`native-first-capture` (fixture `4cf9cdb48fd54301a67c55273ce102d4`): ten checks
per endpoint cover real ordinary WTS authority, actual Options view-model enable,
save without manual backup, automatic publication, joined restart, authenticated
history and recovery matching an independent SHA-256 check. This satisfies the
agreed effort checkpoint. The preceding failure was the fixture's protected
parent lacking creator traversal; the correction grants only Traverse and
Synchronize there, preserving private state/repository ACLs and product source
checks. `native-first`, `native-roundtrip`, `native-roundtrip-2` and
`native-diagnostic` retain failed attempts and joined cleanup.

`native-interruption` (fixture `c2433c518f7b46bfac732378b71acb77`) passes twenty
checks on each endpoint. A held real native directory reader proves Pause cancels
and joins discovery before acknowledgement. Resume and original receipt replay
preserve a healthy owner. Terminating only the private fixture's dedicated owner
backend cancels a held capture independently, joins it before replacement and
retains the unresolved reserved identity. Replacement verifies absence and
captures the pending save without another write. Both fixture processes exit 0
and are joined; root removal, owned-job joining and installation preservation are
all true. These are private compositions, not installed acceptance or native
Windows sign-out evidence.
At this early checkpoint no NEXT-005 capability was deployed or claimed. The
first batch had to join creator-session authority, additive default-off consent/work persistence,
real discovery admission, reserved capture identity, verified publication,
Options/status and independently verified recovery. Existing mutation ordering,
source access, repository leases and native/database fixtures are the boundaries.
No logged-off/reboot, disaster-recovery, VSS or application-consistency claim is
added. Normal PostgreSQL and unrelated data/changes remain untouched.

11 October implementation checks: actual Options and existing save-record tests
pass 39 cases (`green/options-automatic-green.trx`); creator generation/session
joining and supervised sibling cancellation pass five
(`green/supervision-session-green-2.trx`). Source-suspension barriers pass three
(`green/source-quiescence-green.trx`) after the recorded red run. These component
checks do not close native composition/interruption gates. The initial supervision
green attempt contains a failure and remains retained as such.

The host has `EnableLUA=0`. A native Windows LUA derivative, with maximum privilege
restriction and explicit lowering to at most Medium integrity, passed actual WTS
SID/session/LUID, non-elevation/UI-access, privilege and native denial-group
postconditions. `session-7c27d3aeb24441484a602d496139825c.json` records creator read
and refusal of SYSTEM-only, administrator-only and explicitly denied sources.
Independent Astra review accepts this bounded refinement before exposure. No UAC
policy or host session was changed. Earlier refused probes retain their reasons.
The fixture SYSTEM owner restored its test ACLs to correct an unsuccessful
elevated cleanup; the corresponding cleanup record proves joined job, removed
task and removed root. No elevated source token is admitted.

Independent composition review required preservation of manual recovery after
extension/reconciliation failure, source quiescence before consent mutations,
owner-loss observation and pre-publication fencing. Follow-up findings required
effect-free receipt replay, non-poisoning routine cancellation and mutation fencing
for uncertainty created during capture. Real enumeration and owner-backend
interruption now pass as above; manual-recovery fallback and corrupt-claim proofs
remain open alongside the bounded cadence/coverage batch.

## Native authority gate

**Current decision, 11 October 2026:** the owner removed VMs from the design/plan
and confirmed that FluxVault is in staging with disposable data. Use this Windows
PC, the real installed service/WPF/PostgreSQL interfaces and existing owned
fixtures. There is no pending VM approval, image download, hypervisor setup or
second-machine requirement. The earlier blanket implementation block is removed.
Disposable FluxVault staging data and explicitly owned test sources/outputs may
be reset when needed; preserve unrelated data, accounts, repositories and working
changes. Normal PostgreSQL still must never restart. Preserve expected recovery
bytes and results until the relevant test assertions have been resolved.

Independent GPT-6-Astra/high review (`review_protection_design`, 11 October)
accepts the revised route, retaining the completed foundation acceptance:

- Use real WTS acquisition and ordinary creator-token SID/session/AuthenticationId
  checks against the current signed-in session, with disposable source data.
- Use deterministic authority-loss/replacement barriers for stopped
  admission/publication, retained pending work, joined readers and stale-generation
  rejection. Label these as injected transitions, not actual Windows sign-out.
- Reuse owned database/process fixtures for interruption, takeover,
  reconciliation and migration/rollback. Retain independent candidate review,
  checked rollback and joined cleanup.
- Keep actual native sign-out/re-entry as an open candidate acceptance item.
  The current-session checks and injected transitions alone do not close it.
  Disposable data does not authorise operator sign-out or a Windows reboot;
  any such host disruption needs a concrete, separately authorised operation.
  It is not an environment acquisition prerequisite for implementation.

The native acceptance criterion remains: retain the service's duplicated token
across the owned session's sign-out, prove invalidation stops source access and
publication while work remains pending, and join readers before token disposal.
Re-entry has a new AuthenticationId and starts after the old generation joins,
including when the numeric session ID is reused. This check has not been run or
passed by this planning update, and no automatic capability is declared complete.

11 October final installed checkpoint: `quser` returns only the operator's active
console session 1; no separately owned interactive staging session is available.
Before any operator logoff, prepare and independently review a bounded SYSTEM
observer using the existing owned fixture/job interfaces. Its lifetime must
survive the operator desktop and its launcher. Retain the real creator token and
a held source reader, observe actual WTS disappearance, prove cancellation and
join before token retirement, refuse old-token publication while pending work
remains, then reacquire a different AuthenticationId after re-entry and capture
without another save. Include a finite timeout, abort rehearsal, durable ownership
record and joined teardown; normal PostgreSQL and unrelated resources remain
untouched. The headless rehearsal below has passed; actual logoff has not been
executed. Signing out session 1
would close the operator's Windows apps and require normal Windows sign-in again;
that host disruption is outside the existing automatic rollout approval.

### Prepared native session check, 11 October

`eng/test-automatic-session-transition.ps1` extends the existing fixture with
Prepare, Rehearse, Start, Worker and Collect operations. It pins the real creator
SID, 98 protected payload files and four exact installed product assemblies.
The scheduled SYSTEM worker independently owns the existing harness/job tree,
records its native process identity and removes only its unchanged task. Its
finite primary/recovery/join/finalisation bounds reserve 480 seconds beyond the
observation allowance; the actual observation allowance is 600 seconds. An abort
marker requests joined cleanup without terminating the supervisor.

The independently reviewed `AbortRehearsal` run
`574fe1cfb8ec4ce0832467cc4c42b9c5` passes **16 native checks** on the bounded IPv4
endpoint (`native-session-rehearsal/fixture/result.json`). It performs the real
ordinary-creator save → automatic capture → restart → independent recovery
round trip, then holds separate runtime and probe readers across fixture
cancellation. SYSTEM inspection proves one pending and one uncertain work item
and absence of the reserved publication. The event record contains Ready and
AbortJoined; `NativeSignOutVerified=false` and `AbortRehearsal=true` are explicit.
The collector confirms worker exit/join; task removal, private fixture root
removal, owned-job joining and installed service/file preservation all pass.
The retained observer root contains the protected payload and additional raw
logs; compact receipts are retained under `native-session-rehearsal` here.

The first rehearsal refused an older desktop-owned fixture parent before
dispatch. Its worker exited; operator cleanup compared the full saved task
digest before removing that unchanged task (`native-session-rehearsal-failed`).
Independent review approved changing only the two existing fixture parents to
SYSTEM ownership and removing the direct-admin preparer's redundant explicit
FullControl grant. Both originals were saved before mutation; known immediate
children had to be inheritance-protected and NEXT002 empty. SYSTEM/Admin rights,
Users Traverse/Synchronise, unrelated ACEs, group and audit data are preserved.
Windows added only its DACL inheritance-processing marker, including to an
otherwise unchanged protected child. The second preparation stopped on that
observed normalisation before starting any task or child fixture
(`native-session-parent-marker`). The corrected verifier permits equality or
addition of that one marker, refusing its removal and every other descriptor
difference; actual before/after descriptors remain available. This follows the
separate [Windows descriptor control flags](https://learn.microsoft.com/en-us/windows/win32/secauthz/security-descriptor-control).
The trust helper also accepts a redundant already-trusted SYSTEM owner without
adding an identity; unrelated owners still require direct admin membership.

Test-first regression receipts retain these corrections. The final focused
trust run passes **24 cases**, and `green/session-observer-combined-final.trx`
passes **374 ordinary Integration cases, zero skipped**, using the hosted CI
filter that excludes the separately required PostgreSQL/prepared-installation
gates. An earlier unfiltered invocation refused all 40 PostgreSQL cases because
no owned cluster was supplied; this runner mistake is retained as
`red/session-observer-unfiltered-run.trx`, not reported as a product regression
or a passing run. The earlier 40-case native integrity and 63-per-endpoint runtime
results remain valid within their unchanged product scope. No `src` product
code changed for this observer and no identical-product redeployment was needed.

The concrete actual-transition operation is **prepared, not started**:
`f0d1d52eb0064d1fb9cda2913f76be89`, request SHA-256
`E0D5762315B0BEBE10B936ACF6C79C712A5CC469AB0B7D62E87F2AA01D555150`.
Its protected root is
`C:\ProgramData\FluxVault.Tests\NEXT005Session\f0d1d52eb0064d1fb9cda2913f76be89`.
`native-session-prepared/prepared.json` confirms no task, worker record or child
fixture exists. Start must recheck the protected request/payload and installed
assembly hashes. Actual sign-out must be separately authorised and must wait
for its durable Ready record. The observer then requires real WTS identity loss,
runtime-originated capture cancellation before fixture cancellation, stale-token
refusal, joined old readers, a different AuthenticationId after re-entry, and
capture plus independently verified recovery without another source write.
Collect checks worker exit and every owned teardown result. Preparation or an
injected abort cannot close this gate.

Independent GPT-6-Astra/high assessment (`review_protection_design`, 11 October)
verified the rehearsal/cleanup evidence, both final test counts, all 98 prepared
payload hashes, four installed product pins and absence of a started task/worker.
It finds no preparation blocker and accepts readiness to request concrete
operator-session-disruption consent. It does not authorise that disruption or
close actual sign-out/re-entry acceptance.

`installed-1.0.11/post-observer-installed-check.json` freshly confirms exact
registration/payloads and unchanged bootstrap/authentication/storage ACLs, with
PostgreSQL postmaster 13748 still started at `2026-10-10T08:12:14.0315058Z`.
The immediately accepted 1.0.9 fallback is retained and hash-checked; its exercised
manual-only rollback evidence is reused. The operator's desktop remains active;
no mouse/keyboard/UI automation, sign-out or reboot was used for this preparation.

**Superseded context, 8 October:** the A/B token fixture did not provide an owned
interactive WTS session; its tokens and injected transitions could not prove
actual sign-out/re-entry. `authority-environment-inventory.json` retains the
historical host observation. The subsequent VM proposal and pending environment
question are superseded by the owner's 11 October decision and are not work items.
The [WTS token contract](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken)
still governs actual token acquisition and closure.

## Unexposed durable catalogue and export prerequisites

The additive extension keeps base identity schema 1 and predecessor-readable
configuration JSON unchanged. It defaults automatic preference off, records
explicit creator consent with the authoritative saved source fingerprint, and
invalidates approval for a granted user's source change. Original receipt replay
does not renew consent. A dedicated non-pooled PostgreSQL owner connection never
reconnects itself; replacement drains the existing repository lease before
publishing a durable generation fence. Catalogue mutations and new repository
leases verify the activated generation. Dispatch reserves an immutable version
identity, newer dirty generations survive earlier completion, interrupted claims
retain their original identities, and obsolete scopes are retained as superseded.
These are unexposed primitives; the supervised runtime, WTS adapter, startup
reconciliation barrier, durable journal coverage and Options remain unimplemented.

Real SSPI fixture `82b0d9224bd747b28db055d3ce09be8f` (`store-first`) exits 0 and
is joined: **205 catalogue checks on each of IPv4/IPv6**, including 21 new
automatic-store checks. It kills only its own dedicated PostgreSQL owner session,
proves replacement waits for the prior repository lease, and verifies retained
claim identity, newer save/scan preservation, consent invalidation and no renewal
on receipt replay. All three cleanup/preservation predicates are true. These are
real-store tests with explicit policy caller doubles, not native source-authority
or automatic capture acceptance.

`red/policy-red.trx` observes the absent automatic command policy before its
implementation. An intermediate shape test incorrectly matched the existing
maintenance `RunAutomatically` field; `red/policy-shape-assertion-error.trx`
retains that failure. The corrected assertion checks the actual base root
properties rather than rejecting unrelated pre-existing settings.
`green/automatic-policy-selection-outbox-green.trx` passes 22 affected cases.
`green/runtime-fence-outbox-green.trx` passes five focused cases, proving the
fence runs while holding the OS repository lease and rejection releases it,
and distinguishing absent reserved identity from corrupt published content.
`green/core-foundation-full.trx` passes all **592 Core tests, zero skipped**.
All green test processes exit 0 and are joined; TestHost builds with zero warnings/errors.

`red/outbox-existing-red.trx` reproduces a mismatched existing journal filename
being accepted. Existing exports are now compared to the exact durable UTF-8
payload with a bounded buffer before acknowledgement; a mismatch is preserved
and remains pending. New exports use CreateNew, flush and atomic rename. Bounded
draining uses the existing repository lease/generation fence; this is not
independent disaster recovery. Real SSPI fixture
`6f4c0293193e4872b8491ad7d34ba9bb` (`store-outbox`) exits 0 and is joined:
**205 catalogue, 128 metadata and 10 repository checks on each IPv4/IPv6**.
It proves a mismatched existing export remains durably pending and unchanged,
then explicitly repairs only its synthetic test corruption and drains without
another capture. Its deletion occurs after export and does not prove pending
export retirement. All three cleanup and
installation-preservation predicates are true. No normal PostgreSQL restart or
NEXT-005 deployment occurred.

Independent Astra/high foundation review requires permanent storage for all five
automatic extension tables and relation kind/owner/ACL verification before
reading the identity version. `red/store-persistence`, fixture
`8010737dbf154dbf8ef94b9ef13e7790`, observes the unlogged-work acceptance defect
and exits 1 with owned jobs joined, root removed and installation unchanged.
The corrected verifier and unexpected-view tripwire pass real-store
verification; the test separately preserves manual recovery **admission**, not
a claim of actual recovery while the extension is unavailable.

The same review identifies pending export survival across version deletion,
including orphaned pending rows from older installations. The correction retires
this deletion's pending version exports atomically and reconciles old orphans
under the existing metadata mutation lock before drain selection, preserving
unrelated work and existing journal files. Retirement uses the existing 1,000-row
bound; remaining orphans are excluded from export and stay visible until
subsequent retirement. Repository coordination precedes the short metadata
transaction and covers file export/acknowledgement. No new recovery format or
independent disaster-recovery claim is introduced.

`red/outbox-deletion`, fixture `3acfe0d08c694e75b361124e00339e1e`, reproduces
failed export → deletion → stale export publication, exits 1 and confirms all
three cleanup/preservation predicates. The final `store-reviewed` fixture
`a6808d5e59d34d1cad1ac60d48eb17f8` exits 0 and is joined: **208 catalogue,
134 metadata and 10 repository checks on each IPv4/IPv6**, failure/location null,
root removed, owned jobs joined and installation unchanged. It also proves
1,001 prior orphan rows retire in bounded batches without publishing the final
orphan, while valid pending history exports and repeated draining is harmless.
It reuses pinned authenticated PostgreSQL manifest
`AF6B95D45491EC1203AAB26CCCFEB2244D4755CECB18609454BA352815F6DA18` with fresh
verified copies; no new reference download. Final TestHost build has zero
warnings/errors. Independent GPT-6-Astra/high review confirms the reviewed files
are unchanged and closes both foundation findings after inspecting final result
and cleanup. Runtime composition, migration/rollback rehearsal and native WTS
authority gates remain open; this is neither automatic capability nor deployment
approval. The unrelated primary-worktree index script/config hashes still match
their recorded values.

The initial focused tests cover reserved immutable capture identity and the
pre-publication source/session guard. These are necessary NEXT-003 prerequisites:
a crash after publication must reconcile the exact result, and a changed source
or expired creator authority must keep work pending without acknowledging a
mixed or unauthorised version. Retain previous good history on either failure.

`red/automatic-identity-red.trx` observes six failing missing-contract assertions;
`green/automatic-identity-green.trx` passes all 27 focused identity/existing
integrity cases, zero skipped, with a joined exit-0 process. The three native
source-stability cases fail before their guard exists in
`red/source-stability-red.trx` (joined exit 1).
`green/source-stability-green.trx` passes all 31 focused native caller/source
cases, including an ordinary unchanged read and a same-length rewrite with its
last-write timestamp restored; NTFS change time still rejects publication.
Generic providers cannot claim an observed source. The first build exposed a
C# definite-assignment error in two combined native queries; splitting those
queries corrected compilation before the passing run. Both processes exited
and were joined. This is prerequisite evidence, not an automatic round trip.

Independent Astra/high refinement requires two ordering corrections before
runtime exposure. Takeover drains the existing repository lease and establishes
a durable generation fence before activating mutations; stale generations
revalidate under that lease before publication/export. Interrupted automatic
claims reconcile before fresh destructive/manual maintenance admission, so a
published but uncompleted version cannot be deleted then mistaken for unstarted
work and resurrected. Reuse the existing lease and bounded startup mutation
barrier. Receipt replay returns its original result without renewing invalidated
consent. The remaining extension/consent/authority refinement is accepted.

The plan's two-batch checkpoint remains binding: a private real-service/DB/UI
automatic round trip and restart result must exist, or the affected path must be
replanned around its concrete blocker. No new tracking framework is introduced.
