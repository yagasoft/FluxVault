# FluxVault product and engineering review

Reviewed on **1 October 2026**, against source revision **b930ed5**. The recommended outcome is a rebuilt desktop experience and selective redesign of the trust-critical backend, followed by measured performance work. The current code contains substantial useful capability, but it is not yet a dependable professional backup product: targeted probes reproduced successful operations that leave data corrupt or unrecoverable.

The owner describes the app as working but hard to trust, wants a much easier and more modern interface, and identifies performance as a major concern. The intended audience is professionals using Office, CAD/BIM, Adobe and large working files. Staging status permits breaking changes; Windows-only and local-first basic functionality are settled constraints.

The actionable output is the [improvement roadmap](../../improvement-roadmap.md). The [UX review](ux-review.md) contains current screenshots and flow observations. The [evidence record](evidence/README.md) distinguishes the tests, probes and measurement limits. This review changes documentation only; it does not fix the findings or certify the app for real data.

## Scope and evidence quality

Reviewed source includes the WPF app/view models, service/IPC/configuration/watchers, repository/metadata/retention/mirrors/restore, Windows capture adapters, sync/cloud foundations, tests, benchmarks, installer/release workflows and core project documentation. Independent reviews covered integrity/concurrency, service security/lifecycle, and delivery/performance evidence. The main review covered product/UX, command semantics, integration and conclusions.

The current build was launched and eight desktop workspaces inspected through native window capture and UI Automation. There was no FluxVault process or installed service at the start of this run. Screenshots therefore show a fresh, service-unavailable state, not a populated production vault. Opening Options caused a confirmed unhandled timeout crash. Successful backup/restore UI journeys, tray behaviour, real VSS writers, Narrator, DPI/high-contrast combinations and a populated large-vault interface were not exercised.

No installed service, real database, real user repository, cloud account, driver or production configuration was changed. Storage probes used disposable file-manifest repositories but invoked the shared repository implementations used by normal metadata mode. UI probes used fake clients and a disposable configuration file. Security and concurrency findings below marked **source** establish the code path, not a live exploit or timing reproduction. Physical power-loss durability is an identified risk requiring separate validation.

Flux KB was unavailable. Current files and execution output were the evidence source. Existing unrelated edits to the local code-index scripts were preserved. Historical roadmap claims and test names were treated as context, not proof of current capability.

## What already deserves to be retained

- The desktop/service separation, typed configuration, local-only diagnostics and named boundaries between platform-independent and Windows-specific work are useful foundations.
- Content-addressed chunks, streaming capture, hashes, multiple codecs and retention policy concepts are implemented. Ordinary disposable byte round trips and deduplication worked in this run.
- Writer-aware VSS code distinguishes covered application consistency from crash consistency; ordinary readable capture is explicitly labelled best effort. This is a better starting point than an unconditional “safe open-file backup” promise.
- Restore destination selection, overwrite confirmation, staged writes, mirror warning semantics, bounded capture concurrency and fast-status paths show awareness of important failure modes. Their remaining gaps are identifiable and testable.
- The suite covers many policies, service operations, view-model transitions and platform abstractions. It builds without warnings and is fast enough to remain a useful development feedback loop.

These strengths argue against discarding the entire codebase. They do not outweigh the false-success and deletion defects.

## Verification observed in this review

| Check | Observed result | What it establishes |
| --- | --- | --- |
| Release build, .NET SDK 10.0.300 | Passed, 0 warnings, 0 errors; 64.484 s including restore | Current solution compiles with the available SDK and strict warnings. |
| Full automated suite | 484 passed, 0 failed, 0 skipped; 4.156 s command time | Existing assertions pass: Core 194, Windows 17, Integration 96, App 177. |
| Equal-length raw corruption probe | Restore succeeds with incorrect bytes; rehearsal says Healthy; scrub says Critical | Restore/rehearsal success is not content-integrity proof. |
| Missing dedup payload probe | Recommit succeeds; payload remains absent; restore throws FileNotFoundException | A sidecar can be mistaken for valid stored content. |
| Last healthy mirror drain probe | Drain says Healthy and removes the only healthy payload; scrub becomes Critical | Copy-before-delete logic does not verify the surviving copies. |
| UI configuration probe | Four unrelated settings groups omitted; a custom metadata DB resets to the default; disabled becomes enabled | Ordinary dashboard save can change operating behaviour outside the edited scope. |
| Failed-save command probe | Backup still requested after an explicit rejected save | The UI does not make save outcome a prerequisite for dependent work. |
| Native Options workflow | Application terminates with .NET Runtime event 1026 and TimeoutException | A missing service crashes the settings flow. |
| Bounded CLI performance/restore | Two 257 MiB final restores match source SHA-256 | Healthy file-mode round trip and bounded small-workload measurements, not DB/professional-workload certification. |

