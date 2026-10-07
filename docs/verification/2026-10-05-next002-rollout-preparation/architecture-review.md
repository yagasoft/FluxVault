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
