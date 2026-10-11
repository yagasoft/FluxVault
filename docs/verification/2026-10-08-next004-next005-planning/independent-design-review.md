# NEXT-004 and NEXT-005 independent design review

**Date:** 8 October 2026. **Outcome:** approved approach after one bounded correction. **Reviewer:** independent GPT-6 Astra, high reasoning, `review_protection_design` in the planning session. This is technical design acceptance only; implementation, migration, candidate and installed acceptance gates remain pending.

## Scope

- [NEXT-004 design and implementation plan](../../superpowers/plans/2026-10-08-next004-protection-controls.md): explicit state commands, preserved configuration/drafts/receipts, stopping without purge, independently previewed deletion and per-slice rollback.
- [NEXT-005 design and implementation plan](../../superpowers/plans/2026-10-08-next005-signed-in-automatic-protection.md): creator-session authority and source consent, durable discovery/admission/publication, supervised shutdown, additive migration and rollback.

The review inspected relevant current source at the accepted `1840621` baseline. It reused NEXT-002 evidence and did not repeat the security audit, run tests, start applications, change code or operate databases/services. NEXT-004 was accepted on the first review; NEXT-005 required the compatibility correction below. The reviewer then checked only that correction and the two accompanying notes and returned **Approved approach**, with no remaining blocker in the reviewed changes.

## Finding resolved

`PostgreSqlVaultCatalogue` rejects unknown configuration JSON properties and pins control identity schema version 1. A new optional `AutomaticProtectionPolicy` property in the stored base configuration would therefore prevent NEXT-004 binaries from opening the vault after rollback.

The NEXT-005 plan now stores typed policy/consent/work in separate, independently versioned additive extension tables while preserving the predecessor's base JSON and identity. Rollback must durably quiesce work and invalidate automatic consent before replacing the binary. Old-binary reads/saves preserve extension records. Re-upgrade preserves pending work but requires fresh creator approval of the current source scope. The mandatory native migration scenario includes legacy scope edits, interrupted migration/rollback, preserved history/receipts and verified recovery through each stage. No queue or database rollback may erase later accepted history.

## Incorporated implementation notes

- The existing request fingerprint hashes the entire serialised request. Compatibility applies to every affected historical request, including old combined save/purge receipts, not only pause.
- The existing purge dependency closure can cross the requested path. The new standalone deletion must refuse outside-scope dependent versions, including unprotected ones, and prove retained recovery. Discovery of a dependency is not deletion authority.

The reviewer accepted the explicit pause and acknowledgement rules, non-purging deselection, desktop-independent creator authority without privileged fallback, consent invalidation, durable cursor/work ordering, dirty generations, commit-loss reconciliation, joined shutdown and separation of roadmap slices. Source stability checks retain best-effort consistency semantics; they do not claim application consistency.

## Planning handoff boundary

The owner explicitly requested a pause before implementation for a model switch. That pause remains in force despite design acceptance. After explicit resume, the recorded standing authority covers implementation, installation/corrective redeployment and self-operated validation within each agreed slice. Independent candidate review, checked rollback, preservation, live acceptance and joined cleanup still apply. NEXT-006 remains the following priority; NEXT-007 is bounded design exploration, NEXT-009 supplies measurement requirements and NEXT-010 retains VSS/professional consistency. Theme/accessibility work remains deferred.

## Planning validation by the task owner

Five changed Markdown documents were checked: 71 local file links resolve, and the whitespace check passes. No product tests were run for these documentation-only changes. Final read-only observations preserve the primary checkout's two index-script hashes, FluxVault PID 10384/start time and PostgreSQL postmaster PID 10660/start time. All shell/tool sessions launched for this planning pass returned completed; no temporary product, build, test or database processes were launched. Existing acceptance is reused within the limits stated in the plans, not represented as a fresh runtime pass.

## Staging verification revision — 11 October 2026

The owner directs: “Remove VMs from the design/plan; we are still in staging and
the data is disposable.” This supersedes the later environment acquisition
proposal and its pending approval. The original planning/model-switch pause above
is historical: the owner already resumed, and standing implementation/staging
rollout authority remains in force within the agreed slices.

Independent GPT-6-Astra/high reviewer `review_protection_design` returns
**Accepted approach** for proceeding on this Windows staging PC with the real
service/WPF/PostgreSQL interfaces, existing owned fixtures and disposable
FluxVault data. No virtual machine, hypervisor, evaluation image or additional
Windows environment is required to continue implementation. Reuse the completed
foundation review and evidence; no further architecture or tracking framework is
introduced.

The accepted verification route combines real WTS token acquisition and
SID/session/AuthenticationId checks against the current creator session with
explicitly labelled injected authority-loss/replacement barriers. The latter must
prove stopped admission/publication, retained work, joined readers and stale
generation rejection; they do not establish an actual Windows sign-out. Existing
owned PostgreSQL/process fixtures cover interruption, takeover, reconciliation
and migration/rollback. Independent candidate review and joined cleanup remain.

The reviewer retains one specific acceptance limitation: actual native
sign-out/re-entry is still unproved and remains a candidate check. Disposable
FluxVault data does not authorise operator sign-out, Windows reboot, unrelated
database changes or restarting normal PostgreSQL. Any required host disruption
must be made concrete and separately authorised when reached; it is not a blanket
implementation block or a reason to recreate the removed VM requirement.

This is a planning correction. It does not rerun, invalidate or expand previously
completed tests, and it does not establish new runtime or live-acceptance evidence.
Final independent review of the six-document diff returns **Approved plan
correction**, with no material mismatch. All 80 local file links resolve and the
whitespace check passes. No product build, test, installation or service action
was performed for this documentation change.
