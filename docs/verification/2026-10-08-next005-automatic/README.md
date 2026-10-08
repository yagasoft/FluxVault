# NEXT-005 signed-in automatic protection execution

The owner resumed the [agreed plan](../../superpowers/plans/2026-10-08-next005-signed-in-automatic-protection.md).
NEXT-004 is independently installed/live-accepted as 1.0.9 and isolated in
[PR #35](https://github.com/yagasoft/FluxVault/pull/35), squash-merged at
`41222b625b772e53f02972f76674440713d3884b`. Fresh hosted CI
[37796903281](https://github.com/yagasoft/FluxVault/actions/runs/37796903281)
passed at exact reviewed head `ef60231ead15a9d2141fa80753a816b86577db42`.
The test-only hosted-fixture correction has separate independent Astra/high
approval; it did not reopen unchanged installed/product gates. Work reuses
the same dedicated task worktree on `codex/next005-signed-in-protection`.

## First automatic round trip

Not yet available: the native authority environment gate below remains open.
No NEXT-005 capability is deployed or claimed. The first batch must
join creator-session authority, additive default-off consent/work persistence,
real discovery admission, reserved capture identity, verified publication,
Options/status and independently verified recovery. Existing mutation ordering,
source access, repository leases and native/database fixtures are the boundaries.
No logged-off/reboot, disaster-recovery, VSS or application-consistency claim is
added. Normal PostgreSQL and unrelated data/changes remain untouched.

## Native authority gate

Independent GPT-6-Astra/high assessment (`review_protection_design`, 8 October)
finds a precise environment blocker: the existing A/B token fixture cannot prove
real WTS sign-out/session replacement while preserving the operator's session.
Native `LogonUser`/`LsaLogonUser` tokens are not an independently owned WTS desktop
session. Injected coordinator transitions remain useful tests but do not replace
actual WTS revocation. The [WTS token contract](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken)
requires an actual logged-on session and service-owned token closure.

`authority-environment-inventory.json` records the read-only host inspection:
no Hyper-V/VMware/VirtualBox service or tool, all Hyper-V/Sandbox/VM platform
features disabled, and only the operator's console session observed. The owner
has been asked for the material additional environment decision: a bounded
disposable Windows software VM with an official evaluation image (at most 40 GB
disk/6 GB RAM, no host reboot), another existing isolated interactive Windows
environment, or pausing NEXT-005 at this gate. No VM is provisioned and no host
virtualisation feature is changed while that decision is pending. Automatic
runtime exposure/deployment remains blocked; safe unexposed prerequisites may
continue. This does not request testing assistance from the owner.

Required native proof retains the service's duplicated token across sign-out of
the owned session, proves invalidation blocks new source access/publication and
retains pending work, then joins readers before token disposal. A new creator
logon must have a new AuthenticationId; replacement starts after the old
generation joins, even if Windows reuses the numeric session ID. The review
does not approve a different source-authority model or waive this gate.

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
