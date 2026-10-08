# Independent provisioning architecture assessment

The existing Astra service/security reviewer read the provisioning contract and
current interfaces. Outcome: approved bounded approach, with four implementation
requirements incorporated into the contract before code changes:

1. Validate all targets, the full configuration and both namespace absences
   before the first mutation; a pre-existing metadata namespace must not be
   discovered only after creating a new catalogue.
2. Create fresh private directories exclusively with their final security
   descriptor under pinned parents; require-new bootstrap publication retains
   the protected parent. Existing empty roots also remain refusals.
3. Retain fixed first-instance setup pipe ownership across the host lifetime,
   serialising different processes/tickets. Latch failed attempts once effects
   begin; the protected ticket identifies potential resources durably first.
4. Bootstrap publication activates the installation. Cancellation or lost
   acknowledgement after that point cannot prove non-activation. Preserve the
   exact installation and reconcile through normal authorised status; stop
   admission and join requests before disposing tokens, pins or pools.

The first executable milestone must use real setup → joined host → normal
product open → save/backup/history/verified recovery, including wrong identity,
existing/partial target and publication interruption checks.

This is architecture assessment, not completed implementation acceptance,
G01 approval or normal-machine rollout authorisation. Database/authentication
changes, legacy relocation, service replacement and rollout remain separately
gated. No scheduler, schema migration/adoption or general installer framework
is added.

## Bounded S05 preparation assessment

The independent Astra reviewer accepts the finite retention of the two unchanged
legacy trust entries for retirement monitoring while runtime is blocked. This
does not restrict that old access to reads. Use a fresh short transaction for
each poll, publish and validate the protected administrator backend PID before
DDL, retire admission and reload, then check backend absence again through a
fresh service connection. Account for old-role sessions and join monitoring
connections before activation; removing HBA admission does not close established
sessions. Never terminate unrelated sessions automatically. Uncertainty must
retire administrator admission, preserve partial state and keep runtime blocked.

The reviewer found the initially generated HBA prefixes were comment lines,
requiring active parsed-row proof rather than hashes or syntax-error absence.
The corrected proposed files and optional bounded existing-fixture proof are
now accepted with no blocking finding. Current file hashes match PostgreSQL's
ordered SSPI/reject rows and exact maps; final has zero old trust rules and
temporary/probe have exactly two. Exact prepared SQL proves restricted role
attributes, database ownership, PUBLIC denial, administrator backend retirement
and repeat refusal without target changes. The final owned run has no failure,
tooling passes 63/63 with no skips, and the census verifies 151 retired process
identities/126 resources across seven runs with normal state unchanged.

This assessment covers only the completed preparation correction/private proof.
It does not accept the unfinished normal commissioning supervisor, final G01,
normal application or rollout. The lifetime requirements above remain required.

## Administrator handshake checkpoint

The existing independent Astra reviewer accepts the completed helper checkpoint
with no blocking finding. The real-child tests and current native run establish
that DDL follows validated publication and durable receipt, runs once, and has
a joined worker on success, failure and cancellation. Separate native inspection
confirms backend retirement and unchanged state after repeat refusal. The eight
focused regressions and combined 71-test tooling suite pass. The updated census
includes the new owned run. This assessment does not close the complete normal
supervisor, temporary-admission retirement, checked rollback or final G01.

For the remaining normal authentication guard, the reviewer accepts recognising
the existing NetworkService PostgreSQL authority only at the exact verified
PostgreSQL authentication paths. Verify the observed daemon identity and the
existing operator's direct Administrators membership; retain reparse, ancestor
replacement and untrusted/propagating-writer checks. Preserve existing ACLs and
do not broaden commissioning, executable or vault-root guards. This is a bounded
path-specific assessment, not permission to apply the prepared files or deploy.

## Authentication retirement and bounded ACL checkpoint

The independent Astra reviewer accepts the coherent procedure/fixture/test
checkpoint with no blocking finding. Administrator baselining precedes admission;
fresh retirement checks reject unaccounted administrator and surviving legacy
sessions. Native `2d9f2cc1c78447cca233ff5ecce6e018` proves exact SYSTEM SSPI,
receipt-before-DDL, worker joining, backend retirement, fresh administrator refusal
and restricted service access. The filesystem rollback portion preserves legacy
bytes/ACLs and separates fresh failed state without overwriting it.