There are no automated live PostgreSQL integration tests in the inspected suite: SQL-string/projection tests, in-memory metadata and explicit legacy CLI mode are used. Zero skipped tests therefore does **not** establish database validation. Packaging/XAML/source-string checks are useful structural checks, but they are not installed-behaviour or rendered-UI tests.

## Findings that block trust

Severity is task-relative: **P0** must be resolved before trusting real files; **P1** is necessary for the dependable professional product; **P2** is a later limitation or improvement. Exact line references refer to b930ed5, before the documentation additions.

### F01 — Privileged commands lack caller and resource authorisation

**P0 · source-confirmed; exploitation not attempted.** The MSI installs the service as LocalSystem. The pipe grants AuthenticatedUser and AnyPackage read/write access, then dispatches a request with no caller identity. Configuration, restore, diagnostics export, retention and mirror operations have no command/profile/path authorisation boundary. An ordinary permitted local process can request service-privileged reads, writes or deletion. Restricting connection principals does not establish authority over each operation. Remote accessibility was not tested and is not needed for this local finding.

Evidence: `installer/wix/FluxVault.Installer/Package.wxs:56`; `src/FluxVault.Core/Ipc/NamedPipeFluxVaultServer.cs:108–109,182`; `src/FluxVault.Core/Service/FluxVaultOperations.cs:303–306,773–780,1415–1439`. Current pipe security tests assert broad grants rather than denial of unauthorised work. **Roadmap:** NEXT-002, with real Windows identity/path/reparse tests. Microsoft documents pipe access control and [client impersonation](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client); the conclusion about FluxVault follows from its missing operation-level checks.

### F02 — Restore and rehearsal can report corrupt content as success

**P0 · reproduced.** Restore decodes and writes chunks without checking their content digest, expected decoded/stored length, layout or final logical length before replacement. Rehearsal checks total length alone. Flipping one byte in an uncompressed chunk produced incorrect restored bytes, a Healthy rehearsal and a Critical scrub. The staging-file pattern protects against exceptions but does not help when invalid bytes are accepted.

Evidence: `src/FluxVault.Core/Storage/FileSystemChunkRepository.cs:1833–1846,1031–1040`; existing scrub validation at `1938–1991`; [probe results](evidence/integrity-results.jsonl). **Roadmap:** NEXT-003; one shared verified reader and publication only after complete verification, including destination preservation on failure.

### F03 — Deduplication can publish a version referencing missing content

**P0 · reproduced.** A surviving chunk metadata sidecar suppresses payload creation even if the payload is missing or corrupt. Recommitting the healthy original input returned success while its chunk remained absent. A payload left without its sidecar also risks a mismatch when a later compression choice writes new metadata while an existing payload is retained.

Evidence: `FileSystemChunkRepository.cs:95–107,2347–2356`; [probe results](evidence/integrity-results.jsonl). **Roadmap:** NEXT-003; validate/recover a dedup object and make payload/metadata publication one consistent recoverable operation. Add fault points between writes, not only happy-path round trips.

### F04 — Mirror removal can destroy the last healthy copy

**P0 · reproduced.** Rebalance/drain treats existing target files as sufficient, without verifying their bytes. With a corrupt primary, a corrupt remaining target and one healthy departing mirror, drain returned Healthy and deleted the healthy payload. The existing copy-first/replan safeguard covers absent targets, but not corrupt existing ones. Rebalance also needs explicit under-replication handling when no target qualifies.

Evidence: `FileSystemChunkRepository.cs:1593–1595,1252–1268,1303–1319,1390–1446`; [probe results](evidence/integrity-results.jsonl). **Roadmap:** NEXT-003/005; count verified surviving copies under coordination before deletion and retain sources whenever required health/capacity is uncertain.

### F05 — Profile duplication does not isolate storage metadata

**P0 · source-confirmed; live DB scenario not run.** Duplication changes the repository path but retains the same metadata database and other state settings. Metadata reads and deletes have no vault namespace. The new profile can list the original's versions while looking for their chunks beneath a different root; retention/purge through the clone can remove original metadata.

