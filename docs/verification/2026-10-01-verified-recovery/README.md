# Verified local recovery implementation evidence

Status on 2 October 2026: **the authorised verified recovery slice is implemented, deployed and live-validated as staging v1.0.4 on this PC**. The work is uncommitted in `codex/product-review-roadmap-20261001`, based on `b930ed5`. Nothing is merged or publicly released. The user expressly authorised automatic scoped PostgreSQL provisioning, staging installation and live validation without restarting PostgreSQL. The [design](../../superpowers/specs/2026-10-01-verified-local-recovery-design.md) and [plan](../../superpowers/plans/2026-10-01-verified-local-recovery.md) remain the acceptance contract.

## Observed checks

| Check | Observed result | Evidence |
| --- | --- | --- |
| Fresh Release solution build | Exit 0; zero warnings/errors | [Build](release-build-v104-final.log) |
| Complete owned-server solution run | 687 passed, zero failed/skipped: Core 339, App 187, Windows 17, Integration 144 | [Log](full-tests-v104-final.log), [TRX](full-test-results-v104-final/) |
| Real PostgreSQL/private IPC lane | 35 cases: 28 PostgreSQL, 7 private IPC, included in the full run | [SQL tests](../../../tests/FluxVault.Integration.Tests/PostgreSqlRepositoryIntegrityTests.cs), [folder ordering](../../../tests/FluxVault.Integration.Tests/PostgreSqlFolderPublicationOrderingTests.cs), [IPC tests](../../../tests/FluxVault.Integration.Tests/RepositoryIntegrityIpcTests.cs) |
| Real separate-process family | 11 cases: kill/reopen, restore/retention/capture, capture/repair and drain/capture | [Process tests](../../../tests/FluxVault.Integration.Tests/RepositoryProcessIntegrityTests.cs) |
| Native WPF file flow in owned fixture | Verified restore, healthy preview and corruption refusals observed | [Accessibility](native-ui-accessibility.json), [hashes](native-hash-evidence.json) |
| Native WPF folder flow in owned fixture | New-folder restore hash matched; repeated destination refused and sentinel preserved | [Accessibility](native-folder-accessibility.json), [hashes](native-folder-hashes.json), [shutdown log](native-folder-run.log) |
| Disposable PostgreSQL setup rehearsal | Actual corrected setup under ordinary SCRAM rules; runtime access survives admin removal; collisions/non-ASCII input preserved | [Result](setup-rehearsal.json), [log](setup-rehearsal.log), [helper](setup-rehearsal.ps1) |
| Normal PostgreSQL setup | Scoped runtime access works over IPv4/IPv6; passwordless administrator and unrelated-database access denied; original HBA rules preserved; no restart | [Setup result](installed-postgresql-setup.json), [fresh state check](installed-state-final.json) |
| v1.0.4 installer and staging upgrade | Zero-warning build; all 204 embedded/published/installed files match; configuration bytes preserved; CLI hash round trip and service policy passed | [Package build](release-package-v104.log), [payload](installed-payload-v104.json), [upgrade](installed-upgrade-v104.json), [smoke and policy](installed-smoke-v104.json), [installer log](installed-release-v104-smoke.log) |
| Installed service recovery | Retained all ten versions; file and nested-folder recovery hashes match; repeat destination refused and preserved; inventory availability contract passes | [Current results](installed-live-v104-results.json), [original capture and native hash](installed-live-results.json) |
| Installed desktop recovery | Selected generated version, saved through native dialogue, visible verified result and independent SHA-256; selection/inventory survive cache invalidation | [Accessibility](installed-native-ui-v104.json), [invalidation](installed-ui-cache-invalidation-v104.json), [hash](installed-live-results.json), [UI cleanup](installed-ui-cleanup.json) |
| Six measured CLI trials | All lengths/hashes matched; unchanged recapture created no new chunks; binaries match installed MSI | [Measurements](performance-v104.json), [payload comparison](installed-payload-v104.json) |
| Temporary process cleanup | Zero temporary UI, fixture, benchmark, installer or owned PostgreSQL processes remain; installed FluxVault service healthy/idle; normal PostgreSQL unchanged | [Cleanup](process-cleanup-v104.json), [fresh installed-state probes](installed-state-final.json) |