The exact two-descriptor native proof establishes unchanged children/data,
DELETE-handle refusal and denials after releasing pins, with an empty-junction
control. A nonempty ancestor remains anchored by its protected next component;
removing additional data/EA/attribute rights is not justified by this requirement.
The reviewer observed the 86/86 combined checks and native ACL regression, plus
the fresh census of 499 identities/390 resources across fifteen retired runs.
Normal state remains unchanged. The scoped normal guard/supervisor, installer/SCM
rollback, S01, D01 and final G01 remain open. This assessment authorises no normal
ACL/authentication change or rollout.

## Normal commissioning interruption requirement

The independent Astra reviewer requires an independently running, finite SYSTEM
cleanup task before temporary administrator admission. Forced operator termination
can kill the contained authentication worker without running its `finally`; manual
recovery alone cannot establish finite admission. Bind cleanup to the exact attempt,
original/prepared hashes, postmaster and operator/worker identities, arm and verify
readiness before changes, coordinate one writer, then terminate/join only the owned
job and separately prove backend retirement. Keep verified successful final bytes
final; never restore legacy trust after success. Setup/SCM activation requires the
completed cleanup proof. Uncertainty keeps runtime blocked with retained recovery
inputs. If cleanup holds a job handle it must explicitly terminate/join that job
on operator death. One disposable forced-exit proof during temporary admission and
successful-completion non-reversion cover this bounded correction. No broader crash
framework or roadmap expansion is required. Implementation/proof remain pending.

## Independent interruption checkpoint acceptance

The same independent Astra reviewer accepts the completed bounded checkpoint,
with no remaining blocking finding. It covers the scoped normal PostgreSQL guard,
safe tool environment, legacy configuration anchor/rollback inputs, independent
finite cleanup/readiness, exact successful-final retention, owned-write intents
and constrained interrupted-byte recovery. Monitoring errors cannot bypass the
one cleanup attempt; the watchdog verifies that its own PID is outside worker
containment before admitting temporary access. Activation must reject a watchdog
failure even when fallback cleanup publishes a retirement receipt.

Native `7fa3d78cc3b9492c899d51c1d3b8263e` proves successful-final preservation and
forced operator death while the temporary map is active. Native
`4517c7aacd80410eb9b63c14b6fd79c2` and
`a2754a051b6b4bf9a865c13bd6cf0b81` prove HBA mid-write/before-truncation recovery;
the pinned-file regressions separately cover torn `pg_ident` writes. Actual
interrupted bytes remain available. All cases retire admission and block failed
activation without restarting PostgreSQL. The combined checks pass 101/101 with
no skips, and the refreshed census confirms 22 retired runs, 867 absent captured
process identities and 628 retired resources. Normal installation, authentication,
ancestor ACLs, candidate hashes and unrelated primary edits are unchanged.

This is technical checkpoint acceptance only. Exact normal setup/activation and
rollback execution preparation, S01 resource permission/proof, D01, whole-branch
G01 and separately approved normal rollout/live validation remain open.

## Consolidated executable procedure acceptance

The independent Astra reviewer accepts the fixed candidate installation/rollback
procedure and disposable native proof, with no remaining blocking finding in
this scope. The initial review identified two existing interruption invariants:
an unmatched installer intent after operator death must block further installers,
and task ownership must be durable before registration and recoverable without
a launch acknowledgement. Both are corrected. Reopened-context regressions use
real filesystem receipts and the actual parent recovery/wait/removal flow,
substituting only its scheduler boundary; mismatched tasks remain preserved.

The combined relevant checks pass 118/118 without skips or build warnings.
Native `3423b5b4d7b3447eadc358951f3c5ec7` exercises the exact SYSTEM rollback
worker/task intent/dispatch/teardown, restoring original authentication and
retiring administrator access without restarting its private PostgreSQL.
The fresh existing census verifies 25 runs, 1,059 captured process identities
absent and 753 resources retired. Normal authentication, postmaster, ancestor
ACLs, 204 installed payload files, 208 candidate payload files and unrelated
primary changes remain unchanged. The exact source ticket is validated by the
frozen product; the read-only operator preflight passes. The legacy installer
executes only from a hash-verified protected copy of its retained input.

