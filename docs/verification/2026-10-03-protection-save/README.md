# Protection-save verification

3 October 2026. Batch C of the [approved plan](../../superpowers/plans/2026-10-03-next002-with-protection-save.md), based on `afdfe1f`. Verified in the isolated `codex/next002-planning-20261003` worktree; no installed application/service or normal PostgreSQL change.

## Acceptance results

| Gate | Observed result | Evidence |
| --- | --- | --- |
| C01: preserve untouched settings | Passed through actual VM commands, both production file-store routes and reopened disk state. Structural comparison covers all untouched properties, with non-default metadata, maintenance, sync, diagnostics and disabled protection. | `ProtectionSaveContractTests.Saving_protection_selection_preserves_every_untouched_setting_through_real_store`; final TRXs |
| C02: unsuccessful save | Rejected response, IO, timeout, access denial, task cancellation and cancelled removal confirmation keep draft and send no dependent backup. A store commit followed by lost acknowledgement stays Unknown. Error/cancel messages survive refresh. Destructive uncertainty cannot be blindly replayed. | Contract tests; [initial red](baseline-red.log), [extended red](extended-red.log), [review red](review-red.log) |
| C03: acknowledged sequencing | Async save barrier proves no early backup; successful persistence precedes exactly one explicitly targeted backup. Newer edits remain pending against the acknowledged baseline and block dependent backup. Failed/skipped/cancelled discard retains edits. Options and Explorer use actual VM/WPF flow tests. | [review red](review-red.log), [discard edit red](discard-edit-red.log), [modal Options red](options-session-red.log), [status/Options red](status-options-red.log), [Options close red](options-close-red.log), [owned Options save red](options-owned-save-red.log), [Options message red](options-reload-message-red.log), [focused green](options-reconciliation-final.log) |
| Current visible failure flow | Actual native MainWindow: rejected/cancelled save, retained Added row, refresh, retry success. Rejected trace has no backup before acknowledgement and one after a later explicit action; cancelled trace has none. | [rejected fixture](native-rejected), [cancelled fixture](native-cancelled) |
| Combined regression and cleanup | Release solution build: **0 warnings/errors**. Full suite: **722 passed, 0 failed, 0 skipped** (339 Core, 17 Windows, 222 App, 144 Integration). Temporary PostgreSQL stopped; no task-owned test/host/compiler processes remained. | [build](release-complete.log), [suite](full-suite-complete.log), [TRXs](combined-complete/results), [cleanup](combined-complete/cleanup.json) |

The native fixture uses production configuration stores and the actual MainWindow/view model with controlled service responses. Its backup result is command acceptance, not captured-file evidence. It contacts neither the installed service nor a database. Native observations precede review corrections; additional paths are covered by actual WPF/VM tests in the final suite. Security and broader UI acceptance are not claimed.

## Test-first evidence

Fresh unfixed baseline: eight failures and one passing sequencing control. Both real-store round trips exposed precisely the five reported configuration resets. Extended cases, visible binding, status reconciliation and review cases were added before each corresponding fix. Assertions were not weakened; an older removal test was updated to require the newly agreed retained review draft after partial purge failure.

## Review and boundaries

The initial independent Astra review found five blocking cases: selection-baseline rebase, dispatched destructive scopes, Options entry guards, Explorer Show versions overwriting a draft, and prematurely cleared discard result. Each was reproduced and corrected. A bounded correction review resolved all five and found the related post-Options reload window. That was reproduced and corrected, including held/failed reloads, retained intervening edits, successful reconciliation and owning the save until acknowledgement. [Independent review record](independent-review.md).

S01–S08 remain unproved for NEXT-002. Full NEXT-004 remains open. Overall roadmap, architecture, vault support, security and isolation gates are unchanged. No rollout, installed HBA edit, migration or service/PostgreSQL restart is included.
