# Implementation progress: NEXT-002 with bounded protection-save batch

Plan: [approved implementation plan](../../superpowers/plans/2026-10-03-next002-with-protection-save.md). Baseline `afdfe1f`; existing worktree `codex/next002-planning-20261003`.

The user approved execution after the model switch. Creator-only vault access with explicit Windows user/group grants is settled. One implementation owner; independent review at the specified gates. Installed service/PostgreSQL changes and rollout remain separately scoped operational actions.

## Batch C

- Resumed and inspected actual view-model/store flows and existing mirror/configuration tests.
- Fresh baseline run: nine contract cases, eight expected failures and one passing sequencing control. Output: `baseline-red.log` and `baseline-red.trx`.
- Additional test-first cases cover persistent error, retry, discard, edits and attempted profile switch during a held save, absent baseline, committed save with lost acknowledgement and failed purge. Their pre-fix failures are retained in `extended-red.*`.
- Implemented full accepted-baseline copies, typed/persistent save results, captured profile/edit generation and dispatched destructive scopes, handled cancellation/unknown acknowledgement, retained drafts, selection rebasing and dependent-backup gating.
- Independent Astra review found five missing cases. All were reproduced before correction. Bounded follow-up resolved them and found post-Options stale-baseline admission; actual modal tests reproduced and verified its correction. Dialogues keep their in-flight save owned; fresh reload or explicit discard is required before protection saving resumes after an unsuccessful Options reload.
- Final focused family: 117/117. Release solution build: zero warnings/errors. Full suite: 722/722, zero skips. Actual native rejected/cancelled flows retain edits through refresh and permit confirmed retry. Both native fixture processes exited and roots were removed; owned PostgreSQL exited and no task-owned test/host/compiler process remained.
- Final independent assessment: no remaining Batch C blocker. C01–C03 complete in the isolated branch; no merge/install/rollout performed. See `README.md` and `independent-review.md`.

## Remaining batches

S1, S2 and S3 are unimplemented. The named disposable Windows fixture and read-only preflight are prepared separately; account/task creation requires the explicit gate in the approved plan. D's three concepts and state mapping are retained; visual selection remains deferred until broader UI implementation.
