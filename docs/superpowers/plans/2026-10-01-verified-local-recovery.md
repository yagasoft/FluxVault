# Verified local recovery implementation plan

> **Execution status:** the owner resumed implementation with "Approved." Implementation and independent integrity/concurrency source review have passed in the existing task worktree. Native fixture and installed service/CLI/UI recovery passed; all required runtime gates now have evidence.

**Goal:** eliminate false content-integrity success and unsafe mirror deletion while proving a real local capture-to-verified-restore flow.

**Architecture:** retain the current payload/sidecar and manifest layout; centralise bounded verification, conservative publication and physical-root ownership/leases. A content-sensitive operation holds its leases through reference publication or destination publication. Existing CLI/IPC/UI surfaces report verified results and warnings accurately.

**Tech stack:** C#/.NET 10, WPF, Windows NTFS, BLAKE3, existing codec libraries, PostgreSQL/Npgsql and xUnit. No new production packages, cloud services or drivers.

**Spec:** [Verified local recovery design](../specs/2026-10-01-verified-local-recovery-design.md). Read it together with this plan; its invariants and boundaries are part of every task.

**Planning checkpoint (historical):** independent Astra review approved the approach after the two identified design corrections; local document links and whitespace checks passed. Application tests were not rerun during that planning-only turn. Current implementation and runtime evidence is recorded at the end of this plan and in the linked acceptance matrix.

## Global constraints

- Baseline b930ed5; preserve the existing review/roadmap changes in `codex/product-review-roadmap-20261001` and unrelated index-script edits in the primary checkout.
- Reuse `C:\Users\os008\.codex\worktrees\fluxvault-product-review\FluxVault`. Do not create another worktree or run from the dirty primary checkout.
- Windows-only, local-first core operation; staging compatibility may change, but no existing repository/database may be reset or silently adopted.
- Six implementation batches, one owner. After batches 1 and 2, demonstrate the real CLI round trip and failure case before expanding the slice.
- Preserve immutable digest/decoded-length/stored-length/encoding descriptors. No recompression repair and no automatic orphan reclamation.
- Internal limits: 16 MiB decoded chunk, 16 MiB stored chunk, 16 MiB decoder window/dictionary, 4 KiB sidecar, 64 MiB serialized manifest, graph depth 128, graph nodes 100,000, expanded destination entries 100,000, expanded chunk references 1,000,000, cumulative restore metadata/path budget 128 MiB. Charge each expansion, including cached DAG reuse, plus retained UTF-16 destination paths; at most one bounded current manifest is outside the retained budget while its charge is checked. No user switch to bypass integrity checks.
- Metadata vault isolation, service authorisation, configuration correctness, scheduler recovery, full disaster recovery and a redesigned desktop remain later work. Do not mark their roadmap packages complete.
- Test only fresh GUID-named local roots, a fixture-owned PostgreSQL instance/database and a private unprivileged test host/pipe. Never inherit normal ProgramData or use the installed service.
- Introduce no build warnings. No `dotnet format`, destructive Git recovery, deployment, service restart, installer operation or user-data cleanup.

## Review focus

These failure classes require explicit proof, not incidental coverage:

1. Publication succeeds but lineage recording fails/cancellation arrives: report verified completion with warning, never ordinary failure after replacement (batches 1 and 3).
2. Same raw digest, different compression representation or contradictory historical references: preserve old versions and refuse an ambiguous repair/dedup hit (batch 2).
3. Another process uses a primary/mirror alias or dies while holding a lease: prevent overlap, reject unsafe ownership, recover without deleting lock files (batches 1, 4 and 5).
4. Folder destination appears mid-operation or contains Windows-normalised collisions: publish nothing into an existing tree and clean only owned staging (batch 3).
5. The sole healthy mirror is the departing source and the remaining targets are corrupt/full/offline: no deletion until verified required survivors exist (batch 4).

## Source map

Existing implementation anchors at b930ed5:

