# NEXT-002 planning evidence

Baseline: `afdfe1f527ab7f0bef54c816a7ce858c36008444`. Worktree: `codex/next002-planning-20261003`. Runtime source is unchanged. This record supports the [next-slice plan](../../superpowers/plans/2026-10-03-next002-with-protection-save.md), not a claim that the defects are fixed.

## Configuration regression results

New test source: [ProtectionSaveContractTests](../../../tests/FluxVault.App.Tests/ProtectionSaveContractTests.cs).

| Case | Observed baseline result |
| --- | --- |
| VM selection save → FileFluxVaultConfigurationStore → reopen | Fails: IsEnabled, RepositoryMaintenancePolicy, Sync, MetadataStore and DiagnosticsPolicy changed unexpectedly |
| VM selection save → FluxVaultProfileConfigurationStore + FileFluxVaultProfileSetStore → reopen | Fails with the same five changed fields |
| Rejected save response | Fails: RunBackupNow was dispatched after SaveConfiguration |
| IO, timeout and access-denied save failures | Three failures: each dispatched RunBackupNow |
| Save task cancellation | Fails: OperationCanceledException escapes the view-model command |
| Cancelled removal confirmation | Fails: no save sent, but RunBackupNow was dispatched |
| Save held until persistence acknowledgement | Passes: no early backup; save persisted before exactly one backup |

Totals: **9 cases, 8 expected failures, 1 pass, 0 skips**. [Run output](configuration-red.log), [TRX](configuration-red.trx). A test-authoring analyser issue was corrected before this run; compilation errors are not counted as reproduced defects.

The transport fixture records actual commands, delegates configuration writes/reads to the production stores and never contacts the normal service or a database. It uses unique temporary roots and deletes only its own root in `Dispose`. The failure cases check unchanged disk bytes and retained edits before the failing command-sequence/result assertion. The round-trip test structurally compares every untouched configuration property, not source text or a hard-coded list of just the five failures. The success control uses an explicit asynchronous barrier, not timing sleeps.

The new cancellation/failure assertions deliberately remain red until batch C is implemented. They are not skipped and must not be merged alone. Failure result text still needs additional persistent-state/native checks in C, as listed in the plan.

## Existing-test control

The existing App suite, explicitly excluding only the newly added red contract class, passed: **187 passed, 0 failed, 0 skipped**. [Run output](existing-app-tests.log), [TRX](existing-app-tests.trx). This is a control demonstrating no existing App regression from adding the tests; it is not reported as a passing full solution or passing combined App suite.

## Scope and remaining proof

No production code, overall roadmap, architecture document, installed application, normal database, authentication rules or service state was changed. Security findings are current-source observations; no live exploit or security-isolation test was run. Three generated UI images are exploratory concepts with explicit corrections in their design note.

[Independent Astra review](independent-design-review.md) approved the planning proposal without blocking corrections. Its pending product decision was subsequently resolved by the user accepting creator-only access with explicit Windows user/group grants. The review record retains the original assessment and a dated decision addendum. Implementation is paused for the user's model switch.

[Document/source checks](planning-checks.json) confirm intact local links, clean authored whitespace and unchanged runtime/roadmap scope. [Process cleanup](process-cleanup.json) found zero task-owned test/build processes and zero remaining fixture roots. Tests use the existing Windows toolchain with shared compilation and build-server reuse disabled. The access-decision follow-up changed planning documents only; test results above are retained evidence, not new runs.
