# Bounded recovery-history browsing

This S2 batch pages full recovery history through the authenticated single-vault
service and actual WPF/view-model flow. It retains failed/stale pages, loads
recorded folder contents on demand and resolves recovery to the recorded child
identity even outside the visible history page. It preserves the original
configuration fixes, ownership/grants, repository binding and verified
publication. It does not complete NEXT-002, the UI rebuild, scale/accessibility
verification or normal installation rollout.

## Contracts and recovery

ReadHistory authorises both new commands before repository work. Cursors bind
repository, canonical scope, optional kind, page size, exact UTC tick/ordinal-ID
anchor and transactional generation. Capture/pruning changes invalidate old
cursors without returning data. A repeatable-read transaction observes generation
and rows coherently. Scope filtering precedes keyset/LIMIT; escaped wildcard and
drive-root probes prevent scope broadening or omission. Cancellation while
waiting for mutation admission cannot increment generation or add a version.

SQL projects thin headers after selecting at most page-size plus one rows, caps
cumulative transferred JSON and refuses corrupt selected headers. The handler
measures the actual authenticated wire envelope using a counting stream. It
shortens pages to the smaller of the 2 MiB browsing budget and configured response
limit, anchors continuation to emitted rows, and fails explicitly for a single
oversize item. Both directions remain complete without holes. One bounded exact
manifest supplies each immutable folder page; missing/pruned children are refused
without a latest-file substitution.

Fresh staging metadata is schema v3 with a transactional history generation and
scoped tick/ordinal-ID index. Existing unsupported schemas are refused before
DDL, without backfill, automatic upgrade or data adoption. Rollback means keeping
the uninstalled branch and original v1.0.4 installation/database/storage assets
separate. Older binaries must not be pointed at the fresh staging schema.

Options exposes `repositoryBrowse.itemsPerPage` as **Items per recovery page**:
default 100, normalised 1–256. Both real-store Options routes reopen the saved
value, and protection-only saves retain it with every other untouched field.
Window closure cancels and joins reads, including child resolution; repeated
disposal awaits the same join. Stale asynchronous selection results cannot
replace current contents. An unavailable/denied initial status is handled at the
shared browser/startup/watched-folder entry point without paging or an empty
history window.

## Regression and final checks

- [Actual-flow red](app-red.trx), [Core red](core-red.trx) and
  [real PostgreSQL red](native-red/e0388cee4c27412abf5d3076eddd0f85/result.json)
  reproduce the original full-history/default unsupported paths. The private
  runner intentionally exited one and passed teardown.
- [Review reds](ui-review-red.trx), [malformed-page reds](malformed-review-red.trx)
  and [focused green](ui-review-green.trx) cover exact case-distinct IDs,
  atomic page replacement, null/malformed/overlapping pages, cursor direction,
  stale selection and shared disposal. [WPF red](wpf-first.trx) caught synchronous
  Closing re-entry; [WPF green](wpf-green.trx) verifies the queued close and actual
  minimum-window controls. The final [render](history-minimum.png) was inspected.
- [Entry-refusal red](entry-refusal-red.trx) reproduces four public browser/startup
  offline/denied failures escaping the UI action. The shared handled entry point
  passes [17 focused paging/WPF cases](entry-refusal-green.trx).
- The final [Release build](build-release.log) has **zero warnings/errors**.
  [Combined suites](combined) pass **Core 524, App 381, Windows 141, ordinary
  Integration 163: 1,209 tests**, with no failures or skips. The final App suite
  includes the entry-point correction; Core/Windows/ordinary Integration evidence
  remains valid because that correction changed only the UI entry routing.
  The changed Windows runner parses without errors. All build/test sessions were
  joined; process reuse/shared compilation were disabled.

## Native, integrity and cleanup evidence

The [metadata-only fixture](metadata-native/856ced141c3d4d14b3dc2d956791443b/result.json)
exits zero and passes 52 metadata checks per loopback. The later
[combined native fixture](native/951b719c8ef8464b8cd77f83b5da3f9c/result.json)
exits zero with no failure, passing **63 metadata checks on each IPv4/IPv6
loopback**, including size-limited SQL/wire responses and complete continuations.
The actual native pipe, catalogue, executor and inventory view-model pass creator
A's **111 checks**, ungranted B's **18 refusals** and SYSTEM's **124 product
responses**. Paged history and recorded-child recovery have independently
compared SHA-256 output bytes. Both commands deny ungranted users with valid
requests; wrong query/envelope binding is refused and stale cursors return no
page. The fixture requires the history-paging marker.

The separately approved [owned integrity fixture](integrity/985b7cd255fc4856a409191df1fd9959/result.json)
exits zero and its [TRX](integrity/985b7cd255fc4856a409191df1fd9959/results/PostgreSql.trx)
passes **40/40**, with no skips, failures or aborted tests. All 38 database intents
are Removed. Every fixture uses a fresh GUID root, private non-5432 PostgreSQL
cluster and the retained authenticated Windows binary snapshot. Normal service
and database endpoints are excluded. Every cleanup report passes root removal,
joined owned jobs and installation preservation.

The [fresh census](process-census.json) verifies all four roots and all **62 captured
owned process identities** are absent. Accounts, group, scheduled task and task
build/test/CLI processes are gone. Normal PostgreSQL/FluxVault process identities,
configuration and HBA/ident hashes are unchanged across all before/after
snapshots; unrelated primary-worktree index edits retain their original hashes.
Unrelated FluxKnowledge tests and unattributed shared compiler/MSBuild workers
were observed and preserved. Their lifetimes are not claimed as this task's
cleanup evidence; no unowned process was terminated.

Independent Astra review required the cursor/UI/native corrections and the
initial-binding correction. [Review closeout](independent-review.md) records the
bounded final verdict. Current-file inventory paging, background/VSS authority,
remaining interruption/concurrency/lifecycle/scale/accessibility checks, full
G01 and separately approved normal rollout remain open. The roadmap and
applicable acceptance gates are unchanged.