| Area | Files and relevant entry points |
| --- | --- |
| Shared repository | `src/FluxVault.Core/Storage/FileSystemChunkRepository.cs`: CommitAsync (~71), RestoreAsync/RestorePreviewAsync (~377), RunRestoreRehearsalAsync (~996), RunMirrorRebalanceAsync/RunMirrorDrainAsync (~1234/1283), RestoreManifestAsync (~1787), ValidateChunk (~1938), AtomicWrite (~2342) |
| Formats and results | `src/FluxVault.Abstractions/Storage/IChunkRepository.cs`, `FileVersionManifest.cs`, `RepositoryMaintenanceReports.cs`; `src/FluxVault.Core/Storage/ChunkMetadata.cs` |
| Codec and chunk size | `src/FluxVault.Core/Content/ZstdChunkCodec.cs`, `src/FluxVault.Core/Chunking/ChunkingOptions.cs`; production chunkers in `FluxVaultOperations.cs` and `src/FluxVault.Cli/FluxVaultCli.cs` use 1 MiB maximum |
| Metadata | `src/FluxVault.Core/Storage/Metadata/IRepositoryMetadataStore.cs`, `PostgreSqlRepositoryMetadataStore.cs`, `InMemoryRepositoryMetadataStore.cs`, `MetadataBackedChunkRepository.cs` |
| Mirrors | `src/FluxVault.Core/Storage/MirrorPlacementPlanner.cs`; mirror report types in `src/FluxVault.Abstractions/Storage`; existing configuration policy meanings remain |
| Surfaces | `src/FluxVault.Core/Service/FluxVaultOperations.cs`, `src/FluxVault.Abstractions/Ipc/FluxVaultIpcResponse.cs`, `RestoreSelectionSummary.cs`, `src/FluxVault.Cli/FluxVaultCli.cs`, `src/FluxVault.App/ViewModels/MainWindowViewModel.cs`, `src/FluxVault.App/Services/RestoreServices.cs` |
| Existing tests | Core `RepositoryTests`, `RepositoryMaintenanceTests`, `MetadataPrimaryRepositoryTests`, `ContentCodecTests`; Integration `RepositoryMirrorTests`, `ServiceOperationsTests`, `CliHarnessTests`; App `MainWindowViewModelRefreshTests` |

Add focused files under `src/FluxVault.Core/Storage/Integrity/`: `RepositoryIntegrityLimits.cs`, `RepositoryIntegrityException.cs`, `RepositoryLeaseSet.cs`, `StorageOwnership.cs`, `VerifiedChunkReader.cs`, `RepositoryObjectPublisher.cs`, and `RestoreManifestValidator.cs`. Add `ChunkDescriptor.cs` and `RepositoryRestoreResult.cs` under `src/FluxVault.Abstractions/Storage/`. Keep orchestration in the existing repository. Small supporting records/enums may share the relevant file. Do not mechanically split the entire repository class.

## Interfaces to establish

These are design decisions for the implementation; they are not existing APIs:

| Contract | Signature/meaning |
| --- | --- |
| Immutable descriptor | Public `ChunkDescriptor(string Digest, int LogicalLength, int StoredLength, ChunkEncoding Encoding)` in `FluxVault.Abstractions.Storage`; excludes per-version offset |
| Limits | `RepositoryIntegrityLimits` typed defaulted record in Core; values above; validate all positive and internally consistent |
| Failure | `RepositoryIntegrityException : IOException` carrying a `RepositoryIntegrityFailure Code` and safe message; enum values `CorruptObject`, `MissingObject`, `InvalidManifest`, `LimitExceeded`, `DescriptorConflict`, `OwnershipUnknown`, `OwnershipMismatch`, `RepositoryBusy`; do not wrap caller cancellation |
| Lease set | Internal `RepositoryLeaseSet.AcquireAsync(string primaryRoot, IReadOnlyList<string> mirrorRoots, MirrorLeaseMode mirrorMode, CancellationToken cancellationToken) -> ValueTask<RepositoryLeaseSet>`; modes None, BestEffort, Required; IAsyncDisposable; exposes acquired mirror roots, storage ID and warnings |
| Verified read | `VerifiedChunkReader.ReadAsync(string root, ChunkDescriptor expected, CancellationToken cancellationToken) -> Task<byte[]>`; returns only bounded, completely verified raw bytes |
| Bounded decode | `ZstdChunkCodec.Decompress(ReadOnlySpan<byte> payload, int originalLength, ChunkEncoding encoding)` retains its call shape but rejects unknown codecs and enforces bounded decoder/input/output allocation using the reader's limits; add a limits-taking internal overload if needed |
| Descriptor lookup | `IRepositoryMetadataStore.FindChunkDescriptorAsync(string digest, CancellationToken cancellationToken = default) -> Task<ChunkDescriptor?>`; null only for no acknowledged references; conflicting references throw; no default empty implementation |
| New publication | Internal `RepositoryObjectPublisher.PublishNewAsync(string root, ChunkDescriptor descriptor, ReadOnlyMemory<byte> storedPayload, CancellationToken cancellationToken) -> Task`; requires caller-held leases, absent finals and no acknowledged reference |
| Repair | Internal `RepositoryObjectPublisher.RepairAsync(string donorRoot, string targetRoot, ChunkDescriptor descriptor, CancellationToken cancellationToken) -> Task`; verified actual stored bytes, target staging/verification, donor preserved |
| Restore result | `RepositoryRestoreResult(string OutputPath, long VerifiedLogicalBytes, int RestoredFileCount, IReadOnlyList<string> Warnings)` in Abstractions; change `IChunkRepository.RestoreAsync(...)` to `Task<RepositoryRestoreResult>`; preview keeps Task |
| IPC result | Optional `RepositoryRestoreResult? RestoreResult = null` on `FluxVaultIpcResponse`, factory `WithRestore(RepositoryRestoreResult result)`; optional warnings collection on `RestoreSelectionSummary` |