This is acceptance of executable preparation, not normal installed execution,
an installed legacy reinstall drill, S01/D01, complete G01 or rollout authority.
Full-branch independent implementation assessment is proceeding while the
specific S01 resource approval and D01 manual-observation responses are pending.

## Consolidated code and replacement candidate acceptance

Independent Astra review completed the whole-branch implementation assessment
and identified one C02/S07 defect: incomplete or invalid service response frames
could fault WPF commands instead of preserving usable uncertainty. The bounded
client response-read correction is accepted, with no remaining blocking finding
in the code/preparation delta. It retains the original cause as an inner exception,
outgoing validation, caller cancellation and authoritative complete responses.
Actual-client/real-store Main/Options save, backup and original-receipt regressions
prove retained records/operation IDs/newer edits without dependent backup or blind
retry. This corrects the review finding without another orchestration abstraction
or native fixture extension.

The reviewer independently verifies both installer hashes and all 208 payload
hashes for replacement candidate `51b103b3c75645dfaa36600e2cbe07a5`, source
`e4736366cd6c47598523e62ffcbb6f71810f61a2`. Its five builds join without warnings;
the actual replacement MSI contract, frozen typed ticket and read-only preflight
pass. Final ordinary suites pass 1,483 cases with zero skips. Native `696378...`
passes the existing actual single-vault save/backup/history/verified recovery
workflow and cleanup. The fresh census verifies 26 runs, 1,103 captured process
identities absent and 796 resources retired, plus five replacement builders
absent, with normal installation and unrelated files unchanged.

The previous full-source and executable-procedure assessments remain applicable.
This accepts the code and preparation portion of G01, not full G01 or rollout.
S01 packaged runtime permission/proof, D01 actual desktop/scale observations and
separately authorised normal commissioning/live acceptance remain open. The
normal fixed entry/SCM effects and retained-installer rollback drill have not run.

## Final predeployment technical assessment, 7 October

Independent Astra review accepts the consolidated code and executable preparation
at `31a9834`, together with the prepared-input correction to the accepted
commissioning lifetime refresh. Product source and candidate are unchanged;
all eleven recorded procedure hashes match. The reviewer accepts the bounded
inventory delta, five focused regressions and joined native cleanup evidence,
with no new source or procedure blocker.

This completes the current candidate's technical preparation assessment. Full
G01 remains conditional on D01's actual desktop observations and separately
authorised installed setup/S05/live save → backup → history → independently
verified recovery. Blank captures and zero native saves supply no D01 closure.
The installed legacy reinstall drill remains unexecuted; checked rollback
preparation is not demonstrated installed recovery. No operational authority
or slice-completion claim is supplied by this review.

## Immediate predeployment consolidation, 7 October

The same independent Astra reviewer accepts the exact approved installation
operation, with no remaining predeployment blocker. Review reuses the accepted
whole-branch/procedure assessment and checks the fresh Prepare evidence:
208 candidate files, 204 legacy files, all eleven procedure hashes and the
pinned unchanged postmaster. Native desktop evidence supplies rejected-save
clarity, retained edits, navigation, 10,000-item browsing and actual DPI layout.
The user's accessibility/theme deferral is explicit; those items are not passed
and all other UI/UX requirements remain.

Proceed only with the reviewed unchanged candidate/procedure and their existing
refusal/recovery controls under the user's separate explicit rollout approval.
Installed S05, visible save → backup → history → independently verified recovery
and final cleanup remain necessary for completion. The legacy reinstall drill
remains unexecuted; this assessment supplies no demonstrated installed rollback
claim.

## Concrete ACL checkpoint continuation acceptance

