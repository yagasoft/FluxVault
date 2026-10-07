# Installation preparation

The [once-only provisioning contract](provisioning-contract.md) and its
[independent architecture assessment](architecture-review.md) define the missing
NEXT-002 installation prerequisite. The provisioning component and explicit
service setup mode are implemented. The [corrected preflight](setup-preflight-review.md)
and [completed checkpoint review](setup-acceptance-review.md) accept the bounded
native result below.
The user approved the unchanged prepared normal rollout on 7 October: “When you
get to the rollout, it's approved by me.” The installer has now completed and
the exact raw ancestor ACLs are applied. Commissioning stopped before any
authentication/provisioning effect at the managed ACL comparison described below;
the service remains stopped while guarded continuation is reviewed.

Current critical path, 7 October: native desktop observations now resolve the
earlier blank-capture dependency. Only accessibility and theme checks/remediation
are explicitly deferred by the user; other UI/UX requirements remain. Complete
the fresh read-only preflight and bounded predeployment review, then execute the
approved exact rollout. Installed S05 and live save → backup → history →
independently verified recovery, including visible UI/UX states and final cleanup,
follow that approval. Historical status entries below retain their original context.

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
Computer Use's `node_repl` interface has become callable, but native window
enumeration and capture return inconsistent handles. Its documented single
recovery retry fails; logical renders and automation names do not close those
desktop observations. A guided manual session is awaiting the user's response.
The candidate and normal installation remain unchanged. This is not slice
completion or a rollout request.

## Executable candidate installation and recovery

[The fixed operator procedure](commission-installation.ps1) connects the frozen
candidate to authentication retirement, the actual service setup command and
native creator CLI, joined setup/verified bootstrap, and service activation.
Its default `Prepare` mode is read-only. [Observed preflight](installation-procedure-preflight.json)
matches all 208 candidate and 204 legacy payload hashes, exact ticket/SQL/auth
inputs, current service policy and original preservation/ancestor state. Product
source originally matched `2ce19df`; the response correction and replacement
candidate recorded below supersede that candidate.

Targets remain `C:\Program Files\FluxVault` and the new creator-owned single-vault
bootstrap/repository/state under `C:\ProgramData\FluxVault`. The protected
commissioning directory is `C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f`.
The [typed ticket and command inputs](normal-setup-inputs.json) pin creator
`S-1-5-21-136112424-624261118-1239521417-1001`, installation
`7871ff7f8d1b404db20771f2e742364f`, vault `675392789292487093cda011419228dc`
and ticket SHA-256 `8DEAAA406C027C361E20C32C90F4C9AFD7ADDA76A72528E4D5E1E2936CA2DDE4`.
The frozen product parser validates the ticket. Initial folder/mirror selections
are empty; local-first/manual-only defaults apply.

Run `commission-installation.ps1 -Mode Prepare` for the read-only recheck.
Only after the required technical gates and separately recorded operational
approval, run the same script with `-Mode Install -OperationalApprovalRecorded`.
The switch records the caller's obligation; it does not grant approval. The
procedure refuses existing attempt/preservation targets, quiesces FluxVault and
its recovery policy, preserves legacy state, installs the exact bundle stopped
and on demand, verifies the full installed payload, applies only the reviewed
two-ancestor DACL correction, then runs and joins authentication workers.
Setup waits for independent successful retirement, never merely a receipt.
A separate SYSTEM job creates the protected ticket copy and runs the fixed
installed setup entry. The native creator CLI confirms the exact instance/vault
and revision; the SYSTEM host must exit successfully with a matching protected
bootstrap. Only then may SCM recovery/delayed automatic start and runtime begin.

For approved rollback, use `-Mode Rollback -OperationalApprovalRecorded`.
Recovery reopens the frozen protected context and its separately pinned parent
snapshots, recovers task ownership from the durable pre-registration intent
even without launch acknowledgement, and joins owned actors. Every unmatched
installer intent blocks further installers: a killed launcher cannot prove an
external MSI transaction has ended. No SQL/setup/installer is retried automatically.
Uninstall the exact new bundle before restoring legacy state and original
authentication; retain fresh state at the declared Failed path. Restore only
the two reviewed ancestor DACLs, then run the retained v1.0.4 installer and
verify all 204 files, exact configuration/SCM policy and unchanged postmaster.
The retained installer source has replaceable ancestors, so execution uses only
its hash-verified protected commissioning copy. Failure keeps FluxVault stopped
and preserves both databases, repositories and recovery records. No PostgreSQL
restart or automatic data deletion is included.

