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
