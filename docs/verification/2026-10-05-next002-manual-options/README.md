# Manual maintenance Options contract

The single-vault service composes manual commands without a maintenance loop.
Options now explains this boundary, disables its saved schedule controls and
preserves the original enabled state, automatic preference and exact interval.
Only active manual repair and rehearsal parameters are changed. No scheduler,
new configuration field, database change or normal installation change is added.

Two executable regressions failed before the correction: the actual Options
save through `FileFluxVaultConfigurationStore` changed a saved 7-hour-31-minute
interval to eight hours and cleared its automatic preference; a rendered WPF
dialogue left the automatic controls enabled. The [original results](red.trx)
retain both failures. An initial test-scaffolding compile typo was corrected
before that executable red run and is not counted as a regression.

The [focused run](green.trx) passes 19 checks. The final [App suite](app-final.trx)
passes 475 checks without skips, including the existing real-store and actual
view-model protection-save, failure/cancellation and dependent-backup tests.
Legacy source checks were aligned with the changed labels; runtime tests prove
the schedule contract. The [actual rendering](options.png) shows disabled saved
schedule controls, a visible explanation and editable manual parameters.

The complete Release solution build passes with zero warnings/errors.
Independent Astra review accepts the bounded correction, genuine regressions,
runtime checks and rendering without a blocking finding. The final read-only
[census](resource-census.json) finds no worktree build/test executable and proves
normal service identities, PostgreSQL authentication, staging configuration
and unrelated primary edits unchanged. All commands/test dispatchers were joined.

This corrects the manual-only presentation and preservation contract. It does
not complete native denied/locked-source and no-background-capture checks,
accessibility/scale validation, interrupted effects, packaged identity, final
G01 review or separately approved installation rollout.