Private helper signatures not listed here are implementation choices. Reuse existing enums/report records where their semantics fit. Keep test fault callbacks internal and constructor-injected; no public fault endpoints, static global fault switch or production setting.

## Batch 1 — Verified file recovery under an owned repository lease

**Deliverable:** a healthy real file restores correctly; a corrupt chunk cannot replace an existing destination. This is the first runtime result, not a scaffolding milestone.

**Files:** add the descriptor/result/failure/limits/lease/ownership/reader files above; modify `IChunkRepository`, `FileSystemChunkRepository`, `ZstdChunkCodec`, existing implementing fakes and the necessary CLI/service return-value call sites. Add `tests/FluxVault.Core.Tests/RepositoryIntegrityTests.cs`, `RepositoryLeaseTests.cs`; extend `ContentCodecTests.cs` and `tests/FluxVault.Integration.Tests/CliHarnessTests.cs`. Add Core-test internal visibility in the existing `Properties/AssemblyInfo.cs` only where needed.

**Consumes:** existing chunk format, codec/hasher, metadata read path and staging-file pattern. **Produces:** lease, descriptor, verified reader, restore-result contracts; single-file content verification and publication.

- [ ] Add failing `Restore_rejects_same_length_corruption_and_preserves_destination`: copy the review's synthetic setup, alter one raw byte, assert `RepositoryIntegrityException`, exact sentinel destination bytes, no new restore hint and no owned `.tmp` residue. Add absent-destination, empty file and late-corruption-after-a-valid-chunk cases. Add `Restore_preserves_file_created_during_staging`: use a publication barrier to create a sentinel at the initially absent destination; assert publication refusal, byte-identical sentinel and no restore hint.
- [ ] Add codec theories for Raw/Zstd/Lz4/Brotli/Lzma: exact round trip; wrong expected length/digest; truncated stored stream; extra decoded byte; unknown enum; declared allocation over the limit. Use tiny injected limits and header fixtures so rejection is proved before a large allocation. Preserve known BLAKE3 test vectors.
- [ ] Add file-manifest failures for bad digest/version grammar, overflow, negative fields, gaps, overlaps and ordering. Assert the final path and unrelated neighbouring files remain untouched. Upgrade only affected synthetic ID fixtures; never weaken validation to retain placeholder IDs.
- [ ] Add lease/ownership tests for same/nested/reparse roots, foreign/malformed marker, unmarked non-empty root, cancellation at the process gate and partial acquisition failure. Every failed attempt must permit a later valid acquisition without deleting a lock file. Marker creation is allowed only for fresh owned roots.
- [ ] Run the narrow tests and retain the expected failures. Implement verified reads and decoder bounds, coarse primary lease across restore and all existing content mutation entry points, marker guards, sibling staging, flush/verify and final publication. Use non-overwriting publication for an initially absent file destination; use replacement only under the existing-destination overwrite policy. Avoid re-entrant lease acquisition by extracting private core methods where required. Return verified completion with a warning if the post-publication hint fails.
- [ ] Prove the healthy and corrupted cases through `FluxVaultCli.RunAsync`, including nonzero exit on integrity failure and independent SHA-256 on success. Adapt required callers to the new restore result without redesigning their UI yet.
- [ ] Run `dotnet test tests/FluxVault.Core.Tests/FluxVault.Core.Tests.csproj -c Release --filter "FullyQualifiedName~RepositoryIntegrityTests|FullyQualifiedName~RepositoryLeaseTests|FullyQualifiedName~ContentCodecTests"`, then the relevant `CliHarnessTests`. Expected: no failures or warnings, and each corruption assertion preserves its destination.

