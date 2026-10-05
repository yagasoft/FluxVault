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

## Frozen candidate and completion work

One unsigned v1.0.5 candidate is frozen at
`artifacts/staging-single-vault/289c59630be846cb90f9a623c4ab0d1c`, from source
`2ce19dfdd97f3169d37001f9db15c3d5610e8f12`. Its [manifest](candidate.json)
records 208 payload hashes and five joined successful build processes. All five
build logs have zero warnings. No installer executable is run. The read-only
`actual-candidate-msi.trx` checks this exact MSI, as well as the inert compiler
fixture, and passes the commissioning contract.

| Artifact | SHA-256 |
| --- | --- |
| FluxVault.Setup.exe | `01C842290227EBDC68118BC6BFC72E7364B027CC6DE75B8898B0322BB50725EC` |
| FluxVault.Installer.msi | `01D7ED37854852B1728BF2B3426C28C41014B059088816D5197FCF05D3BDD8EF` |

The [normal catalogue preflight](normal-database-target-preflight.json) uses the
existing `fluxvault` role with transaction read-only enabled and a bounded
statement/connection timeout. It confirms proposed `fluxvault_single` and
`fluxvault_service` are absent. It creates no role/database, changes no grant and
does not reload authentication or restart PostgreSQL.
The prepared SQL's catalogue-only anonymous precheck is also parsed/executed
successfully through that existing read-only role (`normal-create-precheck-parser.log`);
none of its CREATE/COMMENT/REVOKE statements is executed.

The [prepared SQL](authentication/create-fluxvault.sql) refuses existing targets,
creates only that fresh database/account, assigns restricted role attributes and
revokes PUBLIC database access. Proposed authentication files retain unrelated
rules, remove exactly the two legacy FluxVault trust lines in the final files and
add scoped SYSTEM SSPI plus explicit database/account rejection rules. The
temporary files retain the unchanged old trust block for finite read-only
session-retirement monitoring while FluxVault remains stopped. That existing
access itself is not read-only. The temporary administrator
map is SYSTEM-only on `postgres`/`postgres`, with realm identity preserved; it
still confers cluster-superuser authority. Normal-instance correlated SSPI
observation must confirm the expected principal before adding either map.
No prepared SQL/authentication file is applied now.

| Proposed replacement | SHA-256 |
| --- | --- |
| final-pg_hba.conf | `83F8A4844E68DCF26C6F068890794D2327F83544DFDA1E8BF95FB9321628E40E` |
| final-pg_ident.conf | `DD710C85E5064576F4108CDD6862B1A2FAA077D611ED23E31D76CDB56C40E2B3` |
| temporary-pg_hba.conf | `08AA5DB675E5112DE605DF235B6B73CCD37516CC17DB129F4E3C0ECCECADC946` |
| temporary-pg_ident.conf | `71EA5A61F5913FFA3E93523902839F50FB38C115E573F7D477FD86B965717E56` |

Finish the finite owned commissioning supervisor, close/join its non-pooled
administrator connection/backend before removing the exact temporary map/rules
and reloading, prove fresh admin-login refusal and service-only login, then allow
setup. Publish and validate its protected backend PID before any DDL. Use a
fresh short read-only transaction for each monitoring poll; after retiring
admission, repeat the backend-absence check through a fresh service connection.
Account for surviving old-role sessions too: removing trust does not close them.
Join monitoring connections and never terminate unrelated sessions automatically.
Preserve exact original auth bytes and interrupted-cleanup instructions
before any access change. No ordinary runtime starts while temporary admin
access is unresolved. Existing guarded legacy paths and retained v1.0.4 setup/
204 matching installed hashes remain rollback inputs; complete the checked
restore procedure and exact service-policy snapshot before rollout approval.
The fresh [completion preflight](staging-preflight-completion.json) still matches
all 204 installed files and the retained rollback EXE, records zero installed
clients and both absent preservation paths, and captures typed SCM startup/
recovery registry values. The original historical preflight is retained.
[Postmaster identity](normal-postmaster-completion.json) records PID50372,
start `2026-10-01T13:38:16.2557791Z`; neither PostgreSQL frontend nor postmaster
is restarted. These are checked rollback inputs, not a completed restore drill.

The existing C01–G01 matrix remains the completion checklist. Astra's completion
assessment retains C01–C03/S02/manual-only S04/S06/S07/S08 evidence and identifies
only S01 packaged proof, D01 observations/scale, S05 commissioning/rollback and
final G01 as the critical path. No scheduler/VSS/crash matrix or new verification
programme is added. Pending package/profile/trust approval remains distinct from
the separately required normal rollout approval. The slice is not complete.

The bounded S05 preparation now has executable proof. Independent review found
the first generated HBA blocks were each a single comment line; their hashes did
not establish active admission or rejection. The actual PostgreSQL parser
reproduces this failure in `native/c995fd62a1644145b243c15772ec1e8a` before the
correction. [Generation](authentication/prepare-authentication.ps1) now uses
literal multiline blocks and verifies normal authentication hashes before and
after writing only the proposed files. Its [current hashes](authentication/prepared-hashes.json)
supersede the earlier unaccepted proposals. No normal file is replaced or reloaded.

