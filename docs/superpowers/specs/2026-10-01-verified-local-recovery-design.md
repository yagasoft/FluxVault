# Verified local recovery: first integrity slice

**Status:** architecture approved on 1 October 2026; implementation authorised and in progress.

**Design review:** independent Astra integrity/concurrency review approved the approach on 1 October 2026 after clarifying cumulative graph limits and destination-appearance preservation. No remaining design blockers were reported in the reviewed scope. This is architecture approval; implementation and runtime acceptance evidence remain pending.

**Baseline:** b930ed5, with the review and roadmap documentation in `codex/product-review-roadmap-20261001`. [Review](../../reviews/2026-10-01/review.md), [evidence](../../reviews/2026-10-01/evidence/README.md), [roadmap](../../improvement-roadmap.md), [implementation plan](../plans/2026-10-01-verified-local-recovery.md).

## Intended outcome and size

FluxVault is intended for professionals protecting Office, CAD, Adobe and other large working files. The owner prioritises correctness, easier use, modern UI and performance. Windows-only, local-first core operation and staging compatibility freedom are settled decisions. This slice establishes one necessary part of trust: a repository operation must not silently accept corrupt content or remove its last required healthy copy.

The first observable result is a disposable file captured through the CLI, listed, restored and independently hash-checked; after deliberate stored-byte corruption, restore must fail without changing an existing destination. Subsequent batches extend the same invariant through deduplication, mirror maintenance, PostgreSQL, IPC and the existing UI's result handling. Six coherent implementation batches are planned, with an executable checkpoint after the first two. This is a substantial repository capability, not the whole rebuild.

**Mapping:** implement the content-integrity portion of NEXT-003 and the relevant baseline work in NEXT-001; bring forward only the repository coordination from NEXT-005 needed to protect these operations. Address F02/F03/F04, plus the affected manifest/codec/coordination parts of F09/F10. NEXT-002, the rest of NEXT-003/005, and the full Phase 1 gate remain incomplete.

## Scope and limits

In scope: bounded verified chunk reads; validated manifest traversal; safe single-file and single-folder-version publication; verified preview/rehearsal; fail-closed deduplication; flush/validate-before-reference ordering; verified repair and mirror drain/rebalance; physical storage ownership and cooperating-process leases; truthful results through existing CLI/IPC/UI; real isolated PostgreSQL tests.

Out of scope: LocalSystem authorisation, metadata vault namespaces, configuration reset/lost-update fixes, USN scheduling, service lifecycle redesign, portable disaster recovery, power-loss certification, live application/VSS consistency, full metadata/ACL/ADS fidelity, existing-folder merge/transactional multi-selection restore, UI reskin/navigation, cloud, sync and new drivers. These remain explicit roadmap blockers. Finishing this slice does not make unrestricted use with real files safe.

The verified support envelope is fresh disposable repositories on local Windows NTFS, fresh dedicated metadata databases, non-overlapping owned roots and ordinary file/folder destinations. Slow/offline/full mirrors are tested failure cases. UNC, cloud synchronisation races, hostile processes modifying paths, cross-vault metadata sharing and physical power failure are not certified. Reject unsupported/reparse storage roots explicitly; do not silently weaken verification.

## Approaches considered

| Approach | Trade-off | Decision |
| --- | --- | --- |
| Add a hash check only in restore | Small, but leaves false dedup success, destructive mirror behaviour and separate inconsistent validators | Insufficient for this slice. |
| Shared verification, conservative publication and coarse storage coordination | Reuses current format and algorithms; serialises content-sensitive work and rejects ambiguous damage | Selected. It closes the reproduced failure family with repeatable evidence. |
| New single-object chunk format plus new vault schema | Cleaner eventual publication/identity model, but expands immediately into schema, recovery and every caller | Defer to a separately justified format decision; staging permits it later. |

Retain payload plus JSON sidecar and the existing `ManifestChunk` descriptor. Existing acknowledged descriptors are immutable: digest, decoded length, stored length and encoding. A newly selected compression policy must not re-encode an existing digest and invalidate older versions. No silent migration, repository reset or deletion is part of this work.