Do not postpone this result until every legacy fixture is converted, every mirror path is optimised or the new integration runner exists.

## Batch 2 — No false deduplication or publication success

**Deliverable:** damaged/partial acknowledged objects cannot become successful new versions; valid existing content survives a compression-policy change.

**Files:** add `RepositoryObjectPublisher.cs`; modify `FileSystemChunkRepository`, `IRepositoryMetadataStore`, `PostgreSqlRepositoryMetadataStore`, `InMemoryRepositoryMetadataStore` and relevant metadata fakes. Remove the unused `MetadataBackedChunkRepository.cs` decorator and migrate its three useful behaviours into the normal metadata-primary tests. Add `tests/FluxVault.Core.Tests/RepositoryPublicationTests.cs`; extend `MetadataPrimaryRepositoryTests.cs` and targeted metadata SQL tests without treating SQL strings as runtime proof.

**Consumes:** batch 1 verification/leases. **Produces:** authoritative descriptor lookup, staged object publisher and verified immutable deduplication.

- [ ] Add failing `Commit_rejects_sidecar_without_payload_without_recording_version`, `Commit_rejects_payload_without_sidecar`, and `Commit_rejects_missing_but_referenced_object`. Assert unchanged version count and no altered existing version/descriptor. Use a recording/failing store to prove no reference publication on invalid objects.
- [ ] Add `Commit_reuses_verified_representation_after_compression_policy_change`: the same bytes are captured with two preferences, both versions restore, and existing stored bytes/encoding remain unchanged. Add conflicting per-version descriptors and assert explicit conflict rather than first-row selection.
- [ ] Add publication fault tests after staged payload, staged sidecar, first final rename and before metadata recording. Reopen a new repository instance; partial objects fail closed, healthy objects remain verifiable, no cached unpublished version appears, and potentially acknowledged objects are never deleted as compensation.
- [ ] Implement `FindChunkDescriptorAsync` using distinct descriptor tuples in `version_chunks` for PostgreSQL and equivalent in-memory logic. Check the actual digest index; use parameterised SQL and enforce conflicts transactionally during recording rather than updating a digest's representation. Legacy lookup scans/builds its descriptor map once per commit, not per chunk. Cache repeated digest checks within the commit.
- [ ] Implement flush/stage/validate/promote for new objects and fail-closed reuse rules. Verify final object pairs before publishing references. Invalidate mutation caches on failed publication. Keep a metadata acknowledgement failure distinct from an assurance of rollback.
- [ ] Convert the real sidecar/missing-payload probe into an assertion of the repaired behaviour. Run `RepositoryPublicationTests`, `MetadataPrimaryRepositoryTests`, relevant repository/lineage tests and the CLI capture/restore tests. Expected: all valid flows pass, damaged deduplication returns failure and stores no false new version.
- [ ] **Effort checkpoint:** run capture → list → restore → SHA-256 via the real CLI, then corrupt/recommit/restore refusal with a sentinel destination. Record commands and results. If there is still no executable result, stop scope growth and fix that blocker before batches 3–6.

The normal PostgreSQL implementation changes here receive their real transaction gate in batch 5; do not claim that gate from fake/SQL tests.

## Batch 3 — Safe folder recovery and honest preview/rehearsal/UI results

**Deliverable:** complete verified folder versions publish into new destinations; all existing recovery surfaces report what was actually verified or committed.

**Files:** add `RestoreManifestValidator.cs`; extend `FileSystemChunkRepository`, targeted manifest retrieval in `PostgreSqlRepositoryMetadataStore`, `FluxVaultOperations`, `FluxVaultIpcResponse`, `RestoreSelectionSummary`, `FluxVaultCli`, `MainWindowViewModel`, `RestoreServices`. Add `tests/FluxVault.Core.Tests/FolderRestoreIntegrityTests.cs`; extend `RepositoryMaintenanceTests`, `ServiceOperationsTests`, `IpcSerializationTests` and `MainWindowViewModelRefreshTests`.

