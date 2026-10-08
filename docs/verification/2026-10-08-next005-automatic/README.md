# NEXT-005 signed-in automatic protection execution

The owner resumed the [agreed plan](../../superpowers/plans/2026-10-08-next005-signed-in-automatic-protection.md).
NEXT-004 is independently installed/live-accepted as 1.0.9 and isolated in
[PR #35](https://github.com/yagasoft/FluxVault/pull/35); its hosted CI/integration
is being completed without adding NEXT-005 changes to that branch. Work reuses
the same dedicated task worktree on `codex/next005-signed-in-protection`.

## First automatic round trip

In progress. No NEXT-005 capability is deployed or claimed. The first batch must
join creator-session authority, additive default-off consent/work persistence,
real discovery admission, reserved capture identity, verified publication,
Options/status and independently verified recovery. Existing mutation ordering,
source access, repository leases and native/database fixtures are the boundaries.
No logged-off/reboot, disaster-recovery, VSS or application-consistency claim is
added. Normal PostgreSQL and unrelated data/changes remain untouched.

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