## Invariants and commit points

| ID | Required behaviour |
| --- | --- |
| I1 | Success from restore means the exact bytes written were verified against the selected manifest's chunk digests, lengths and layout. |
| I2 | Before destination publication, any integrity, IO, cancellation or validation failure leaves an existing file byte-identical and a new destination absent. |
| I3 | Commit publishes no version referencing an object that failed validation. Existing damaged/partial objects never count as dedup hits. |
| I4 | Repairs preserve the acknowledged descriptor; conflicting descriptors for one digest block repair and deletion. |
| I5 | Drain/rebalance deletes a copy only after verifying a healthy primary and the required surviving mirror copies while holding their leases. |
| I6 | All cooperating content-sensitive operations use the same physical-root lease protocol. Retention cannot remove content while restore uses it. |
| I7 | Verification consumes bounded inputs and verifies the same byte stream later published or copied. Cancellation before publication remains cancellation. |
| I8 | Returned health/success reflects what happened. Failure to write lineage information after a verified restore cannot be reported as an ordinary failed restore. |

For capture, the existing metadata transaction or legacy manifest publication is the version commit point; chunk objects must be flushed and verified first. A failure after a database transaction may have committed but before the response is observed has an unknown acknowledgement outcome, not a guarantee that nothing was captured. Never delete potentially committed objects to compensate. Invalidate the in-memory mutation cache on any failed publication.

For restore, the same-volume final file replacement or non-overwriting directory move is the publication point. Check cancellation immediately before it. After publication, return verified completion; a failed/cancelled restore-hint write is a warning. A lost IPC response does not permit a claim that the destination was unchanged. This slice does not add durable operation IDs to resolve lost acknowledgements.

Process-kill tests prove recoverability of interrupted application operations, not power-loss durability or storage-controller behaviour.

## 1. Storage ownership and operation leases

Use a held `FileStream` with `FileShare.None` on `.fluxvault.lock`, not a thread-affine mutex held across `await`. Leave the empty lock file in place on release; ownership is the open handle, not file existence. OS handle release after a killed process permits a later cooperating process to acquire it. Do not delete a lock to clear an apparent stale owner.

Retain an in-process primary-root semaphore for cancellable queuing. Acquire physical-root file leases in deterministic canonical-path order. Cross-process acquisition is non-blocking: return a clear repository-busy failure rather than running concurrently or implementing unbounded retries. Release every acquired handle and the semaphore after any partial failure. Private core methods share the caller's lease and never re-enter a public leased method.

Lease the primary and every mirror actually read, written or deleted. Capture can skip an unavailable/busy optional mirror with a warning while completing a valid primary capture. Destructive mirror maintenance requires leases over all roots participating in its plan; an unavailable required root blocks deletion. Ordinary history/status queries remain outside the long-running content lease.

Reject same, nested, case-equivalent or reparse-aliased primary/mirror roots before mutation. Resolve/check root components and reject reparse points in this supported envelope; adversarial path replacement and caller authorisation remain NEXT-002. A root lease alone is not permission to use another repository's mirror.

Add a small `.fluxvault-storage.json` marker: `formatVersion: 1`, `storageId: <32-hex GUID>`, and `role: primary|mirror`. Mirrors carry their primary's storage ID. This is physical ownership only, not a substitute for the future metadata `VaultId` namespace. Create markers only under a lease on a new, empty owned root; the lease and empty recognised storage directories are allowed bootstrap artefacts. An unmarked non-empty root, malformed marker or foreign storage ID produces an explicit ownership error. Never claim, reset or delete that root automatically. Existing staging data is preserved; this slice's harness always creates fresh roots.

The lease covers the complete metadata-reference lifetime for commit, deletion recording, purge, retention and repair; restore/preview/rehearsal keep it until publication/cleanup. Scrub and mirror previews also use it for trustworthy observations. Retire the unused `MetadataBackedChunkRepository` decorator, which currently records metadata after its inner commit has returned; production already injects the store into `FileSystemChunkRepository`. Carry its useful tests into that normal path.

## 2. Shared verified reader and bounded validation