**Consumes:** batches 1–2. **Produces:** bounded graph validation, staged new-folder publication and propagated verified results/warnings.

- [ ] Add malformed graph fixtures for missing children, tombstone/folder cycles, mismatched child kind/length, excessive depth/count, rooted/parent paths, ADS/device names, trailing dot/space and case-equivalent collisions. Add `Folder_restore_rejects_cumulative_metadata_budget` using individually valid manifests, and `Folder_restore_rejects_expanded_DAG_entry_and_chunk_budgets` using repeated cached references; inject small limits and assert rejection during incremental resolution with no published output or further descendant reads. Charge retained UTF-16 destination paths as well as serialized metadata. Assert no writes outside owned staging. Bound JSON reads before deserialisation, including targeted PostgreSQL retrieval.
- [ ] Add `Folder_restore_does_not_merge_existing_destination`, `Folder_restore_keeps_destination_created_during_staging` and `Late_corrupt_child_publishes_no_tree`. Include a valid nested folder, tombstone recovery and duplicate chunk references with matching descriptors. Compare relative file lists and independent hashes on success.
- [ ] Implement whole-tree staging beside a new destination and non-overwriting publication. Keep bulk multi-selection per-file semantics and propagate real counts, failed paths and warnings. Adjust the destination picker/help text to select a parent and a new folder name for a folder version; direct IPC callers receive the same validation.
- [ ] Add post-publication hint failure/cancellation tests: output is verified, `Success` is true and warnings explain missing lineage recording. Add pre-publication cancellation/IO failure tests where destination bytes remain unchanged. Verify IPC serialization preserves the restore result/warnings.
- [ ] Remove size/marker-only cached-preview reuse. Test that a tampered same-length cached preview is regenerated or rejected before launch. Verify failure does not call `IVersionPreviewLauncher.OpenFile`.
- [ ] Make rehearsal use an owned GUID child directory; test preservation of an unrelated sentinel and another operation's child. Corrupt content fails; zero eligible files yields Warning and zero verified versions. Do not turn cancellation into a Healthy report.
- [ ] Test UI success, integrity failure, busy and warning responses in `MainWindowViewModelRefreshTests`. Use concrete copy such as “Restored and verified” for successful content and “The destination was not changed” only for proven pre-publication failures. Keep the UI redesign and missing-service Options crash outside this batch.
- [ ] Run the touched Core, Integration and App test families. Expected: every malformed/cancelled flow preserves the required destination, valid folders restore, and no surface displays false success. Capture one disposable native restore result/error flow at final verification, after the real host is available.

## Batch 4 — Verified repair and safe mirror drain/rebalance

**Deliverable:** mirror operations preserve verified required copies, including the reproduced sole-healthy-departing-mirror case.

**Files:** modify `RepositoryObjectPublisher`, `RepositoryLeaseSet`, `StorageOwnership`, `FileSystemChunkRepository`, `MirrorPlacementPlanner` and the affected mirror report records. Extend `tests/FluxVault.Integration.Tests/RepositoryMirrorTests.cs`, Core `RepositoryMaintenanceTests.cs` and publication/lease tests.

**Consumes:** shared verified reader, authoritative descriptors and object publication. **Produces:** verified donor repair, gated deletion and truthful mirror health reports.

