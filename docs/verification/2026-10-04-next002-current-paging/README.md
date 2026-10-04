# Current-file inventory paging

This bounded S2 batch adds authenticated, finite current-file transport to the
single-vault dashboard. It preserves the roadmap, creator access policy, fresh
schema v4 and both configuration-save fixes. It does not complete NEXT-002 or
authorise normal installation rollout.

## Behaviour

Protected status explicitly advertises paging and omits the complete tracked
inventory without building it first. Recent-history availability remains a
separate status contract. Current pages bind the initial status's repository
identity and configuration revision, with one repeatable-read history generation
across the sweep. Ascending canonical path identities provide forward keysets;
selected pointers, paths, kind, exact ticks, deletion state and immutable binding
are checked. Header SQL and the exact authenticated envelope share the existing
2 MiB page budget. Short pages continue from their final emitted identity.

The actual dashboard validates and accumulates pages privately. Only completion
with no continuation replaces its browser inventory. Denial, cancellation,
stale generation/revision, malformed JSON, repeated identities/paths and invalid
continuations keep previous entries and pending edits. Save reconciliation does
not clear entries when status omits them. Edit/discard reviews are checked after
all page awaits. Manual, startup and automatic reads are cancelled and joined
before application shutdown without a synchronous dispatcher wait. The existing
defaulted RepositoryBrowse policy controls both history and current page counts;
Options now labels it Items per repository page.

This bounds transport only. Client accumulation remains O(n), and status/recent
history and the current sweep are separate snapshots. Scale, rendering,
accessibility and broader lifecycle/performance gates remain open.

## Regression and runtime evidence

The [actual-flow red](app-red.trx) and [real-store red](native-red/2f31a42b458c42499ebeab1cc8a65991/result.json)
fail against the missing path, with owned native teardown retained. The final
[nineteen actual-flow cases](review-green.trx) cover complete/empty/short pages,
failed and malformed sweeps, edits during an await and joined shutdown through
manual, startup and automatic entry points. The [focused core cases](core-focused.trx)
verify protected status avoids the complete current read, permission routing and
exact wire bounds/continuation. These are store/view-model calls, not source
string assertions. The retained JSON decoding red prompted a production handler
correction; the early byte-budget fixture used too small a single-row allowance
and was corrected using measured serialisation without weakening the bound.

The combined App run also reproduced a Windows file-store race between an open
configuration reader and replacement publication ([original failure](app-config-race.trx)).
A new [real-store snapshot red](config-snapshot-red.trx) reproduces it. Delete
sharing alone still failed ([intermediate attempt](config-snapshot-share-only.trx)).
The store now uses the existing atomic replacement pattern, preserves open-reader
snapshots, flushes its exclusive temporary file before publication, checks
cancellation and removes only its owned temporary path. Locked publication and
pre-cancelled writes preserve original bytes and leave no temporary file. All
[43 focused configuration checks](config-snapshot-green.trx) pass, including the
original full-record and actual save-sequencing regressions. The protected
PostgreSQL configuration route is unchanged by this supporting file-store fix.

The [final native fixture](native/19dd065afad74f68b9a20db47efb143c/result.json)
exits zero with no failure: **125 metadata and 125 catalogue checks per IPv4/IPv6
loopback**, 10 repository checks per loopback, creator A's **122 checks**, ungranted
B's **19 refusals** and SYSTEM's **136 product responses**. Real-store paging
matches independently read current entries, includes tombstones, avoids header
payload arrays, refuses changed generations/wrong pointers and traverses SQL/wire
short pages without omissions. The native actual dashboard first checks the live
working-file contract, then holds only its generated document by a unique sibling
rename, publishes its missing-file row from native pages and restores the exact
file in nested finally after joining reads. Save → backup → history → independently
verified file/folder recovery still passes.

Two unsuccessful positive fixture attempts are preserved: the
[first](native/83c8788f2afd45cd88bc2141e15bdfe2/result.json) correctly refused a probe
attempt to rewrite an immutable version, fixed by creating a new version; the
[second](native/b78c245e1ede41da9ea855aa636c14f9/result.json) exposed an incorrect
assertion that live rows expose phantom recovery IDs, fixed by the contained
missing-file check. Neither changed the corresponding production invariant.
Both complete teardown and are included in the final census.

The [Release build](build-release.log) and [final native probe build](build-native-fixture.log)
have **zero warnings/errors**. The final [ordinary suites](combined) pass Core 531,
App 400, Windows 141 and ordinary Integration 163: **1,235 tests**, no failures or
skips. All sessions are joined, with build-server reuse/shared compilation disabled.

The separate [owned integrity run](integrity/6e1edc87b88e4de6bd62f1bb9482eb51/result.json)
exits zero. Its [TRX](integrity/6e1edc87b88e4de6bd62f1bb9482eb51/results/PostgreSql.trx)
passes **40/40**, without failures, skips or aborted tests. All 38 generated
database intents are Removed. The [census](process-census.json), reproducible with
[check-cleanup.ps1](check-cleanup.ps1), confirms all five roots, accounts, group,
task, **72 captured owned process identities** and task build/test/CLI workers
are gone. No out-of-scope workers remained; none were terminated. The original
PostgreSQL/FluxVault service identities and HBA/ident/configuration hashes, all
installation snapshots and unrelated primary-checkout index edits are unchanged.
No normal rollout, restart, adoption or upgrade is included.

[Independent review](independent-review.md) accepted the complete bounded diff,
regressions, native/integrity results and final cleanup evidence with no blocker.

## Rollback and remaining scope

Retain the uninstalled branch and original v1.0.4 installation/database/storage
assets. Older binaries must not use the fresh schema. Remaining background/VSS
authority, unsent drafts/other interruption, concurrency/lifecycle, scale and
accessibility, full G01 review and separately approved rollout gates remain open.