Introduce a focused `VerifiedChunkReader` used by restore, preview, rehearsal, scrub, dedup checks and repair/mirror planning. It returns bytes only after these checks: descriptor grammar and supported encoding; bounded sidecar; sidecar/expected-descriptor agreement; bounded opened payload length; bounded decode; exact decoded length; BLAKE3 match. Open once, verify the bytes from that handle, then consume those same bytes. Do not validate one read and copy a second unchecked read.

Use a typed `RepositoryIntegrityLimits` with default ceilings: 16 MiB decoded chunk, 16 MiB stored chunk, 16 MiB decoder dictionary/window, 4 KiB sidecar, 64 MiB serialized manifest, depth 128, 100,000 resolved manifest nodes, 100,000 expanded destination entries, 1,000,000 expanded chunk references and 128 MiB cumulative restore metadata/path budget. Reject the operation when a limit is exceeded. The existing production chunker maximum is 1 MiB. These are internal safety/support limits, not user-tunable performance promises; expose no disable-verification switch or dormant Options fields. Tests can inject smaller limits. Validate configured chunker maxima against the reader limit before allocating/capturing.

Resolve incrementally. Charge serialized manifest bytes on every graph expansion, including cached/repeated DAG references, plus UTF-16 bytes of retained destination paths to the cumulative budget. Count expanded entries/chunk references, not just distinct node IDs. Reserve/check the budget before retaining the expansion or descending; never eagerly fetch the entire graph. At most one current manifest bounded by the per-manifest ceiling may be held outside the retained graph budget while its charge is checked. Tests must exceed cumulative limits using individually valid small manifests and repeated references, not only one oversized input.

Unknown codec values fail; they never fall through as raw. For zstd/LZ4/Brotli/LZMA, bound compressed input, decoded output and decoder window/dictionary allocation before allocation. Use the pinned library's bounded APIs/header validation and a capped output sink; a length check after unbounded `MemoryStream.CopyTo` is insufficient. Prove compatibility with each encoder's current output. If a decoder cannot meet the bound, stop that batch for a specific codec decision rather than quietly dropping the guard or changing Options semantics.

Validate digest grammar as 64 hexadecimal characters and version IDs as 32 hexadecimal GUID characters before any path construction; normalise case. Validate non-negative logical length, positive chunk lengths, supported enums and checked offset arithmetic. The supplied chunk order must cover `[0, LogicalLength)` exactly, with no sorting to conceal gaps/overlaps; an empty file has no chunks and zero length. Multiple references to one digest are valid only when their immutable descriptors agree. Content signatures, when present, must agree with the canonical sequence; they are consistency checks, not authenticity signatures.

Bound legacy JSON reads before deserialisation. For normal targeted PostgreSQL manifest reads, enforce the serialized-size ceiling before returning an oversized value to the client (server-side size check/bounded retrieval), then use the same manifest validator. This does not claim to solve all unpaged listing/maintenance queries; those remain NEXT-009.

## 3. Safe restore, preview and rehearsal

Single-file restore streams verified chunks into a uniquely named sibling staging file, checks the final logical length, flushes it, then publishes. Verify destination is outside primary/mirror storage, reject reparse destinations/ancestors in this envelope, and preserve the existing destination on pre-publication failure. If the destination was absent when the operation began, the final move must be non-overwriting: a file that appears during staging is preserved and publication fails. Replacement is allowed only for a destination that already existed under the requested overwrite policy. Never allocate whole-file content.

A single folder-version restore accepts a destination that does not yet exist. Resolve its bounded graph before writing; detect cycles, including tombstone cycles, missing references, entry-kind mismatch and inconsistent child lengths. Reject rooted/traversal names, separators, ADS colons, reserved device names, trailing dot/space aliases and case-insensitive/Windows-normalised duplicate names. Stage the entire tree beside the destination and publish with a non-overwriting directory move only after every file verifies. If the destination appears during staging, refuse publication and preserve it. Existing-folder merge is explicitly unsupported in this slice.

The existing multi-selection command remains per-file transactional, not all-or-nothing across the selection. Report actual restored counts and failures; never label a partial selection fully restored. The UI must explain the new-folder requirement for a folder-version restore and suggest choosing a parent plus a new folder name.