- [ ] Port the retained drain probe into `Drain_repairs_and_verifies_survivors_before_removing_sole_healthy_mirror`. Corrupt primary and remaining target, retain one healthy departing copy; assert repair yields independently verifiable required survivors before deletion. Add a no-capacity variant asserting the departing bytes remain and the report is not Healthy.
- [ ] Add corrupt-existing-target, missing-sidecar, conflicting-descriptor, zero eligible target, last configured mirror, full disk and unavailable/busy required-root cases. Assert no source deletion when required survivors cannot be established. Primary-only capture with an unavailable optional mirror must still succeed with a warning.
- [ ] Use leases/ownership for every touched root. Reject a foreign-owned/shared mirror and root aliases; sort/deduplicate physical roots, release partial acquisitions, and avoid waiting while holding an incomplete set. Do not silently skip a required survivor and then lower the count.
- [ ] Implement per-digest verified plans and repair from an acknowledged-descriptor donor. Preserve donor until target readback succeeds. Compute capacity once with reservation updates, account for metadata/staging overhead, and avoid repeated full-directory scans per chunk.
- [ ] Immediately before deletion, verify the primary and the policy-required survivors, with at least one remaining mirror for drain. Hold leases through payload/sidecar deletion. Do not use the primary to satisfy `MinimumMirrorCopies`.
- [ ] Inject interruption between target payload/sidecar publication and between departing payload/sidecar deletion. Reopen/retry; all surviving acknowledged versions must restore with matching hashes, and a partly deleted donor must never be counted healthy.
- [ ] Run mirror/maintenance/publication/lease families. Expected: all three original defect probes now assert safe outcomes, and blocked operations preserve content and return Unresolved/non-Healthy reports.

## Batch 5 — Prove the same behaviour through PostgreSQL and real process boundaries

**Deliverable:** a repeatable disposable service/IPC + PostgreSQL round trip and two-process concurrency/fault evidence. This is a required completion gate, not an optional manual experiment.

**Files to add:** `tests/FluxVault.TestHost/FluxVault.TestHost.csproj`, `Program.cs`, and fixture handlers; `tests/FluxVault.Integration.Tests/RepositoryProcessIntegrityTests.cs`, `PostgreSqlRepositoryIntegrityTests.cs`, `RepositoryIntegrityIpcTests.cs`, `Fixtures/DisposablePostgreSqlFixture.cs`; `eng/test-repository-integrity.ps1`. Update `FluxVault.slnx`, test project references and internal visibility as needed. Keep the host out of installer/publish projects.

**Consumes:** completed repository contracts and the existing `NamedPipeFluxVaultClient`/server handler. **Produces:** isolated executable verification through real DB transactions and the same request dispatch used by the service.

- [ ] Build a test-only host that requires explicit scratch root/pipe/database parameters and rejects normal defaults. Bind actual `FluxVaultOperations` to a real repository/store and disposable local capture; use a private current-user-only pipe factory via an internal test seam, rather than changing production authorisation in this slice. For the native walkthrough, give the test host WPF/App references and a test-only UI mode: instantiate the real `MainWindow` and inject a `MainWindowViewModel` with that pipe client, a scratch layout store and a non-mutating service-controller fixture. Do not launch the normal App startup path or add a production debug-pipe option. No SCM service registration or admin rights.
- [ ] Write `eng/test-repository-integrity.ps1 -PostgreSqlBinPath <existing PostgreSQL bin directory> -RunFullSuite`. It creates its own cluster/data directory, role/database and free loopback port beneath a new GUID scratch root, starts only its own process hidden, supplies fixture-specific environment values and records ownership/process identity. It must never accept an arbitrary normal database endpoint, run the project's machine-wide setup script or stop an existing PostgreSQL process. Use existing installed PostgreSQL binaries; if absent, report that concrete prerequisite without silently skipping tests or installing anything.
- [ ] The fixture must confirm its ownership marker, loopback port, random database name and test-server identity before any create/drop operation. Cleanup targets only those verified generated resources; preserve logs/artifacts on failure. Do not print credentials or reuse user/production connection settings. Use explicit test environment variable names, not HOME/CODEX_HOME.
- [ ] Run named-pipe capture → list → restore with real PostgreSQL and independent SHA-256. Repeat raw/compressed corruption, missing referenced object and contradictory descriptor cases; query actual version/reference rows to prove no invalid publication. Test transaction failure and ambiguous acknowledgement without destructive compensation.
- [ ] Use test-host child processes and explicit barriers for restore-versus-retention, capture-versus-repair and drain-versus-capture. Assert the second process cannot access protected content concurrently and later succeeds once ownership is released. Kill the owning child at defined publication boundaries; reopen from a fresh process and verify content/partial-state handling. A thrown exception alone does not satisfy the process-kill cases.
- [ ] Check process-gate cancellation and failure halfway through a multi-root lease acquisition. Test two physical root names resolving to the same location and foreign mirror ownership. Ensure fast status can still answer while a long content operation holds its lease.
- [ ] Mark DB-dependent tests `Category=RequiresPostgreSql`; explicitly selecting them without fixture prerequisites fails with an actionable message, never passes or silently skips. Ordinary focused runs may use `--filter "Category!=RequiresPostgreSql"`. The runner's full-suite mode provides the environment and runs all tests, including the DB lane.
- [ ] Run the script and retain command output/TRX plus exact source/runtime/PostgreSQL versions. Expected: every A10/A11 scenario passes with no normal service, database or user repository access. If a live prerequisite is missing, report the gate as incomplete and do not claim the slice complete.

