# Definite refusal before admission

Inspection of the remaining command surface reproduced a client interruption defect: an unsupported save was refused before admission, but returned `Unavailable`. Protect and Options correctly treated that generic code as uncertain and retained a pending-operation record even though this attempt had never reached the catalogue. The user could not safely retry or discard.

The capability-refusal branch now returns `InvalidRequest` and explains that no change was started **by this request**. Existing definite-rejection handling conditionally clears only the matching freshly reserved record. Both actual view-model flows retain pending edits, the real stores remain unchanged, and dependent backup is not sent. Admission outages, exceptions after admission/execution and genuinely unknown outcomes retain their existing conservative handling. Historical receipt lookup remains before capability refusal, and uncertain destructive effects are not replayed.

This batch enables no unsupported command, changes no vault ownership/binding rule and creates no new runtime abstraction or configuration option. It fits within S2's command-policy/interruption work; unsent drafts, remaining authorised command/background paths and final gates stay open. Rollback is reverting this bounded source change before release; no installed service/database migration is involved.

## Evidence

- All four [dispatcher cases failed](core-red/Core.trx) before the fix. All four [actual VM/real-store cases failed](app-red/App.trx), covering Protect and Options through both production store routes.
- The [final combined ordinary run](combined-final/Core.trx) passed **1,042 tests**, zero failed/skipped: Core **448**, [App **328**](combined-final/App.trx), [Windows **103**](combined-final/Windows.trx), [Integration **163**](combined-final/Integration.trx). Integration uses `Category!=RequiresPostgreSql`; the private native database suite remains a separate gate. Core includes completed/admitted historical receipt lookup with unsupported capability; both return their original outcome without executor work.
- The [final Release solution build](build-release-final.log) passed with **zero warnings/errors**.
- [Independent Astra review](independent-review.md) approved the bounded correction. [Process census](process-census.json) verifies launched build/test/CLI processes have exited and normal service/PostgreSQL process starts, configuration hashes and unrelated primary-checkout edits remain unchanged.

The earlier 1,040-case combined run is retained; the final run includes two additional historical receipt regressions and the reviewed wording refinement. No normal rollout or PostgreSQL restart occurred.
