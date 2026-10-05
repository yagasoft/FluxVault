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
