# Native pipe resource verification

Status: bounded S06 verification accepted by the existing independent Astra
reviewer. Based on `30f3717`; no product-runtime or installation change.
The single-vault architecture, command policy and acceptance gates are unchanged.

`NamedPipeResourceBoundaryTests` uses the actual Windows pipe factories,
authenticated server, frame implementation and native caller-token provider.
Its handler controls completion/output size to isolate transport behaviour; it
does not substitute for catalogue or repository validation.

Six native cases prove:

- Two incomplete frames occupy all configured slots, then expire without
  handler dispatch or token capture; a later valid request completes.
- Two admitted handlers hold capacity; a third waits until one finishes.
  Peak handler concurrency remains two, and queued work succeeds while the
  second original handler remains held.
- Continuous slow input cannot extend the absolute frame deadline and never
  captures a caller. A later valid request completes.
- A peer that does not read a two-MiB response, and a peer that receives a
  frame but omits its receipt, both release their native caller context while
  remaining connected. Later work succeeds.
- Shutdown joins native read/output work without requiring peer disconnect.
  The peer observes the beginning of output before cancellation.

Handlers verify actual Windows impersonation. After joined shutdown every
captured context rejects further impersonation as disposed. Client/dribble
tasks are joined, including assertion/transport-failure paths. The reviewer
required joining all three saturation clients together even when an earlier
client or server stop faults; that correction is included.

## Observed evidence

The first executable focused run passes 6/6. There was an initial test-only
compile correction for the factory's `Stream` return type and the collection
assertion analyser; that was not a product regression. The final
[Windows suite](windows-reviewed.trx) passes 147/147 without failures/skips.
The Release solution build has zero warnings/errors. No product fix was needed.

The final resource census checks for surviving worktree build/test executables,
unchanged normal service identities and configuration/authentication hashes,
and preserved unrelated primary-checkout edits. These tests create transient
pipe/token resources, not accounts, profiles, certificate trust or databases.
No PostgreSQL restart or normal installation rollout occurred.

## Independent assessment and limits

The reviewer accepts this bounded same-user native transport milestone, with
no remaining blocking correction. This is not SYSTEM/A/B isolation, packaged
identity, sustained-load fairness, whole-service shutdown or completion of
S06/G01. Those gates remain open with their existing scope; the package
registration rehearsal still awaits its separate resource approval.
