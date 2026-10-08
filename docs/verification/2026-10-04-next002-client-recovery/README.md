# Backup outcome recovery after dashboard restart

The product dashboard now writes one per-user pending-backup record before IPC dispatch. It contains only the installed repository identity, operation ID and original configuration revision. Reopening loads that record after verified service status; a second backup is refused until the original outcome is confirmed. This is the bounded backup portion of S2. Save-draft persistence and remaining interruption/command gates are still open.

The file store flushes creation before dispatch and holds an exclusive shared file gate across read, reservation and conditional removal. Separate Windows sessions cannot overwrite a pending operation or clear a newer operation. Corrupt, inaccessible, busy and wrong-binding records are preserved and explained; they prevent dispatch. Missing/denied/unavailable receipts remain uncertain. A matching terminal failure with the executor's unsuccessful backup summary is a confirmed failure, not unavailable receipt retrieval. Failure to clear the record also prevents retry.

## Evidence

- The actual file-store cases [failed before implementation](store-red/store-red.trx), then [passed](store-green/store-green.trx).
- Six actual-dashboard cases [failed before wiring](restart-flow-red/restart-flow-red.trx). They exercise both production configuration-store routes, reopening the dashboard, inspecting the durable record at the IPC dispatch boundary, wrong repository/revision refusal and corrupt/busy record refusal with pending edits retained.
- The first wired run [exposed unhandled corrupt-record parsing](backup-restart-green/backup-restart-green.trx). The exception is now handled without dispatch or data loss.
- Independent review found that the real executor's completed failed backup has `ErrorCode=Unavailable` plus an unsuccessful backup summary. Immediate and reopened-dashboard cases [failed before correction](terminal-failure-red/terminal-failure-red.trx), then passed with refusal cases retained.
- The [final focused run](backup-restart-final/backup-restart-final.trx) passed 79 tests without skips. It also verifies conditional-clear failure, later configuration revisions and a newer operation created by another session.
- The [final App suite](app-final/app-final.trx) passed 263 tests without skips. The Release solution build passed with zero warnings/errors. All launched test/build processes exited.

Independent Astra review approved this bounded implementation after the terminal-failure correction. No normal installation deployment, database change or service restart was performed for this batch.

Crash before dispatch can leave a record without a service receipt. Absence of a receipt does not prove safe retry: the record remains blocked with an honest uncertainty message. This batch does not introduce an automatic replay or reset path.