PostgreSQL 18.6 binaries at `D:\Program Files\PostgreSQL\18\bin` powered owned GUID clusters on non-standard loopback ports with fsync, synchronous commit and full-page writes enabled. The runner stops only its identified postmaster and checks exit. Fault injection never uses normal ProgramData or the normal database. Hosted CI is unrun. The installed service is an ongoing staging deliverable; it currently has no watched test folders. ProgramData and diagnostic history are retained.

## Corrections proved during implementation and live validation

1. PostgreSQL retirement and version identity defects were reproduced against real transactions: [original red](postgresql-original-red.log), [identity red](postgresql-version-identity-red.log). A READ COMMITTED transaction-scoped advisory lock now serialises record/delete before reading references. Immutable descriptor tuples and version IDs are checked before mutation. Identical replay is idempotent; changed identities roll back the batch. Retirement touches only affected, unreferenced catalogue/location rows.
2. Uppercase digests hid references behind case-sensitive SQL keys. [Red](postgresql-digest-case-red.log) and [green](postgresql-digest-case-green.log) support projection/query canonicalisation. Barrier-controlled record/delete orders, waiter cancellation, statement rollback and actual process loss after SQL commit are included in the full run.
3. Initial normal provisioning exposed a nested managed-HBA-block bug: removing temporary administrator access also removed runtime access. Insertion now stops before managed blocks, and successful setup requires a fresh runtime connection after admin removal. [Helper red](setup-auth-cleanup-red.log), [green](setup-auth-cleanup-green.log) and an actual setup rehearsal under SCRAM rules prove the correction. Normal access was repaired by adding only the scoped runtime block and reloading; no administrator rule was re-enabled, no database was reprovisioned and PostgreSQL was not restarted.
4. Installed v1.0.2 file recovery passed, but the latest folder snapshot omitted a sibling after captures completed out of timestamp order: [failure](installed-folder-ordering-failure.json). Generated folder snapshots and folder tombstones now advance by at least one PostgreSQL microsecond under the existing lease; file capture times remain unchanged. [Core red](folder-ordering-core-red.log), [SQL red](folder-ordering-postgresql-red.log), [core green](folder-ordering-core-green.log) and the full 670-test run cover reversed/equal/submicrosecond captures, subsequent updates, root/nested deletion, overflow and actual `current_entries`. Historical snapshots remain immutable. v1.0.2 was uninstalled while preserving ProgramData/database/history; v1.0.3 captured a fresh generated checkpoint and restored both files.
5. The first v1.0.3 packaging attempt reused the old v1.0.2 MSI despite new build parameters. Offline hash/version inspection refused it before installation. The release script now rebuilds both WiX projects. The corrected embedded MSI is version 1.0.3.0 and all 204 payloads match. The rejected package is retained under `artifacts/rejected-stale-v103`; the historical candidate checksum is in [v1.0.3 provenance](installed-payload-v103.json).
6. Resumed installed desktop validation exposed unavailable status inventory being reported as empty and clearing versions/selection. The [correction](inventory-correction.md) distinguishes unavailable snapshots, guards cache identity/generation, and protects shared profile-response handling. The current installed wire and desktop checks prove ten retained versions, automatic recovery from invalidation and a successful native restore.
7. A full-run preview failure prompted investigation. It did not reproduce in the diagnostic rerun, but a separate [deterministic regression](preview-cleanup-red.log) proved cleanup could delete fresh empty staging directories. Directory cleanup now observes the existing age cutoff; [four preview cases](preview-cleanup-green.log) and the final full suite pass. The original intermittent failure is not conclusively attributed to that defect. Roadmap assertions now match the observed partially implemented PostgreSQL staging scope.

Independent Astra reviews approved the complete integrity source scope and the bounded SQL, setup and folder-ordering corrections. [Review record](independent-source-review.md) separates source approval from runtime evidence. Direct metadata writers must also hold the repository lease when coordinating physical pruning; the SQL lock alone cannot protect that filesystem interval.

## Acceptance matrix

These proofs cover fresh owned local NTFS storage and bounded content operations. They do not certify unrestricted professional-file or production use.

