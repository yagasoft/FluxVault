# Independent catalogue and dispatcher review

Reviewer: existing independent Astra `service_security_review`, 4 October 2026. Source review only; the reviewer did not run the reported tests or approve operational changes.

PostgreSQL was approved as the least complicated adequate authoritative catalogue. Protected bootstrap, fixed qualified control schema, service-only access, coherent admission/revision transactions, synchronous WAL acknowledgement and explicit unknown-outcome reconciliation were required. No stale policy cache may authorise commands.

The implementation review required three corrections: reject catalogue mutations through generic admission; persist and recheck effective request-specific permissions on receipts; authorise receipt envelopes before fetching/decoding response payloads. Discovery was changed to bounded keyset pages of authorised summaries. Real-store regressions cover the corrected cases.

A subsequent dispatcher review required phase-aware error reporting. Three new regressions reproduced misleading read messages and mutation exceptions reported as invalid requests or escaping without a typed outcome. The corrected dispatcher conservatively reports unknown mutation outcomes after possible effects and avoids claiming that a read never started after execution began.

Final source verdict: **approved for this bounded catalogue/dispatcher milestone, no required corrections remain**. The reviewer explicitly excluded whole NEXT-002, native source/output/background access, runtime namespace isolation and deployment. These gates remain open.
