# Installation preparation

The [once-only provisioning contract](provisioning-contract.md) and its
[independent architecture assessment](architecture-review.md) define the missing
NEXT-002 installation prerequisite. The provisioning component and explicit
service setup mode are implemented. The [corrected preflight](setup-preflight-review.md)
and [completed checkpoint review](setup-acceptance-review.md) accept the bounded
native result below.
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

The first executable checkpoint passes in fresh fixture
[`534f66d5c3814eeeafe4d5eb2c058d2b`](native/534f66d5c3814eeeafe4d5eb2c058d2b/result.json):
authenticated native setup → joined setup host → ordinary product open →
save/backup/history/independently hash-verified file and folder recovery.
Thirty-six setup checks cover wrong native actor/correlation, existing empty/file
targets, both namespace refusals, exclusive-creation races, publication
cancellation/failure/collision, retained partial state, same-lifetime result replay
and reopened-target refusal. The native owner passes 143 workflow checks,
ungranted B is denied setup and nineteen ordinary commands, and SYSTEM records
163 successful ordinary responses. Actual view-model/local-draft and real-store
configuration preservation and save/backup sequencing remain covered.

Core 105/105, Windows 154/154 and tooling 63/63 pass without skips. The final
complete Release build has zero warnings/errors. An initial tooling selection
included two existing database tests without their required owned integrity
fixture; both refused execution. That diagnostic remains in
`setup-tooling-combined.trx`; the corrected existing category filter passes.
A concurrent copy diagnostic (`setup-release-copy-race.log`) is retained;
joining the tooling host and rebuilding removes both transient MSB3026 retries.

The first native attempt, `6eb5ebbe54e04cbaabf12fd52ac73ec6`, failed the protected
ticket-parent guard before provisioning. The fixture now writes its nine flushed
tickets below a fresh SYSTEM-owned `catalogue/setup-intents`, preserving the
product guard. Its [failed-run census](failed-native-census.json) records the
unaccepted run and all 41 captured identities/37 resources retired. The
[accepted-run census](accepted-native-census.json) verifies 44 identities/43
resources retired. Both roots/accounts/group/tasks are gone; normal services,
authentication/configuration and unrelated primary edits remain unchanged.

This runs real provisioning components over the private fixture pipe. The
installed fixed-path Service.exe setup entry has not been executed. The bounded
creator command and installer ordering are now implemented and verified below.
Next prepare the exact candidate/hash, database/authentication delta and checked legacy rollback before
requesting the separately required installation approval. Scope, architecture,
creator policy and the overall roadmap remain unchanged. Packaged identity,
complete desktop/scale, final G01/deployment review and live rollout remain open.

The fresh [creator CLI run](native/a773b350b6a74cb6a4977a478cc0c1db/result.json)
confirms actual CLI dispatch as native A through the existing SYSTEM-verified
private fixture pipe, then the same product provisioning and full workflow.
Setup36/A143/B19/SYSTEM163 pass, including creator ownership and both real-store
and actual view-model configuration/command contracts. Parent acceptance requires
`CreatorCliConfirmed`; the setup host is joined before ordinary product open.
The [fresh census](creator-cli-native-census.json) verifies all 44 captured
identities and 43 journal resources retired, root/accounts/group/task absent,
normal services/authentication/configuration and unrelated edits unchanged.

`commissioning-combined.trx` passes 75 checks: strict CLI parsing/correlation,
contradictory reply rejection, cancellation/deadline join and no automatic retry;
existing diagnostic CLI file round trips; actual developer script function flow
with native/event mutations intercepted; and real compiled-MSI table inspection.
The MSI test first reproduced every premature start/data/recovery defect
(`installer-order-all-red.trx`), then passed stopped/demand-start/no state creation.
Its inert cab files are never executed and the MSI is opened read-only, never
installed. Registration tests also reject actual untrusted temporary ACLs and
explicit/inherit-only writer descriptors before effects. Read-only inspection of
the normal installed dependency tree/ancestors passes the protected-payload guard;
no registration is attempted. `creator-cli-tooling.trx` passes 63 checks.

Retained test-construction diagnostics include the initial inert harvest lacking
a dependency file, raw-string/analyser corrections and an empty ticket filter;
none supplies acceptance evidence. `creator-cli-ticket.trx` passes all nineteen
ticket cases. `commissioning-release-final.log` reports zero warnings/errors.
The [independent completed review](creator-installation-acceptance-review.md)
accepts this bounded checkpoint; its obsolete VSS quickstart statement is corrected
to the implemented manual-only/unavailable-VSS contract. No signed package/profile/trust,
normal SQL/authentication, installed setup, service transition or data move is
performed by this checkpoint.