Evidence: `src/FluxVault.Core/Service/FluxVaultProfileManager.cs:148–153`; `src/FluxVault.Core/Storage/Metadata/PostgreSqlRepositoryMetadataStore.cs:108–168,243–258`; `PostgreSqlMetadataSchema.cs`; `FileSystemChunkRepository.cs:1837`. **Roadmap:** NEXT-002; durable vault identity and isolated metadata/chunk binding. Validate with two populated real-DB vaults, not profile name assertions.

### F06 — Dashboard save resets settings outside the edited scope

**P0 · reproduced client payload and file-store round trip.** `BuildConfiguration` creates a new configuration but omits MetadataStore, RepositoryMaintenancePolicy, Sync and DiagnosticsPolicy, while forcing IsEnabled=true. The store fills omitted groups with defaults; it does not merge them with the existing profile. A custom database name became `fluxvault_metadata` in the probe. Saving file selections or running backup can therefore disconnect the user from the intended metadata or silently alter policy.

Evidence: `src/FluxVault.App/ViewModels/MainWindowViewModel.cs:1995–2024,693–711,1212–1215`; `src/FluxVault.Core/Configuration/FileFluxVaultConfigurationStore.cs:107–125`; [UI probe results](evidence/ui-results.jsonl). **Roadmap:** NEXT-004; immutable snapshots plus validated scoped updates and preservation tests across every settings group.

### F07 — Change checkpoints can acknowledge work before it is protected

**P0 · source-confirmed interleaving.** USN checkpoints are saved before changed paths are returned for backup. Capture results are ignored by the catch-up caller; pending watcher entries are removed before successful capture. A crash, lock or disk failure after checkpoint publication can remove the opportunity to replay a change. Periodic reconciliation can eventually capture a surviving current file, but cannot recover a lost intermediate version.

Evidence: `src/FluxVault.Core/ChangeTracking/UsnCatchUpService.cs:52–55`; `src/FluxVault.Core/Service/FileSystemProtectionLoop.cs:96,108,145,154,412`. **Roadmap:** NEXT-005; durable admission/acknowledgement and retry state, with interruption tests on both sides of checkpoint publication.

### F08 — Background faults and stop completion are not reliably owned

**P0 · source-confirmed; timing stress pending.** `Task.WhenAll` combines protection and indefinitely running siblings, so one failed child can remain hidden while the aggregate never completes. Disable/stop cancels without joining runtime tasks; loop watchers lack final disposal; IPC connection tasks are detached. Re-enabling a cached loop can overlap the prior generation. A responsive dashboard can therefore coexist with failed protection, and acknowledged stop does not prove all writes ended.

Evidence: `FluxVaultProfileManager.cs:21–23,242–243,275–302`; `src/FluxVault.Service/Worker.cs:50–53`; `FileSystemProtectionLoop.cs:36–111,219–222`; `NamedPipeFluxVaultServer.cs:43`. **Roadmap:** NEXT-005; structured fault supervision, cancel/join/dispose and generation ownership. Tests must fault individual real child loops, not substitute an already-failed aggregate.

### F09 — Repository operations do not share a complete concurrency or durability contract

**P0 · static race/power-loss risk; not a reproduced physical crash.** The repository mutex is process-local and does not cover restore, scrub, repair, rebalance and drain. A restore can lose a chunk to retention; service/CLI processes can race reference checks and physical deletion. Ordinary atomic rename gives useful visibility protection, but no explicit flush/order contract establishes that chunk/journal content is durable before database references/export flags commit. Interrupted cleanup has no complete recovery protocol.

Evidence: `FileSystemChunkRepository.cs:16,33,76,377–395,1092,1121–1141,1234,1283,2342–2361`; `PostgreSqlRepositoryMetadataStore.cs:91`. **Roadmap:** NEXT-003/005/006; one canonical writer, read/checkpoint pins, coordinated GC and explicit durable publication with fault injection followed by disposable storage-failure tests.

### F10 — Stored manifest fields are not safe inputs for restore

**P0 · source-confirmed trust-boundary defect; hostile repository probe not run.** Deserialised child names, version IDs and digests enter path construction without semantic grammar/containment validation. A tampered or imported manifest can contain rooted/parent-traversal names, malformed graph references or unbounded decode sizes. A folder restore may escape its selected destination; malformed graphs can recurse indefinitely. This remains relevant after IPC authorisation is fixed.

