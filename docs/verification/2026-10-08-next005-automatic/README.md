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

Not yet available. The 11 October staging decision below removes the environment
acquisition block; implementation can continue under the existing authority.
No NEXT-005 capability is deployed or claimed. The first batch must
join creator-session authority, additive default-off consent/work persistence,
real discovery admission, reserved capture identity, verified publication,
Options/status and independently verified recovery. Existing mutation ordering,
source access, repository leases and native/database fixtures are the boundaries.
No logged-off/reboot, disaster-recovery, VSS or application-consistency claim is
added. Normal PostgreSQL and unrelated data/changes remain untouched.

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
