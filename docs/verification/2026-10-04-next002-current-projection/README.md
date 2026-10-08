# Canonical current-entry correctness

This bounded prerequisite for current-file paging fixes demonstrated metadata
errors in the existing single-vault store. It does not complete NEXT-002 or
change its roadmap, security/access policy or remaining acceptance gates.

## Behaviour and recovery

Fresh metadata schema v4 has one current entry per existing canonical Windows
path and entry kind. Exact UTC ticks and ordinal hexadecimal version IDs select
the winner independently of insertion/replay order; winning manifests supply
display casing and deletion state. Latest-by-path lookup uses the same identity
and ordering. Deleting a current version restores the newest retained version,
including a tombstone, or removes the current row only when no history remains.
Deletion rebuild, chunk retirement and history generation share the existing
advisory-locked transaction. Version deletion retains case-distinct identifiers.

Current readers validate the referenced path, version, exact tick, kind,
deletion state, immutable manifest and repository binding. Cached bindings and
the production schema guard refuse old or weakened shapes before adoption/DDL.
The owned probes confirm unchanged data/schema after refusal. No migration or
automatic upgrade is implemented. Rollback preserves the uninstalled branch and
the original v1.0.4 installation/database/storage for existing-data recovery;
older binaries must not use the fresh schema.

## Meaningful regression evidence

The [real-store red](native-red/448c36c4ac964cceac05328cf6c83141/result.json)
reproduces ten failures: 100 ns ordering/latest lookup, case-equivalent path
duplication and lookup, replay/display casing, retained live/tombstone fallback,
wrong-path pointers and case-distinct deletion. The runner intentionally exits
one and still completes teardown. These are executable production store calls,
not source-string assertions. The prior schema smoke assertion is mechanically
updated to the new canonical primary key; its [obsolete expectation](core-schema-red.trx)
is retained separately from the behavioural red.

The [first positive metadata fixture](metadata-native/b8a521c6476345078bbb1d976ea51f76/result.json)
passes 80 checks on each loopback, including injected recording and deletion
rebuild failures that leave history/current pointers/generation unchanged.
The [expanded combined fixture](native/fc3592d8cc9c4cb6bc50d1e0526117f6/result.json)
exits zero with no failure and passes **93 metadata checks on each IPv4/IPv6
loopback**. It also passes 125 catalogue and 10 repository checks per loopback,
creator A's 111 native checks, ungranted B's 18 refusals and SYSTEM's 124 product
responses. The actual save → backup → history → verified file/folder recovery
workflow and paged recorded-child flow pass with independently compared bytes.
The fixture requires `CurrentProjectionVerified` and `HistoryPagingVerified`.

The separate [owned integrity run](integrity/97a3bdb6942944d0995328279aac5d9e/result.json)
exits zero; its [TRX](integrity/97a3bdb6942944d0995328279aac5d9e/results/PostgreSql.trx)
passes **40/40**, with no failures, skips or aborted tests. The final
[Release build](build-release.log) has **zero warnings/errors**. The
[ordinary suites](combined) pass Core 524, App 381, Windows 141 and ordinary
Integration 163: **1,209 tests**, with no failures or skips. The original
configuration preservation and actual view-model save sequencing regressions
remain in these suites. The changed Windows runner parses without errors.

## Cleanup and remaining work

All four private GUID fixtures use the retained authenticated native PostgreSQL
snapshot and non-5432 endpoints. Their cleanup reports verify owned jobs joined,
root removal and installation preservation. The [fresh census](process-census.json),
reproducible with [check-cleanup.ps1](check-cleanup.ps1), confirms **62 captured
owned process identities**, all roots/accounts/group/task and 38 test database
intents are gone. All build/test sessions were joined with worker reuse and
shared compilation disabled. The installed PostgreSQL/FluxVault service
identities, HBA/ident/configuration hashes and unrelated primary-worktree index
edits are unchanged. No out-of-scope workers remained at census time; none were
terminated by this task.

[Independent review](independent-review.md) records bounded closeout. Current-file
inventory transport paging, background/VSS authority, remaining interruption,
concurrency, lifecycle, scale/accessibility and full G01 review are still open.
Normal installation rollout requires its separately reviewed target, rollback,
probes and user approval. This fixture approval does not authorise that rollout.
