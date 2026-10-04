# Single-vault execution milestone

Scope follows the user's 4 October 2026 decision: exactly one logical vault per Windows installation. Multiple protected folders, file types and mirrors remain. Earlier multi-vault results are historical evidence; no dedicated multi-vault validation was added.

The protected bootstrap, transactional singleton catalogue, authenticated handler and Windows executor now compose the actual service path. One mutation gate serialises admission through durable receipts. Immutable repository/metadata binding, creator ownership, explicit grants and configuration revision checks remain. The product UI has no profile or vault switching/creation/discovery controls.

## Observed evidence

- [Native product composition](single-product-composition-native/result.json), fixture `8bc77c942acb4bfdb1f7a0410a70b0d3`: creator A saved two protected folders and file types, captured three files, queried history, and recovered a file and nested folder. Independent SHA-256 checks verified published outputs. Ungranted B could not obtain identity or history. Wrong binding, stale revision and replay checks passed. Both loopback database paths retained admission/denial checks. [Cleanup](single-product-composition-native/cleanup.json) confirms all owned jobs joined, the fixture root removed and the normal installation unchanged.
- Fresh zero-warning, zero-error Release solution build; [Core](single-reviewed-combined/Core.trx) 434, [App](single-reviewed-combined/App.trx) 241, [Windows](single-reviewed-combined/Windows.trx) 97 and [ordinary Integration](single-reviewed-combined/Integration.trx) 157 passed, with no skips: **1,029 tests**. PostgreSQL-dependent tests were deliberately excluded from this ordinary run, not reported as passing.
- Real-store and actual view-model regressions retain untouched settings, pending edits and dependent-backup gating. Added cases cover stale Options sessions, wrong acknowledgement identity/revision, lost acknowledgements, newer drafts, backup receipt reconciliation and pipe access refusal. See retained red results in `ack-revision-red`, `backup-ack-red`, `options-binding-red`, `pipe-access-refusal-red` and `receipt-access-red`, followed by the combined green results above.
- Protected file/root tests cover inherited untrusted grants, ancestor/file identity, ownership, links and denied write/delete sharing. Bootstrap contract failures are retained in `installation-contract-red`; the final Windows suite includes the corrected cases.
- Actual Worker shutdown regression failed before the fix in [worker-join-red](worker-join-red) and passed in [worker-join-green](worker-join-green). Shutdown joins execution even after the host deadline, before disposing catalogue/executor/bootstrap resources.
- Independent Astra security review approved the bounded composition/UI scope and resolved its shutdown and access-refusal blockers using the current diff and results.

## Limits and remaining gates

This is an uninstalled, unmerged execution milestone, not completion of NEXT-002 or release approval. The service truthfully reports automatic protection unavailable while manual backup is available; no legacy privileged background loop is registered. Remaining commands, owner-session background capture, durable client restart reconciliation, applicable native/accessibility/performance cases and full G01 remain open.

The diagnostic `single-composition-combined` run correctly failed 37 PostgreSQL-dependent tests because no owned cluster was supplied. Its shared TRX filename also overwrote earlier project results. It is retained as failure evidence and is not the combined green result. The old elevated trust/unowned-process runner must not be used. The safe full-suite adaptation requires separately reviewed, scoped disposable database resources before execution.

Normal FluxVault/PostgreSQL services and staging data remain untouched. Installation rollout requires the exact target, data-preserving rollback, independent review and the user's separate operational approval. PostgreSQL must not be restarted.