The launcher/operator flow regressions exercise actual function sequencing,
typed ticket parsing, real filesystem receipts and joined real children. Native
state/bootstrap and installer/SCM boundaries are substituted in those focused
checks; they do not establish installed execution. The native
[rollback run](native/3423b5b4d7b3447eadc358951f3c5ec7/rollback-authentication.json)
uses the exact SYSTEM rollback worker, task intent/dispatch/teardown and private
PostgreSQL to prove original-byte restoration and administrator retirement.
The earlier `c1f7d...` proof predates the task-intent correction. The failed
`94b006...` construction is retained, including its exact task-hash recovery;
it supplies no acceptance claim. All three owned clusters/tasks/actors retire.
The final combined checks pass 118/118 without skips or new build warnings;
the [consolidated review](architecture-review.md) accepts this executable
preparation scope. The refreshed existing census verifies 1,059 captured
identities absent and 753 resources retired across 25 runs, with normal state,
the candidate and unrelated changes unchanged.
The native setup/creator/full-workflow and installer checkpoints at `9815673`,
authentication/path `5d5ba09` and interruption `ad6087a` remain valid.

| Matrix gate | Exact remaining dependency |
| --- | --- |
| S01 | Closed by the separately approved 7 October fixed-fixture machine-leaf rehearsal: actual packaged A5/B4, caller recovery independently verified, ungranted identity/history refused, exact trust/resources retired. Independent Astra review accepts the proof and cleanup; prior native elevated/user/group/reopen evidence remains valid. No normal rollout approval is implied. |
| D01 | Agent-operated native desktop observations now verify visible keyboard focus, complete failed-save messages, retained pending edits, zero dependent backups, file/Protection navigation, 10,000-item grid interaction and actual 175%/200% DPI. Both fixtures and Settings exit; scale/contrast are restored and normal state is unchanged. The user defers accessibility/theme work only, including Narrator and the recorded contrast defect; none is reported as passed. Other UI/UX gates remain, including installed successful workflow, visible status/history/recovery and final responsiveness observations after rollout. No user testing assistance is requested. |
| S05 | Execute the fixed installed SYSTEM setup and verify normal SSPI/database/filesystem boundary, unchanged unrelated PG state/postmaster and checked legacy recovery during the now approved exact rollout. Disposable native worker/component proofs and exact preparation are retained. |
| G01 | Final consolidated code/procedure preparation is independently accepted, including the response correction, S01/repin and bounded inventory delta. All eleven prepared procedure hashes match; affected checks and S01 cleanup pass. Consolidate the new desktop observations/user deferral at the predeployment gate; full G01 still requires separately approved normal commissioning/S05/live UI/UX and cleanup acceptance. |

This is a candidate procedure and checked preparation, not an installed rollback
drill, full matrix acceptance or NEXT-002 completion. No normal installation,
authentication, ancestor ACL, service or data move has been performed.

## Full-branch review response correction

The independent whole-branch review found one C02/S07 blocker: a response cut off
after its first byte throws `InvalidDataException`; malformed JSON and invalid
UTF-8 also escape some actual WPF save/backup/receipt handlers. The durable
records survive, but the usable unknown-outcome flow fails. Test-first client
and real-store/view-model regressions reproduce this without extending a native
fixture. The smallest correction is confined to the production client's response
read: classify framing/JSON/UTF-8 failures as `IOException`, retain the inner
exception, and use the existing uncertainty/reconciliation flow. Outgoing request
validation, caller cancellation and authoritative complete-response receipt
behaviour remain unchanged. The independent reviewer accepts this approach.

The seven client failure cases and nine actual Main/Options command cases fail
before correction. They pass afterwards: records retain the original operation,
newer edits survive failed receipt checks, no dependent backup or blind retry is
sent, and a valid original receipt permits reconciliation. Actual reopened stores
prove the committed configuration. The final combined ordinary result is Core
563, App 487, Windows 154 and integration 279, all passed with zero skips and no
build warnings. `ipc-response-core-final.trx` includes the added caller-cancellation
proof; the other three `ipc-response-combined_*.trx` files are the combined run.