The independent Astra reviewer accepts the managed comparison correction,
original-preserving operator handoff and exact checkpoint continuation. No
remaining source blocker is identified. The reviewer inspects the concrete
procedure, read-only normal preflight and all twelve current procedure hashes.
Native raw ACL comparisons/pins are unchanged. Partial/replayed handoffs fail
before rollback effects; the continuation cannot repeat the installer or ACL
application. Only the operator process identity changes in the successor context,
with explicit provenance for the already completed installer receipts.

Acceptance was conditional on the affected combined checks passing and joining
before effects. The observed `normal-rollout-continuation-combined.trx` result is
135 passed, zero failed/skipped, no build warnings; the dotnet process exits zero
and is joined. This satisfies that condition under the user's existing exact
rollout approval. Installed security, visible live workflow/verified recovery and
final cleanup acceptance remain outstanding.

## Concrete native readiness and pre-admission reset acceptance

Independent Astra accepts the exact held-native readiness correction and bounded
pre-admission reset plus subsequent guarded continuation. The reviewer checks11
focused passing tests, the read-only normal reset preflight and all12 procedure
hashes. No blocking finding remains. Acceptance requires the affected combined
run to pass and join before effects. Reset must be followed by the existing
checkpoint Check before continuation. No installer/ACL replay or authentication
retirement bypass is permitted; the next attempt must establish its own genuine
baseline and successful retirement. Installed/live acceptance remains outstanding.
The exact pre-admission watchdog failure may be archived as no admission effects,
with retirement unconfirmed, without inventing a baseline or altering unrelated
administrator sessions. This exception applies only to this file-only checkpoint.

The required affected combined run subsequently passes146, zero failed/skipped,
without build warnings; it exits zero and joins. Fresh cleanup proves all captured
verification/commissioning actors and owned jobs/tasks absent. This fulfils the
review's pre-effects condition. No reset or further commissioning effect is yet
applied; installed/live acceptance is still required.

## Presentation correction design amendment

The independent Astra design reviewer accepts an unconditional process-local
SoftwareOnly choice before App presentation creation. The concrete failure is
independently visible in a Windows Snipping Tool capture; actual App startup has
valid layout, native visibility and rendering events, while a single-variable
SoftwareOnly diagnostic displays the complete UI. A particular NVIDIA driver
fault is not established. No Options toggle, global graphics setting or automatic
fallback framework is justified. Architecture, single-vault scope and security
contracts remain unchanged.

Required delta proof is fresh normal App startup without a harness override,
native pixels and working navigation/reopen/Options/activity, actual175%/200%
DPI, bounded10,000-row responsiveness and CPU/idle observations, then live
save/history/independently verified recovery. Existing D01 has qualitative
responsiveness criteria and bounded workload measurements, not an agreed numeric
native-input threshold; record measurement limits without inventing a new gate.
Layout, UI Automation and RenderTargetBitmap alone cannot satisfy D01. This is
approach acceptance, not final implementation or revised deployment acceptance.

## Frozen 1.0.6 pre-deployment technical gate

The independent GPT-6 reviewer approves the exact frozen technical approach,
with no required correction or unresolved technical decision. Its runtime variant
is not exposed; it does not claim an Astra identity or a model switch. This uses
the applicable independently capable equivalent permitted by AGENTS.md.

The reviewer independently checks all 208 candidate payload hashes, manifest
`6BD79D60D69D6953EA2EB80EF94557AAAA0E0EBFAB4A5F5B473F9C11B670F43A`,
MSI `96D80AA626F32A20A9E6703A098F57885EADC2A869E0F8F6375D11CE8D37A555`,
Setup `3B2BB6F2CE539571131FD148CB4973A91649A392274BA4C6645E26B7D17D6E34`,
and `update-presentation.ps1` SHA-256
`1CB98CF02EEF98C5A09D53BEB7CCA34D20313179857A67D63B474575FC6DBD6C`.
The source delta affects only process-local App rendering; retained security,
integrity, commissioning and PostgreSQL evidence remains applicable to unchanged
product boundaries. The procedure pins sealed MSI identities, checks settled
registration, preserves commissioned data/authentication/ownership, joins native
children and verifies payloads before restoring service policy and activation.
Rollback covers neither-installed state and refuses parallel/unresolved/reboot
states, without invoking legacy commissioning recovery.