| ID | Status | Evidence and limit |
| --- | --- | --- |
| A01 | Verified | Real CLI capture/list/inspect/restore hashes match; corrupt restore returns nonzero and preserves a sentinel. `CliHarnessTests`, [earlier checkpoint](cli-checkpoint.json) and installed CLI smoke. |
| A02 | Verified | Five codecs round-trip; malformed/truncated/wrong-length/digest/unknown/allocation/window attacks are refused. `RepositoryIntegrityTests` and `RepositoryReviewRegressionTests`. |
| A03 | Verified | Layout/signatures/IDs, SQL row/JSON identity, cycles/name/containment and repeated-DAG budgets. `FolderRestoreIntegrityTests` and actual SQL oversize/tampering cases. |
| A04 | Verified | Damaged/missing/partial acknowledged objects refuse false dedup; immutable tuples/IDs, digest case and compression after final retirement. `RepositoryPublicationTests` and real SQL tests. |
| A05 | Verified | Stage/final publication faults, stale caches, process death between pair renames and SQL commit loss. Potentially committed objects are preserved. |
| A06 | Verified within fault model | Corruption/cancellation, pre-publication IO failure, appearing destinations and post-commit warning results preserve the required bytes. Injected IO is not physical disk-exhaustion/power-loss certification. |
| A07 | Verified | New-tree staging, bulk counts, existing-destination refusal; native folder hashes and installed nested-folder hashes. Folder publication ordering/deletion regressions are included. |
| A08 | Verified | Tampered preview cannot launch bad bytes; rehearsal preserves unrelated temporary work. Native healthy preview matched; damaged preview did not call the fixture launcher. |
| A09 | Verified | Sole-good departing donor, capacity/survivor/offline refusal and interrupted drain retry. Required copies are read back before removal. |
| A10 | Verified | Eleven actual process cases plus partial acquisition/cancellation, junction/foreign roots and unavailable-volume cases. Hostile alias replacement and power loss are outside scope. |
| A11 | Verified | All 35 real PostgreSQL/private IPC cases, hashes and version/reference rows; status responds while content is held. Installed normal service/DB recovery also passed. |
| A12 | Verified | UI result/error/busy/warning tests and native fixture file/folder/preview checks passed. Installed service/CLI/UI recovery hashes match; desktop inventory and recovery selection survive cache invalidation. |
| A13 | Verified synthetic legacy lane | Six 64/256/1024 MiB trials, bounded private-memory growth and independent hashes. No PostgreSQL/VSS/professional-project performance claim. |

## Synthetic measurements

[Raw v1.0.4 measurements](performance-v104.json) bind CLI/Core/Abstractions hashes to the installed MSI. Each operation starts a new process; OS file caches are warm after generation/capture. Distinct seeded random blocks and per-MiB headers avoid repeated dedup objects. These timings establish a baseline on an active PC, not a demonstrated speed improvement. Package inspection and the beginning of staging upgrade overlapped this run; timing is observational. Memory is sampled every 10 ms, not an exact allocation trace. The lane explicitly uses legacy metadata without PostgreSQL, mirrors, VSS or Office/CAD/Adobe semantics.

| Pattern / compression | MiB | Capture s | Unchanged recapture s | Restore s | Sampled restore private MiB |
| --- | --- | --- | --- | --- | --- |
| random / off | 64 | 2.734 | 0.750 | 0.703 | 16.07 |
| random / off | 256 | 9.797 | 2.265 | 2.110 | 18.26 |
| random / off | 1024 | 39.843 | 8.875 | 7.516 | 30.32 |
| compressible / zstd | 64 | 1.469 | 0.453 | 0.484 | 14.66 |
| compressible / zstd | 256 | 4.640 | 1.172 | 1.453 | 15.36 |
| compressible / zstd | 1024 | 16.453 | 4.047 | 5.265 | 16.64 |

All six outputs matched independent SHA-256 and length. Restore private-memory growth from 64 to 1024 MiB was 14.26 MiB for random data and 1.98 MiB for compressible data, below the 64 MiB investigation threshold. Every benchmark child exited. Earlier measurement/build/test records remain historical evidence, not the current candidate provenance.

## Remaining acceptance and boundaries

The resumed desktop gate is complete. The owned desktop process exited; original empty-watch configuration was restored without purging history. [Staging deployment](staging-deployment.md) records the target, candidate and rollback. The [completion audit](completion-audit.md) maps the plan to evidence. Authorisation/vault isolation, configuration/lifecycle, independent disaster recovery, professional performance and full UI redesign remain separate roadmap work. Source and evidence are integrated by this user-authorised squash change. The checksum-identical installer is retained in the primary checkout before task worktree cleanup; historical records preserve their original checkpoint paths and Git state.