The correction invalidates the frozen product candidate `289c596...`; retain it
as superseded evidence and build one replacement candidate. The accepted
commissioning/authentication/ancestor/rollback implementation and unaffected
checkpoints `9815673`, `5d5ba09` and `ad6087a` remain reusable. A fresh existing
native single-vault run `6963785f3a3a4de986548666b636c92a` passes the actual
authenticated workflow (creator A143, SYSTEM163 and 36 setup checks, ungranted B
refused). It verifies independent recovery hashes and caller edit access.
All owned jobs are joined, the fixture root is removed, and the normal installation
is unchanged. No package/profile/trust operation is included. S01 permission/proof, D01 actual
desktop/scale observation, separately approved normal commissioning/live workflow
and final matrix acceptance remain open. This is not rollout approval or slice
completion.

The sole current candidate is `51b103b3c75645dfaa36600e2cbe07a5`, built from
`e4736366cd6c47598523e62ffcbb6f71810f61a2`, unsigned v1.0.5.0. Its
[manifest](candidate.json) pins all 208 files and the exact installers:

- MSI SHA-256: `A419E369B86D6AB841410179B50EB015D0221F23097AC20BFE3B46A0D29B7DAD`.
- Setup SHA-256: `8615CD5DFBF7B0B66A2318E21306C35D29729B61741547095140944F4033D38F`.

Both remain under `artifacts/staging-single-vault/51b103b3c75645dfaa36600e2cbe07a5/installer`.
All five zero-warning build processes exit and join. The frozen assemblies validate
the same typed ticket and creator/installation/vault identities. The actual
compiled MSI contract and refreshed read-only operator preflight pass. Only the
candidate/source pins change in the accepted operator procedure; normal targets,
proposed SQL/authentication/ACL bytes and rollback ordering remain unchanged.
The superseded manifest is retained as `candidate-superseded-289c596...json` and
its ignored artifact directory remains intact. The fresh existing census verifies
26 runs, 1,103 captured identities absent and 796 resources retired, plus all five
replacement build processes absent. Normal authentication, ancestor ACLs,
installed payload and unrelated primary changes remain unchanged. Independent
Astra review accepts the complete source assessment and code/preparation delta,
closing the C02/S07 blocker. The existing installation/recovery procedure is
executable for this exact candidate. S01/D01 and approved normal fixed-path
installation/live validation remain required before complete matrix acceptance;
the installed rollback drill remains unexecuted. No rollout approval is requested
until the remaining predeployment technical gates close.

The approved 5 October resource/manual attempt advances S01 to an actual Windows
registration failure, not acceptance. The [retained native evidence](../2026-10-05-next002-packaged-identity/README.md)
and [scoped fallback proposal](../2026-10-05-next002-packaged-identity/native-rehearsal-approval.md)
make its remaining dependency explicit. The [manual attempt](../2026-10-05-next002-desktop-observation/manual-attempt-8cc8ee6adf884f2da24c76e272b7e260/cleanup.json)
retired its UI without observations; D01 awaits a ready human session. Only
fixture code changed: candidate `51b103b3c75645dfaa36600e2cbe07a5`, product source,
configuration/authentication/ACL bytes and the executable normal commissioning
procedure remain frozen. S05 installed execution and full G01/live acceptance
remain behind the separately required rollout approval.

On 7 October, the [authorised packaged rehearsal](../2026-10-05-next002-packaged-identity/README.md)
closes the remaining S01 packaged proof. The correction is confined to fixture
trust ownership and strict test-host configuration; candidate/source, ticket,
SQL, proposed authentication/ACL bytes and rollback ordering remain unchanged.
The normal services had restarted outside this work on 6 October. Fresh
[read-only capture](staging-preflight-20261007.json) matches the installed 204-file
payload, retained installer, configuration/authentication hashes, ACLs and service
rollback policy. The original input is preserved as
`staging-preflight-completion-20261005.json`; only service lifetimes/observation
time change in the active input. The fixed actual postmaster is now PID 10660,
start `2026-10-06T09:31:44.0114872Z`, same executable/data/5432 binding. The existing
[Prepare command result](prepared-check-20261007.json) matches all 208 candidate
and 204 legacy hashes and reports no rollout authority. Independent review
accepts this refresh. Normal PostgreSQL was never restarted by this work.

The critical path is D01's ready guided desktop session (including non-empty
inventory responsiveness), then final technical consolidation and the one exact
normal rollout decision. S05 installed authentication/setup/SCM/filesystem proof
and live save → backup → history → verified recovery follow that separate
approval; the installed legacy reinstall drill remains unexecuted. No new plan
or gate is introduced, and normal rollout approval is not yet requested.

