# Automatic current-file refresh

This focused correction follows b0211d0's current-file transport batch. An
automatic full reload after unavailable fast status could apply new recent
history while keeping old current browser entries. Another authorised request
could also warm the shared cache before the dashboard polled, hiding the
invalidation. No roadmap, schema, access policy or service privilege changes.

## Behaviour and regressions

Protected status carries one nonempty runtime cache epoch, initialised once per
shared installation runtime and rotated under the existing lock alongside cache
generation. Status captures them together and retains the generation-current
check. No additional database query is needed. The dashboard sweeps after a
changed epoch, explicit Full refresh or a cold fast-to-full fallback. Missing,
empty or contradictory paged status is unavailable, rather than silently reused.
Only an applied complete sweep advances the epoch; failed/stale/cancelled pages
and status-only save reconciliation preserve it so a later poll can retry.
Unchanged warm status avoids another complete inventory read. The epoch does
not replace transactional page generation, binding or revision checks.

The [initial actual-flow red](red.trx) reproduces both cold-fallback cases, with
and without pending edits. [Epoch actual-flow reds](epoch-app-red.trx) reproduce
hidden warmed-cache invalidation, missing/empty epochs and the absent retry; the
[core red](epoch-core-red.trx) establishes the missing runtime contract. The
[final current-flow cases](reconciliation-green.trx) pass **27/27**, including
unchanged warm status, retry after failed sweep and the actual protection-save
receipt reconciliation flow. The [core cases](core-green.trx) pass **5/5** and
prove shared request-local operations retain one epoch, invalidation changes it,
another request can warm the new cache and a fresh runtime has a new epoch.
The first focused refresh run passes 88 cases ([TRX](app-green.trx)).

The native repeat also exposed cached live browser rows hiding missing files
and folders when pending edits prevented a navigation reload. The actual
MainWindow flow with the real Windows filesystem reproduces both cases
([red](browser-red.trx)). Loaded folders and the selected folder's files now
reconcile fresh physical entries and complete validated current entries.
Unchanged objects, pending rules, typed address and selection are preserved;
missing/restored transitions and changed recovery IDs update correctly.
Independent review identified descendant expansion and selected-child loss
during parent replacement; the [nested red](nested-browser-red.trx) reproduces
that defect. Parent transfer retains loaded descendants and replaces the
default placeholder; selected-folder rebinding does not invoke navigation.
The [focused final browser/current run](browser-final-green.trx) passes 61 cases.
The subsequent full App run includes the final placeholder correction.

The [ordinary combined suites](combined) pass Core 532, App 411, Windows 141 and
ordinary Integration 163: **1,247 tests**, without failures or skips. The original
real-store configuration preservation and actual view-model save sequencing
regressions remain. Core/Windows/ordinary Integration evidence was retained
after unchanged source/dependencies; App was rerun after the browser changes.
The [final Release build](build-final.log) has zero warnings/errors.

## Native validation

The first fixture
1ea0224048894e05acb2a9a4ba45b07f correctly disproved a test assumption: an unchanged
non-purge settings save completes wholly in the catalogue and need not invalidate
content caches. Its failed result and complete teardown are retained. The
corrected native flow uses an actual backup while its generated document is
present, a separate full status to warm the changed epoch and independent pages
to obtain the post-backup current version. Only then is the document held for
the actual automatic dashboard flow. Reads are joined before exact nested
restoration; no capture occurs while the document is held. The second fixture
1c46f505993e49318084252fd10e868b failed the automatic missing-row check and led to
the real browser regressions above. Both failures and complete cleanup are
retained.

The fresh [native result](native/10f824e437dc4e01bd87e9325048ba8a/result.json) has
no failure: each loopback passes 125 catalogue, 125 metadata and ten repository
checks. Creator A passes 124 workflow/current-page checks, B is refused all 19
guarded commands and SYSTEM passes 141 product responses. The automatic probe
uses a real backup, separate cache-warming request and independent page sweep,
then verifies the actual dashboard's missing-file recovery pointer, pending
edit retention and joined shutdown. All three teardown safeguards pass and
the fixture runner joined with exit zero.

## Remaining work and rollback

The earlier bounded paging proof retains all five fixtures, 72 owned process
identities and 38 database intents removed. This batch's separate fresh
[integrity run](integrity/a47771b767a94a3ba81482cee021764c/results/PostgreSql.trx)
passes **40/40**, zero failed, skipped or aborted, with suite and runner exit
zero. Its 38 database intents are removed. The repeatable
[ownership census](check-cleanup.ps1) records all four fixture roots and
**72 exact owned process identities** absent; all journalled resources, fixture
accounts/group/task and task-attributed workers are gone. Every before/after
snapshot and the final census agree: original PostgreSQL/FluxVault service
identities, HBA/ident/configuration and unrelated primary edits are unchanged.
No other build/test/CLI worker was present at census time.

Client accumulation remains O(n);
broader scale/rendering/accessibility/lifecycle, unsent drafts, background/VSS,
full G01 and separately approved normal rollout remain open. Retain original
v1.0.4 installation/database/storage assets for existing-data recovery. No normal
installation, PostgreSQL restart, upgrade or adoption is included.
