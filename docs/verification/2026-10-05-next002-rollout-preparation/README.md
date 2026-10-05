# Installation preparation

The [once-only provisioning contract](provisioning-contract.md) and its
[independent architecture assessment](architecture-review.md) define the missing
NEXT-002 installation prerequisite. The approach is approved for implementation;
provisioning code, native setup evidence and completed-change review are pending.
Normal installation rollout is not approved or performed.

The [read-only preflight](staging-preflight.json), produced by
[this script](capture-staging-preflight.ps1), freshly matches all 204 retained
v1.0.4 installed payload files and the retained setup SHA-256. Both original
service identities and the legacy configuration/PostgreSQL authentication hashes
remain unchanged. No installed clients were running at observation time. The
two exact proposed preservation paths are absent. Database/role name collisions
are explicitly unchecked; no database connection, SQL, reload, service transition
or storage move is performed by this script.

Self-review corrected a PowerShell array-expression grouping error in the
read-only path preview before committing it. The final script explicitly checks
two distinct exact preservation paths and the intended native operator, and its
final observed output satisfies those assertions. Neither preview executed a
move or used the proposed paths as deletion input.

The first executable checkpoint remains authenticated native setup → joined
setup host → ordinary product open → save/backup/history/verified recovery in
the existing disposable fixture. Preserve partial state on failure; bootstrap
publication is activation. Scope, architecture, creator access policy and the
overall roadmap remain unchanged. Package identity, complete desktop/scale,
final G01/deployment review, exact candidate/rollback and separate operational
approval still apply.