Evidence: `FileSystemChunkRepository.cs:1777–1815,2794–2806`; `PostgreSqlRepositoryMetadataStore.cs:768–778`; `src/FluxVault.Core/Content/ZstdChunkCodec.cs:87–93,107–113`. **Roadmap:** NEXT-002/003; validated IDs/direct-child names, bounded graph/codec processing and final-handle containment including Windows reparse points.

### F11 — Independent disaster recovery is incomplete in normal metadata mode

**P0 · source-confirmed capability gap.** Database-primary commits export journal records only beneath the primary root and skip mirrored legacy manifests. No production journal replay consumer exists. DB backup scripts are useful, but an older dump can reference chunks later reclaimed by retention; journal records lack the deletion semantics needed for safe standalone replay. Old failed exports are not automatically drained, because subsequent commits export only their own version IDs. Surviving mirror chunks alone do not establish a clean-machine recovery route.

Evidence: `FileSystemChunkRepository.cs:1508–1523,571–597`; `PostgreSqlRepositoryMetadataStore.cs:228–269,369–418,794–796`; `eng/backup-fluxvault-db.ps1:41–59`; `eng/restore-fluxvault-db.ps1:88–91`. **Roadmap:** NEXT-006; an independently stored consistent checkpoint/replay format, retained required chunks, durable outbox retry and a destructive **disposable** recovery drill.

## Other material correctness and resilience findings

| ID | Priority and evidence | Finding, consequence and required change |
| --- | --- | --- |
| F12 | P1 · source | Shared profile-set read/edit/save is last-writer-wins; missing-profile saves can resurrect a deleted profile. Add one revisioned config owner, transactional update semantics and stale-client tests. `FluxVaultProfileConfigurationStore.cs:15–30`; `FileFluxVaultProfileSetStore.cs:49–60`. NEXT-004. |
| F13 | P1 · source | Profile IDs need only be nonblank before path composition; sanitisation is lossy and permits dot segments. Cadence validation is incomplete and load skips validation. Use opaque IDs, canonical state paths, comprehensive bounds and a last-known-good config. `FluxVaultProfileManager.cs:358–362`; `FileFluxVaultProfileSetStore.cs:144`; `src/FluxVault.Service/Program.cs:114–121`; `FileFluxVaultConfigurationStore.cs:31,49–93`. NEXT-002/004. |
| F14 | P1 · source | Missing/reappearing roots and native watcher overflow are not recovered by a health-driven watcher lifecycle; offline deleted IDs, rename old paths and directory moves can be lost until reconciliation. Subscribe to native errors and reconcile namespace changes; test actual NTFS failure paths. `FileSystemProtectionLoop.cs:204–263`; `WindowsUsnChangeJournalReader.cs:130,241–274`; `UsnCatchUpService.cs:59`. NEXT-005. |
| F15 | P1 · source | USN catch-up bypasses debounce/minimum interval decisions; pause is a queued non-idempotent toggle behind long mutations. Unify change-source scheduling and expose explicit requested/actual pause states. `FileSystemProtectionLoop.cs:65,96,154,333`; `FluxVaultOperations.cs:816–821,1391–1406`. NEXT-004/005. |
| F16 | P1 · source | IPC permits unbounded lines and clients without per-request deadlines; 32 idle connections can exhaust availability, and disconnected mutations have ambiguous completion. Use bounded frames, tracked jobs, deadlines, quotas and safe retry semantics. `NamedPipeFluxVaultServer.cs:19,43,132–136`; `NamedPipeFluxVaultClient.cs:11–16`. NEXT-002. |
| F17 | P1 · reproduced and source | Options load leaks a connection timeout through async-void and crashes the app. Backup proceeds after save failure/cancellation. Some other commands likewise lack consistent exception handling. Use a common result/error/cancellation contract; preserve local edits and the selected version. `OptionsWindow.xaml.cs:18`; `OptionsViewModel.cs:220`; `MainWindowViewModel.cs:693–721,1212–1220,836–846`. NEXT-004/007. |
| F18 | P1 · source | Folder consistency copies the newest child's label although siblings come from different times. Readable live files have no post-read stability proof; VSS cancellation can bypass cleanup and native wait is infinite. Define stable file/capture-set semantics and bounded cleanup. `FileSystemChunkRepository.cs:1556–1568`; `FluxVaultOperations.cs:2544–2567`; `WindowsVssSnapshotCoordinator.cs:117–121`; `WindowsVssBackupSession.cs:281–289`. NEXT-010. |
| F19 | P1 · source | Restore stores bytes and limited timestamp metadata, but does not restore the full Windows metadata contract. Define supported timestamps/attributes/ACLs/ADS/hardlinks/sparse/reparse behaviour, then test the chosen professional scope. Avoid bare-metal claims. `FileVersionManifest.cs`; `FileSystemChunkRepository.cs:1801–1846`. NEXT-001/010. |
| F20 | P1 · source, intentional current behaviour | Options Save clears all legacy global exclusions; a test explicitly expects this. Removing protection prompts permanent purge of history. These are risky product semantics, even though implemented deliberately. Preserve/explicitly convert exclusions and separate stop-protecting from history deletion. `OptionsViewModel.cs:260`; `OptionsViewModelTests.cs:306–327`; `MainWindowViewModel.cs:695–710`; `RestoreServices.cs:68–90`. NEXT-004/007. |
| F21 | P2 until enabled · source | Sync hydrator materialises whole local/remote files, has check/write races and incomplete crash/idempotency sequencing; peer journal append is not serialised. Conflict resolution records metadata rather than executing all user actions. No production hydrator caller was found. These are foundation limitations, not observed live sync incidents. `FileSyncHydrator.cs:30–72,106–119,145–152`; `FilePeerSyncJournal.cs:37–58,106`; `FileSyncConflictStore.cs:38–45`. NEXT-012; defer rollout. |