Return `RepositoryRestoreResult` for `RestoreAsync`: output path, verified logical byte count, restored-file count and warnings. Preserve a verified result when post-publication lineage recording fails. Add an optional restore-result field to the IPC response and warnings to bulk restore results; CLI prints warnings separately and returns success for committed verified content. Failure before publication returns the existing failure channel with a specific integrity/busy/ownership explanation.

Preview generation uses the same verified reader. Remove the current size/marker-only cache shortcut in this slice: regenerate a verified preview rather than reuse unverified cached bytes. Retain read-only marking and bounded-age cleanup of owned preview files. Treat post-publication preview-marking errors explicitly; never launch an unverified path.

Rehearsal creates its own GUID-named child beneath the supplied temporary root and deletes only that child. Preserve unrelated files and concurrent operations' directories. A run with zero eligible versions returns Warning with zero verified versions, not Healthy. A file passes only after content verification, not length alone.

## 4. Validated publication and deduplication

For each incoming digest, distinguish a new object from an existing/previously acknowledged one under the primary lease. Add a targeted metadata descriptor lookup: return no reference, one immutable descriptor, or conflict. PostgreSQL queries `version_chunks` by digest using the existing digest index; legacy mode builds an operation-local descriptor map once. Cache lookup/verification results per digest for the operation. Do not scan all manifests per chunk or trust a default empty metadata-store implementation.

If a healthy existing pair matches the incoming content and acknowledged descriptor, reuse it without changing its encoding. If either file exists but the pair is damaged/incomplete, or metadata references a now-missing pair, fail with a repair-required result and publish no new version. Incoming source bytes alone do not authorise changing the historical representation. If neither file nor any acknowledged reference exists, publish a new object.

New-object publication writes uniquely owned payload/sidecar staging files, flushes them, validates their bounded descriptor and decoded content, promotes them without overwriting any unexpected destination, and validates the final pair before recording metadata. Two file renames are not a transaction: interruption may leave an orphan/partial pair. Preserve that state and reject it on the next attempt; automatic orphan reclamation is deferred. Healthy unreferenced pairs may be reused only after complete validation.

A repair copies a donor's verified stored bytes and matching metadata using the acknowledged descriptor, stages/flushes/verifies the target, then replaces damaged target files while the donor remains intact. An interruption between replacements can leave an invalid target; it must never consume the donor or acknowledge health. Re-run repair safely on restart. No automatic recompression is allowed.

The descriptor lookup must reject contradictory acknowledged encoding/stored/decoded lengths, rather than select the first row. Remove the PostgreSQL chunk-catalog upsert's ability to silently rewrite an existing representation; enforce descriptor agreement transactionally while recording versions. No schema migration is needed for this lookup/check because the per-version descriptor fields already exist.

## 5. Verified mirror maintenance

Plan copy, repair and deletion per digest using verified objects, not separate file-existence tests. Snapshot capacity once per operation and update in-memory reservation accounting after each staged copy. Include sidecars and temporary-copy headroom; a failed capacity/disk check preserves the source.

Use a healthy primary as donor where available. If it is corrupt, a verified matching mirror may repair it first; the sole healthy departing mirror remains untouched until the primary and required surviving mirrors verify. Copy and verify before deletion, then recheck the required copies under held leases immediately before the deletion phase.

Retain current placement policy meanings: `MinimumMirrorCopies` counts mirrors, not the primary. For drain, evaluate the remaining configured nodes under the existing policy. Conservatively require at least one surviving verified mirror even when removing the last configured node would make the computed requirement zero; dropping all redundancy is not an implicit drain action. Any unsatisfied count, zero eligible targets, foreign ownership, contradictory descriptor or uncertain required health produces an Unresolved action and no deletion for that digest.

Delete payload/sidecar pairs only after the gate passes. Interruption between those deletes can leave a partial departing pair, but the surviving verified primary/mirrors remain available. Retry recognises the partial pair and completes only under a newly verified deletion gate. Reports distinguish verified, repaired, skipped/unavailable and unresolved work; a blocked drain is never Healthy. This verifies content-copy safety, not independent metadata disaster recovery.