The final [independent technical assessment](architecture-review.md) accepts
the current code/procedure preparation at `31a9834`, including the inventory
delta. Reconciliation corrects one stale procedure hash in
`normal-setup-inputs.json` after the accepted lifetime refresh and removes its
obsolete S01 dependency; all eleven listed hashes now match. The product candidate,
SQL/authentication/ACL changes and rollback ordering remain unchanged. D01 human
observations are the remaining predeployment dependency. S05/installed live proof
and complete G01 follow the separate rollout decision; the actual installed
legacy reinstall drill is still unexecuted.

The agent subsequently completed two [native desktop sessions](../2026-10-05-next002-desktop-observation/README.md)
with usable capture, actual failed-save/pending-edit interaction, native inventory
navigation and Windows scaling. The explicit user deferral applies only to
accessibility/theme work, not other UI/UX checks. Both owned WPF processes and
Settings exit; original 175% scale and None contrast are restored. This supersedes
the ready-human-session dependency without changing product source, candidate,
procedure, SQL/authentication/ACL bytes or rollback. The fresh read-only
`prepared-check-20261007-desktop.json` and current predeployment assessment support
the exact operational decision; installed success/recovery UI observations remain
part of live acceptance, not an exemption.

The [immediate predeployment consolidation](architecture-review.md) accepts
the exact approved installation operation with no remaining predeployment blocker.
Fresh Prepare matches all 208 candidate/204 legacy hashes, all eleven procedure
pins and the unchanged normal postmaster. The user's separate approval is recorded
in `normal-setup-inputs.json`. The next action is the existing fixed Install mode,
followed by installed S05, visible live UI/workflow/recovery and cleanup evidence.
No installed acceptance or demonstrated legacy reinstall is claimed in advance.

## Approved rollout and ACL checkpoint recovery

The [original Install attempt](normal-rollout-install.log) completes the exact
installer with exit zero and a joined launcher. All 208 installed payload hashes
match. Legacy state is preserved at the approved rollback path. The unchanged
native ACL helper writes and verifies the exact two reviewed raw descriptors.
The parent's subsequent `Get-Acl.Sddl` comparison fails because DirectorySecurity
canonicalises explicit ACE order. The [native diagnostic](normal-rollout-acl-diagnostic.json)
proves both raw descriptors match exactly and all five recorded children are
absent. Original authentication bytes and the PostgreSQL postmaster are unchanged;
there are no authentication/provisioning tasks or fresh vault state. FluxVault
remains stopped/demand-start. This is a partial rollout, not live acceptance.

The existing native ACL regression now calls the actual operator function and
reproduces that failure [before correction](normal-rollout-acl-red.trx).
Normalising only the managed comparison passes the [focused proof](normal-rollout-acl-green.trx),
including six distinct changed descriptor refusals and already-restored rollback.
Native exact descriptor comparisons, physical pins and child preservation remain
unchanged. The affected tooling batch passes 128 checks without skips or warnings.

The original operator has exited, so independent authentication supervision
requires a fresh process binding. The independently accepted recovery design
preserves the original context and installer receipts byte-for-byte, durably
records a handoff before replacement, changes only OperatorIdentity, carries the
completed installer fact with explicit original receipt hashes and blocks both
continuation and rollback on partial/mismatched handoff. Existing workers,
watchdog, task arguments, candidate, targets and SQL/authentication inputs remain
unchanged. The [focused handoff proof](normal-rollout-handoff-green.trx) exercises
real files and actual recovery readers; original receipts do not claim another
installer execution. The original protected files remain authoritative until
the handoff is executed; copies under `normal-rollout-original-*` retain the
pre-recovery evidence.

The one-checkpoint [continuation](continue-after-acl-verification.ps1) cannot call
an installer or reapply ACLs. Its [read-only normal check](normal-rollout-continuation-preflight.json)
matches the original completed installer, installed payload, preserved legacy
inputs, exact raw ACLs, original authentication, unchanged postmaster and absent
original actors/jobs/provisioning/tasks. Its Check mode is read-only. Continue
requires the existing recorded rollout approval and concrete independent review,
then uses the existing authentication → joined setup → creator confirmation →
activation flow and cleanup. It refuses reuse after any handoff starts. The
reviewed parent procedure hash is updated; the frozen product candidate is not.

Concrete independent review accepts this recovery and all twelve current
procedure pins. The [affected combined run](normal-rollout-continuation-combined.trx)
passes 135 checks without failures, skips or build warnings, then exits zero and
joins, satisfying the review's final condition before continuation effects.
The original approved operation is continued under that authority; no repeat
installation or ACL application is permitted.