## Batch 6 — Measure, review and close the slice

**Deliverable:** checked capability with limits, documentation and independent integrity/concurrency review; no deployment.

**Files:** update `docs/storage-format.md`, `docs/architecture.md`, `docs/testing-strategy.md`, `docs/improvement-roadmap.md`, `docs/roadmap-tracker.md` and relevant CLI/recovery documentation. Save compact redacted results beneath a new dated review-evidence directory. Do not rewrite the historical review as if its findings never existed.

- [ ] Run synthetic 64/256/1024 MiB real-interface capture/unchanged-recapture/restore trials using raw and compressible data. Record source bytes, elapsed time and sampled private/working memory; independently hash outputs. Include the cost of validating dedup hits. Separate process startup/cache effects and make no professional-app/100 GB claims.
- [ ] Use allocation/buffer tests plus the measurements to establish no whole-file buffer. Investigate a greater-than-64 MiB increase in peak private memory between otherwise identical 64 MiB and 1024 MiB single-file restore runs; record the cause and result before closing A13. Timings establish a baseline, not an invented throughput promise. Do not bypass verification to meet a timing target.
- [ ] Run `dotnet build FluxVault.slnx -c Release --no-incremental`, then the runner with `-RunFullSuite` (it supplies the disposable DB environment for all tests), and `git diff --check`. Expected: zero warnings/errors, no failing tests, and no silent omission of the DB/process cases. Run narrower checks first during earlier batches; avoid repeated unchanged full runs.
- [ ] Inspect the native restored-success, corruption-failure and existing-folder-refusal flows using the disposable host. Confirm the returned status is visible and previews launch only verified paths. No full UX/accessibility certification is claimed.
- [ ] Update the acceptance matrix with evidence links and any failures/unrun checks. Mark only the delivered NEXT-003 content capability and NEXT-001/005 sub-capabilities; retain the broader P0 blockers and staging-only status.
- [ ] Request one independent Astra/equivalent review of the complete integrity/concurrency diff, spec and observed evidence. Review must cover descriptor immutability, actual-byte verification, commit-point results, ownership/leases, folder containment, last-copy preservation and real DB/process tests. Resolve blocking findings and rerun affected evidence only.
- [ ] Present the verified slice and remaining gates. Preserve the branch/worktree and all unrelated changes. Do not push, merge, reset a staging repository, install/start a service or deploy unless separately authorised in the resumed task.

## Completion and model-switch hand-off

Completion means A01–A13 are supported by repeatable evidence and blocking review findings are resolved. It does not mean FluxVault is ready for unrestricted real files. If a required integration/codec/platform gate cannot run, retain the work and report that exact gap; do not substitute source-string tests or self-approval.

Recommended execution after the model switch: one GPT-6.1 Sol implementation owner, with an independent Astra review at the consequential gate. The user-selected model remains owner; this recommendation does not switch it. Start with batch 1, reuse the current worktree, and do not repeat the entire product review.

**Current checkpoint:** implemented, deployed and live-validated as staging v1.0.4; integrated by this squash change. The full suite has 687 passes (Core 339, App 187, Windows 17, Integration 144), including 35 PostgreSQL/private IPC and 11 separate-process cases, with zero failures/skips and a fresh zero-warning Release build. Independent Astra review approved the integrity scope and bounded SQL/setup/folder-ordering/inventory/preview corrections. Native fixture recovery and six measured published-CLI trials matched independent hashes. All 204 installed payload files match the inspected MSI. Installed service/CLI file and nested-folder recovery passed; installed desktop recovery through the native dialogue matched SHA-256 and automatic refresh preserved inventory/selection after cache invalidation. All temporary processes exited. The original empty-watch configuration is restored without history purge; normal PostgreSQL was not restarted. See the completion audit for limits; the broader improvement roadmap and UI rebuild remain open.