The affected combined343/343, zero failed/unexecuted, candidate zero-warning
build logs, seven guard red/green cases and unchanged App487/Core563/Windows154
results are inspected. Native staged interaction, actual DPI/navigation, rejected
save retention and four independent matching recoveries are reviewed. Synthetic
format and timing limitations remain explicit. Technical approval does not grant
operational authority. The exact revised decision has been requested; installed
corrected-candidate D01 and live postconditions remain required before G01 closes.

## Frozen 1.0.7 technical gate

Independent review accepts the narrow Options reconciliation correction and the
fixed6→7 procedure, conditional on the running ordinary Integration run passing
and joining. The full App 489 and protection-save139 cases pass; both real modal
activation regressions fail before the correction. Shutdown cancellation/draining,
revision/edit-generation and failed-reload safeguards remain intact.
Candidate c4e807860b1c49e798b2380652ca5926, source e3b5729, all 208 staged payloads
and zero-warning joined build logs are independently checked. ManifestSHA-256
151024FCF7CDA53ED0345A85707E042276A244B80A7FFE71FFB1275276393BE0;
MSI3C64BE547EE678B6CC6349FE31066A9BBE11EC0E25730E8B063AEA98F0EA693E;
Setup6286EA314C9B9537E954573AFD4BAEA02FA8D0355B7FF97558ABA02DBB5025AA.
ProcedureSHA-256D270BBDD11EB054D009DC66B07FD14ABFF958F9661799ECBDBEB6AFC5DEEA42A.
Retained6 rollback restores its known Options race, preserving fixed rendering.
Installed7 Options closing/reopening and full verified recovery remain required.
This is an independent capable equivalent, with exact runtime variant unavailable;
no model switch is claimed. Plan-wide user rollout authority is recorded separately.

## Final branch and installed acceptance, 8 October

Independent final review approves NEXT-002's agreed technical and live scope;
no blocking correction remains and G01 closes. It verifies Integration 343/343,
retained-package guards 8/8 and App 489/489; unchanged Core 563/Windows 154 and named
native identity/integrity/interruption/DPI/scale evidence remain applicable.
The installed 1.0.7 MSI joins successfully,208 payload hashes and sole expected
registration match, and service/creator/authentication/ACL/normal PostgreSQL
postconditions are preserved. Native Options close/save/reopen and the full
installed save → backup → history → restore pass. The reviewer independently
rehashes both final originals and recovered files and confirms exact lengths.

The final fixed procedure SHA-256 is
4BB9001B90F8A455A10E0D8D2270B69D117D85AA4789CEBC57E0CAD33C7558F7.
Retained original manifests, installers and fifteen build logs match outside the
disposable checkout; the path correction preserves effect ordering and refusal
guards. C01–C03/S01–S02/S04–S08/applicableD01 are satisfied; S03 remains retired.
Accessibility/theme are deferred, not passed. Synthetic formats, manual-protection
baseline and timing limits remain; no general application fidelity or throughput
certification is claimed. Documentation and authorised Git closeout may proceed.

## Hosted CI handoff correction

Hosted run37736542823 built successfully but failed before executing tests: its
obsolete `PostgreSqlBinPath`/`RunFullSuite` arguments no longer match the approved
integrity entry point. Independent review accepts explicitly separating ordinary
hosted tests from the required native PostgreSQL/security lane. The hosted job
uses `Category!=RequiresPostgreSql`; the40 native cases and applicable S01–S08
requirements remain required, with unchanged accepted evidence reused only
within its valid code/dependency/configuration/environment scope. This does not
restore the retired elevated TEMP/trust runner or generalise the privileged
fixture for hosted execution. The testing guide now reflects the actual approved
`FixtureId`/`EvidenceDirectory` interface. The two existing workflow-contract
checks pass and their host exits zero and joins; these narrow structural checks
do not prove hosted execution. Fresh execution of the corrected hosted job is
required for Git handoff, with results recorded by [PR34's checks](https://github.com/yagasoft/FluxVault/pull/34/checks).
Installed1.0.7 and its acceptance are unchanged.