## Acceptance matrix

| ID | Repeatable proof | Owning batch |
| --- | --- | --- |
| A01 | CLI captures/lists/restores a healthy disposable file; independent SHA-256 matches; same-length corruption fails with destination unchanged | 1–2 |
| A02 | Raw and every supported codec reject wrong digest/length, truncation, trailing decoded output, unknown codec and allocation-limit attacks | 1 |
| A03 | Gaps/overlaps/overflow, malformed IDs, cycles, cumulative metadata/expanded-entry limits, child-name collisions and path escapes fail before publication | 1, 3 |
| A04 | Missing payload, missing sidecar, both missing but referenced, conflicting descriptors and changed compression cannot produce false dedup success | 2 |
| A05 | Object publication failure creates no acknowledged new version; cache does not invent one; interrupted partial objects fail closed on reopen | 2 |
| A06 | Cancellation, locked/full destination, late bad chunk and destination appearance preserve original bytes/absence; post-publish hint failure returns verified completion with warning | 1, 3 |
| A07 | Folder restore publishes a complete verified new tree; existing trees are preserved; bulk selection reports partial outcomes accurately | 3 |
| A08 | Cached preview tampering never launches bad bytes; rehearsal catches same-length corruption and preserves unrelated temporary files | 3 |
| A09 | Reproduced sole-good-mirror drain preserves a healthy source until primary and surviving required copies verify; corrupt targets/zero capacity/offline roots block unsafe deletion | 4 |
| A10 | Two-process restore/retention, commit/repair and drain/capture cannot overlap protected IO; killed owner releases leases; cancelled/failed acquisitions leak none | 1, 4–5 |
| A11 | Real PostgreSQL and a private disposable IPC host execute capture → visible version → verified restore, including corruption/failure responses and immutable descriptor checks | 5 |
| A12 | Existing UI/CLI show verified success, warnings, busy and integrity failure accurately; no false success, automatic retry or service crash in the exercised flows | 3, 5 |
| A13 | Fixed-file-size increase does not create whole-file buffers; record 64/256/1024 MiB round-trip memory/timing and retain correctness alongside results | 6 |

## Verification, rollout and hand-off

Use new GUID-named scratch roots, a dedicated test PostgreSQL database on an explicitly disposable local instance, a test-owned pipe and unprivileged host process. A fixture must never inherit normal ProgramData or the normal database/pipe. A randomly named broad-access pipe is not sufficient isolation: the test host restricts access to the current user. Missing test prerequisites fail the requested integration gate visibly; unit tests remain runnable separately. No installer, existing Windows service or real repository is touched.

Run narrow test families after each coherent batch, then the full solution, a zero-warning Release build, the real integration lane and independent integrity/concurrency review. Use deterministic fault points for unit regressions and actual child-process termination for lease/publication boundary tests. Test-only hooks remain internal and inert outside the fixture. Do not equate a thrown injected exception with a killed process.

Record source revision, runtime, database version, storage, commands and results. Update NEXT-001/003/005 only for the specific delivered capability; their full roadmap gates cannot be marked complete. No migration or deployment is authorised. Rollback for this slice is to stop the disposable harness and retain its evidence; switch the development build without rewriting/deleting stored data. Never mix old binaries lacking leases/markers with the new format guard on an active test repository.

The implementation plan is ready for one implementation owner after the owner's model switch and explicit resume. Independent review is required for the completed integrity/concurrency change. The existing roadmap's broader operational approval requirements remain intact.

## Subsequent staging validation authorisation

The user later approved implementation and directed deployment/live validation on this Windows installation. That supersedes the original no-deployment restriction for this staging target. Fault tests continue to use owned disposable clusters with the installed PostgreSQL 18 binaries. Normal-server provisioning requires the authentication choice described in [staging deployment](../../verification/2026-10-01-verified-recovery/staging-deployment.md); it is not inferred from the reviewer's technical approval. Existing services, unrelated databases, real working files and production targets remain outside the test scope.