The final [owned run](native/5a477969f6b54962b10cd909a0955554/result.json) uses
PostgreSQL's `pg_hba_file_rules` and `pg_ident_file_mappings` to check the exact
final/temporary/probe bytes, active rule order, loopback scope, parsed realm flag,
exact SYSTEM maps and legacy trust retirement. These views report the current
files, allowing bounded inspection in the disposable cluster; see the
[HBA view](https://www.postgresql.org/docs/18/view-pg-hba-file-rules.html) and
[mapping view](https://www.postgresql.org/docs/18/view-pg-ident-file-mappings.html).
The fixture restores its own original bytes before continuing. Its
[exact SQL proof](native/5a477969f6b54962b10cd909a0955554/prepared-sql.json)
confirms restricted role attributes/password absence, database owner/comments,
PUBLIC denial, the joined administrator backend's absence, and refused repeated
creation with unchanged state. The private postmaster is not restarted.
This proves the prepared SQL, not the unfinished normal supervisor's protected
PID publication, authentication retirement or installed setup entry.

Initial fixture-construction diagnostics (unexported guard, missing projected
sort column, evidence-directory creation and parsed Boolean canonicalisation)
remain retained and supply no acceptance claim. `authentication-tooling-final.trx`
passes 63/63 without skips. An initial broad filter accidentally included two
tests requiring the separately owned integrity fixture; that refusal is retained
in `authentication-tooling.trx`. Unchanged product source/build dependencies
retain the accepted zero-warning Release and C01–C03/full-workflow evidence.
The [fresh census](authentication-fixture-census.json) verifies 151 captured
process identities absent, 126 resources retired across seven runs and every
root/account/group/task/job gone. Normal services, actual postmaster identity,
authentication and unrelated primary edits remain unchanged. The frozen MSI/EXE
candidate is unchanged. The [independent assessment](architecture-review.md)
accepts this bounded correction; S05 commissioning/rollback, S01, D01 and final
G01 remain open before separately approved rollout and live validation.

The administrator handshake checkpoint is now independently accepted. The
[actual helper](commission-administrator.psm1) withholds DDL until the same
owned psql process publishes its closed, bounded backend identity, that identity
matches the expected database/data directory/role/port, and a require-new receipt
has been flushed. It executes the reviewed SQL once and joins the worker on
success, failure and cancellation. Eight real-child regressions cover those
paths, including receipt publication failure and cancellation after an effect;
the combined existing tooling suite passes 71/71 without skips.

The [current native run](native/1ddfdc01f9f848beb004582d7c411551/result.json)
uses that same helper and exact prepared SQL in the owned PostgreSQL fixture.
Its [receipt and inspection](native/1ddfdc01f9f848beb004582d7c411551/prepared-sql.json)
confirm worker joining, backend retirement, restricted database ownership and
repeat refusal. The updated census verifies 205 captured identities absent and
169 resources retired across eight runs. Normal PostgreSQL, authentication,
installed files and unrelated edits remain unchanged; the candidate is unchanged.
This closes backend publication and worker lifetime proof, not the full normal
authentication supervisor, post-revocation checks or rollback. S05, S01, D01 and
final G01 remain the critical path; no additional framework or roadmap scope is
introduced.

Completion preparation now proves the finite authentication phase through the
actual SYSTEM worker and PostgreSQL interfaces. The
[current owned run](native/2d9f2cc1c78447cca233ff5ecce6e018/result.json) uses
[the operator procedure](commission-authentication.psm1): it checks original
bytes and SQL hash before changing admission, preserves durable recovery inputs,
observes the exact SYSTEM SSPI principal, publishes the administrator backend
before once-only SQL, joins its worker, checks fresh administrator/legacy-session
retirement, removes the temporary map, and verifies fresh administrator refusal
and restricted service login without restarting the private postmaster.
An administrator baseline preserves unrelated existing sessions; any new,
unaccounted administrator session blocks activation. The meaningful failing
regression is retained in `unaccounted-administrator-red.trx`. The current native
[baseline](native/2d9f2cc1c78447cca233ff5ecce6e018/administrator-baseline.json)
and [completion receipt](native/2d9f2cc1c78447cca233ff5ecce6e018/authentication-completed.json)
support the stronger retirement check. Ten procedure regressions cover success,
refusal, surviving sessions, final-login failures and failed receipt publication;
failure restores the exact original bytes and keeps runtime blocked.

[Legacy preservation/restore](commission-rollback.psm1) now has real filesystem
round-trip and refusal regressions: retained bytes/root permissions survive,
fresh interrupted state is retained separately, and collisions, changed legacy
configuration and escaping paths are refused. This is the filesystem portion
of rollback; exact installer/SCM restoration and the normal supervisor remain
unfinished. It is not a completed installation rollback drill.

Read-only inspection found effective Authenticated Users DELETE on the two
PostgreSQL ancestors. The [unapplied exact proposal](postgresql-ancestor-acl-proposal.json)
copies their inherited grants as protected explicit grants and removes DELETE
only from the effective grant on each directory. Child inheritance templates,
owner/group, normal data, authentication and service state remain unchanged.
The [native correction/restore helper](postgresql-ancestor-acl.cs) pins the
component chain without DELETE sharing and changes only the two DACLs using
`SetFileSecurityW`. The native regression caught ineffective sharing exclusion
when the directory handle requested only attributes; requesting directory-list
access makes an existing DELETE handle a refusal before any effect.
`ancestor-acl-sharing-correction.trx` proves exact apply/restore, unchanged child
descriptors/data and post-pin access checks using an impersonated token with
Administrators disabled and privileges removed. Formerly allowed DELETE opens
and rename are denied, the protected next child cannot be renamed, and junction
redirection returns directory-not-empty while an empty-directory control succeeds.
This is a bounded S05 correction/proof, not permission to apply normal ACLs.

`commissioning-completion-combined.trx` passes 86/86 without skips. The refreshed
[census](authentication-fixture-census.json) verifies 499 captured process
identities and 390 resources retired across fifteen owned runs. Normal services,
postmaster, authentication/configuration and the two ancestor descriptors remain
unchanged; the frozen 1.0.5 candidate and checkpoint 9815673 remain reusable.
The remaining critical path is the scoped normal PostgreSQL guard/supervisor,
checked installer/SCM rollback, pending S01 packaged rehearsal, remaining D01
desktop observations/scale, and consolidated final G01 before separately approved
normal rollout and live validation. The C01–G01 matrix and roadmap are unchanged.

The scoped normal PostgreSQL guard is now implemented. Seven functional ACL
cases prove the required protected-template, native-volume and trusted-owner
exceptions without admitting ancestor deletion, child deletion, propagating
writers or NetworkService executable writes. A read-only check against the
actual machine still refuses the two unsafe ancestors; their proposed correction
remains unapplied. Administrator helpers use a restricted tool/Windows PATH.
The retained legacy configuration file passes the existing replacement guard;
four real filesystem rollback cases remain green.

An independent finite SYSTEM cleanup task must be alive and outside the
authentication job before temporary admission. It binds the frozen context,
operator, worker job and postmaster, joins that job on forced operator exit,
retires admission and separately proves backend retirement. Exact final bytes
remain final; unconfirmed final state cannot activate runtime. Monitoring failure
also attempts retirement once, records failure and prevents activation.
Closed per-file write intents bind verified old/new bytes before sequential
writes. Cleanup recognises only that interrupted prefix/suffix transition,
preserves the observed bytes and restores originals; arbitrary external edits
remain refused. These controls address the concrete forced-exit and torn-write
failure modes, not a general future lifecycle framework.

Real SYSTEM/private PostgreSQL proofs now pass for
[forced operator exit during admission](native/7fa3d78cc3b9492c899d51c1d3b8263e/result.json),
[mid-write interruption](native/4517c7aacd80410eb9b63c14b6fd79c2/result.json)
and [interruption before truncation](native/a2754a051b6b4bf9a865c13bd6cf0b81/result.json).
Each also proves successful-final non-reversion. Interrupted attempts retain SQL
effects, restore exact originals, deny a fresh administrator login and block
activation. Both write proofs retain actual interrupted HBA bytes and transition
byte records. PostgreSQL is not restarted. The failed rehearsal attempts record
fixture-copy, observation-sharing, unchanged-prefix and resource-name diagnostics;
none is acceptance evidence. Their owned resources are retired as well.
`commissioning-interruption-combined.trx` passes 101/101 without skips or build
warnings. Earlier unchanged product, creator and installer evidence is reused.

The [actual cached v1.0.4 MSI tables](rollback-cached-msi-tables.json) match the
captured LocalSystem, delayed automatic start and 60-second restart policy.
Rollback therefore uses the retained, hash-checked v1.0.4 installer and verifies
the resulting payload/policy; an additional SCM restoration abstraction is
unnecessary. After joining commissioning/cleanup and stopping any new runtime,
uninstall the exact new bundle before attempting the older installer. Preserve
fresh state at the declared Failed path, restore legacy configuration/root and
original authentication before the older installer can start its service, then
verify all 204 retained payload hashes, exact policy/configuration and unchanged
PostgreSQL identity. Restore only the two reviewed ancestor descriptors after
authentication recovery. Never drop the retained/fresh databases, adopt partial
state or retry SQL automatically. A failed rollback keeps runtime stopped and
retains its inputs. No installer, normal ACL or authentication operation is run
by this preparation.

Remaining C01–G01 critical path: pending S01 packaged resource permission; D01
keyboard/Narrator/monitor DPI/high contrast/responsiveness and the unresolved
native capture observation; exact normal setup/activation and rollback execution
preparation; consolidated whole-branch G01; then separately approved installation
and live save → backup → history → independently verified recovery. Native
Computer Use's required `node_repl` interface is not callable in this session;
logical renders and automation names do not close those desktop observations.
The candidate and normal installation remain unchanged. This is not slice
completion or a rollout request.