VSS requires coordination with participating writers to support application consistency; it does not replace representative application reopen checks. This matches [Microsoft's VSS architecture](https://learn.microsoft.com/en-us/windows-server/storage/file-server/volume-shadow-copy-service). The project-set recommendation is an inference from FluxVault's individual-file capture model and the intended professional audience.

## Performance assessment

There is no defensible whole-app throughput claim yet. Existing streaming/dedup primitives performed usefully on a small synthetic local corpus; the large-vault paths contain concrete algorithmic costs that warrant redesign and measurement.

| Cost | Current evidence | Improvement to prove |
| --- | --- | --- |
| Per-chunk mirror scans | Each new chunk obtains mirror used bytes through recursive repository file enumeration; scrub repeats it per unique chunk. `FileSystemChunkRepository.cs:1202–1206,1728–1733,2275,456–457`. | One capacity snapshot/incremental accounting per batch; report filesystem operations and behaviour with slow/offline mirrors. |
| Metadata materialisation and write amplification | Full manifest JSON lists and individual commands per chunk/reference/entry. `PostgreSqlRepositoryMetadataStore.cs:121–140,473–490`. | Indexed paged/streaming APIs, bulk writes, query/round-trip budgets and real-DB scale tests. |
| UI-thread IO and collection replacement | File browser synchronously enumerates/sorts files; inventory fetches all versions; refresh clears/rebuilds collections. `WindowsFileBrowserFileSystem.cs:17–56`; `FileBrowserViewModel.cs:95–143,610–638`; `MainWindowViewModel.cs:1459–1504,1916–1945`. | Cancellable background enumeration, server-side paging/filtering, incremental observable updates and interaction-latency tests. UI virtualisation alone does not virtualise the data. |
| Multiple scopes on one volume | Journal reads/path resolution repeat for each scope; pending-count scans and per-event timestamp queues add callback work. `WindowsUsnChangeJournalReader.cs:65–108,153–187`; `FileSystemProtectionLoop.cs:468–473,510–534`. | One journal reader per volume, indexed routing and bounded aggregate metrics; benchmark fixed churn across 1/10/100 scopes. |
| Chunk allocation/read amplification | Streaming is bounded, but chunk-window copies/allocations recur; changed large files are still read/chunked as complete streams. `StreamingFastCdcChunker.cs:29–49,59–78`; `FluxVaultCli.cs:91–101`. | Measure allocation and physical/source IO before buffer pooling or more complex change-range mechanisms. |
| Dormant SDK payload | Release App/Service/CLI directories measure 64.1, 65.0 and 62.8 MiB respectively; Microsoft.Graph.dll alone 43,249,696 bytes in each. Core references five cloud SDK families although live providers are deferred. | Keep optional provider assemblies outside the essential local runtime. These disk sizes do not establish loaded memory cost. |

Microsoft explicitly distinguishes [WPF UI and data virtualisation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls). Data paging and off-thread IO are required in addition to efficient item rendering.

### Bounded measurements from this run

One synthetic sequence per input type, Windows 11, Ryzen 9 5900X, approximately 80 GiB RAM, local E: NTFS volume, zstd, explicit legacy file-manifest CLI mode. Timings include process startup; memory was sampled every 10 ms. Cache effects were not controlled. No DB, VSS, mirror, live professional writer, UI or physical-IO counters were included.

| Operation | Random input, initially 256 MiB | Repeated compressible input, initially 256 MiB |
| --- | --- | --- |
| Initial capture | 2.872 s; 807 new chunks | 0.604 s; 1 new chunk |
| Unchanged capture | 1.104 s; 0 new chunks | 0.639 s; 0 new chunks |
| 64 KiB middle edit | 1.097 s; 1 new chunk | 0.701 s; 1 new chunk |
| Append 1 MiB | 1.140 s; 4 new chunks | 0.651 s; 1 new chunk |
| Restore final 257 MiB | 0.416 s; SHA-256 matches | 0.426 s; SHA-256 matches |
| Maximum sampled working set | 72.9 MiB | 45.7 MiB |

The repeated-block input is an extreme favourable dedup case. The CLI rereads even unchanged files; the service's metadata skip is a separate small functional test. A 64 KiB edit producing one new chunk shows storage savings, not proof of cheap source IO. No p95/p99 or 100 GB extrapolation is justified by one run. Raw [measurements](evidence/performance-measurements.jsonl) and [hash verification](evidence/performance-hashes.jsonl) are retained. NEXT-009 specifies the real-service benchmark programme.

## Delivery, documentation and capability status

| Area | Assessment | Evidence and roadmap consequence |
| --- | --- | --- |
| Clean installer | Incomplete current dependency path | `eng/package.ps1:113,119,125` publishes framework-dependent binaries; Burn `Bundle.wxs:12` chains only MSI; MSI starts service at `Package.wxs:61`. No .NET/Desktop runtime or PostgreSQL provisioning in that path. NEXT-011 must prove clean install through verified first restore. |
| PostgreSQL | Runtime implementation, not live integration certification | Core tests inspect SQL/projections; service tests inject in-memory metadata; CLI tests choose legacy mode. Keep R7-001's implementation history but retain R7 validation rows as open gates. |
| Settings semantics | Some controls lack their implied runtime effect | Metadata backup location/retention/service fields persist but have no consuming backup scheduler. “Capture workers / DB writers” sum into connection pool size rather than enforcing those limits. `OptionsWindow.xaml:193–218`; `PostgreSqlMetadataConnectionFactory.cs:19`. Implement or remove/rename in NEXT-008/011. |
| Release evidence | Historical, not current whole-product proof | V1 tracker cites April installer runs; current smoke CLI assumes normal DB mode without disposable DB preparation. Source assertions and MajorUpgrade metadata do not prove installed upgrade/rollback. Staging permits dropping legacy migration work. |
| Supply chain | Documented gates exceed workflow execution | Packaging docs require dependency review/SBOM; release workflow builds/tests/uploads without those gates or installed smoke. NEXT-011 makes the claims and executable gates agree. No separate current vulnerability/licence audit was performed here. |
| UX tests | Broad structural and view-model coverage; visual/runtime gaps | 41 XAML quality and six project-directive tests exist. They do not catch hidden default-size Diagnostics or the reproduced Options crash. NEXT-007/008 add rendered state and real command-flow coverage. |
| Documentation | Extensive but accumulative and partly stale | Quickstart omits DB bootstrap and still describes writer-aware VSS as future; testing strategy lists intended 1/10/100 GB/installer checks beyond executed evidence. Introduce an evidence matrix and edit duplicate claims together. |
| Multi-PC sync | Local foundations; end-user journey incomplete | Transport/trust editing/conflict UI remain planned. Do not read R3 primitive rows as a complete live sync promise. |
| WinFsp, Cloud Files/ProjFS, direct cloud, encryption, fleet | Foundations/deferred execution | Existing typed contracts and SDK references are not delivered user functionality. Keep expansion behind verified local protection/recovery. |

The highest-leverage direction is therefore: **establish the trust contract → repair and prove the first local round trip → make interruption and independent recovery dependable → rebuild task-focused UX → meet measured professional performance → validate installation and pilot use → expand deliberately.** The complete packages and acceptance gates are in the [roadmap](../../improvement-roadmap.md).
