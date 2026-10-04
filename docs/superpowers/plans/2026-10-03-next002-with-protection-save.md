# NEXT-002 with the bounded protection-save batch

Status: implementation resumed after the user's approval, 3 October 2026. Batch C is complete in the isolated branch: 722 passing tests, zero-warning Release build, native failure/retry checks and independent review. S1 is underway with reviewed disposable SSPI and authenticated/bounded transport prerequisites; the product vault authorisation/isolation and S1–S3 acceptance remain incomplete. Baseline `afdfe1f`; branch `codex/next002-planning-20261003`. One implementation owner; independent review at the consequential security/isolation gates. No normal-installation change, deployment or merge is included.

[Independent Astra planning review](../../verification/2026-10-03-next002-planning/independent-design-review.md) approved this proposal without blocking corrections. The user subsequently accepted the recommended default: a new vault belongs to its creating Windows user, with explicit access grants for other users or groups. This resolves the review's pending product decision; it does not replace future runtime evidence or authorise implementation during the requested pause.

**Goal:** deliver authorised, isolated multi-vault operations, with protection saves that preserve unrelated settings and never start dependent backup after an unsuccessful save.

**Architecture:** retain the agreed Windows/WPF/.NET/service/PostgreSQL architecture and vault support. Reuse verified repository operations and the real-process/DB harness. Batch C is independently deliverable; S1–S3 retain the complete NEXT-002 gates. D explores the permitted UI journey alongside engineering.

**Spec:** [design and acceptance matrix](../specs/2026-10-03-next002-and-protection-save-design.md). It contains the threat boundary, accepted access policy, direct-DB bypass, namespace proposal, path requirements and recovery constraints. Read it with this plan. The [overall roadmap](../../improvement-roadmap.md) is unchanged.

## Preconditions and fixed constraints

- Use the existing task worktree. Preserve the primary checkout's unrelated code-index edits and local squash commit.
- Apply the accepted creator-only default using the authenticated Windows SID; other users/groups need explicit grants. Do not infer ownership of existing staging data or auto-share a vault. The recorded independent design approval covers the proposed namespace/authentication/path approach; material changes need renewed independent review, and the final implementation review remains required.
- Windows installation and existing toolchain; fresh GUID fixture resources, no separate development environment. All launched test hosts, temporary PostgreSQL instances and helper processes need owned identity, `finally` cleanup and verified exit. Never stop unrelated processes or restart normal PostgreSQL.
- Keep multiple vaults and configurable PostgreSQL endpoints. Two-vault tests must use one database to demonstrate namespace isolation, not avoid the case with separate databases.
- Keep existing verified-recovery invariants and tests. No new production dependency is justified yet. Security bounds are typed/defaulted internal protocol controls; expose only meaningful implemented user settings.
- No staging-data reset, migration, installed HBA edit, service restart or rollout follows from this plan. Prepare exact target/evidence/rollback and obtain required operational authorisation later.
- C does not complete global revisioned configuration, pause/resume or stop-protecting/history-deletion separation. S may implement the minimum binding revision/operation tracking required for its own gate; it does not complete NEXT-005.

## Current evidence and first delivery

At planning time, `tests/FluxVault.App.Tests/ProtectionSaveContractTests.cs` compiled against unchanged runtime code. Nine cases: **eight expected failures, one passing sequencing control**. The failures reproduce both requested defects through actual commands and real stores, including the production profile-store wrapper. The existing 187 App tests passed. [Baseline evidence](../../verification/2026-10-03-next002-planning/README.md). Implementation adds meaningful regressions and makes the batch green: [Batch C evidence](../../verification/2026-10-03-protection-save/README.md).

The original red results are retained as baseline evidence. Do not merge failing tests alone, mark them skipped or invert assertions to make planning look green. First executable delivery is C: select a generated folder → save to disk → reopen settings → run backup only after confirmed save. Its fixture does not invoke the installed service or connect to any database.

## Batch C — Independently deliverable configuration correctness

**Affected files:** `src/FluxVault.App/ViewModels/MainWindowViewModel.cs`; the smallest existing save/status binding in `src/FluxVault.App/MainWindow.xaml`; `tests/FluxVault.App.Tests/ProtectionSaveContractTests.cs`, `MainWindowViewModelRefreshTests.cs`; production store code only if a new test demonstrates a separate cause there. `FileFluxVaultConfigurationStore`, `FileFluxVaultProfileSetStore` and `FluxVaultProfileConfigurationStore` are the test boundaries, not replacement mocks.

**Interfaces:** keep existing public SaveConfiguration/RunBackupNow command surfaces. Change internal `SaveConfigurationCoreAsync(bool refreshAfterSave = true, string? successStatus = null)` to return `Task<ProtectionSaveOutcome>`. The result carries Kind (Saved/Failed/Cancelled/Unknown), captured ProfileId and edit generation. Add observable save state/message for the existing UI. Store a full accepted configuration baseline and apply only actual screen edits with a `with` copy. Later S1 consumes these target/outcome semantics and replaces ProfileId-only routing with bound VaultId/revision.

- [x] Run the retained red suite and confirm the two disk round trips report exactly IsEnabled, RepositoryMaintenancePolicy, Sync, MetadataStore and DiagnosticsPolicy as unintended changes; retain command traces for every unsuccessful-save path.
- [x] Before fixing, add focused regressions for failure explanation surviving refresh, retry after rejection, explicit discard, new edits while save is held, attempted target switch while saving, absent baseline, and saved configuration with failed purge. Include no-backup assertions for every non-Saved outcome. A timeout after an actual store commit must retain the draft and say acknowledgement is unknown, without starting backup or claiming rollback.
- [x] Implement full-baseline preservation. `ApplyStatus` updates that baseline only when applying accepted configuration; `BuildConfiguration` copies it and replaces the fields edited by the dashboard. Verify changed selections and compiled watched folders while structurally comparing every other property after reopening each real store. Seed non-default settings and disabled protection.
- [x] Implement typed save results and persistent presentation. Catch operation cancellation; represent post-dispatch timeout/disconnection as Unknown. Preserve pending edits on failure/cancel/unknown. Capture target/generation and prevent an outstanding save response from clearing or running work for a newer draft/target. A successful save with unresolved purge failure does not automatically dispatch backup.
- [x] Update `RunBackupNowAsync` to proceed only from Saved, dispatch once for the captured profile and retain meaningful results. Audit all `SaveConfigurationCoreAsync` callers for dependent work. Explorer add/remove shares the preservation contract but does not gain new backup behaviour.
- [x] Run new tests, existing refresh/Options/protection-selection tests, then the whole App suite and relevant Core configuration tests. Add a small native fixture check of visible save-error/cancel/retained-draft/retry states; retain process-exit proof. This is a small repair to the current interface, not implementation of the exploratory redesign.
- [x] Review C as a coherent batch. It may be integrated separately after its own green checks and review. State clearly that NEXT-002 and remaining NEXT-004 gates are still open.

**Focused commands:**

```powershell
$env:UseSharedCompilation='false'
$env:MSBUILDDISABLENODEREUSE='1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests/FluxVault.App.Tests/FluxVault.App.Tests.csproj -c Release --filter 'FullyQualifiedName~ProtectionSaveContractTests|FullyQualifiedName~MainWindowViewModelRefreshTests|FullyQualifiedName~OptionsViewModelTests' --disable-build-servers -p:UseSharedCompilation=false -nodeReuse:false
dotnet test tests/FluxVault.Core.Tests/FluxVault.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~ConfigurationStoreTests' --disable-build-servers -p:UseSharedCompilation=false -nodeReuse:false
```

No expected failures remain at C completion. The initial eight failures are evidence of the unfixed baseline, not acceptable release results.

## D — UI exploration alongside C and security planning

**Files:** [journey and state contracts](../../ui-concepts/2026-10-03-next002/README.md), three generated concept PNGs. They use the retained actual app captures as references. Retain WPF and FluxVault/Yagasoft identity; keep vault selection visible.

- [x] Explore compact, guided and timeline-led layouts, with the same three tasks described for each: understand protection, save a selection, recover an existing version.
- [x] Map visible controls to save/backup, permission, vault-target, revision and recovery-result contracts. Separate draft, accepted configuration, captured version and verified output.
- [ ] Select/refine a visual direction with the owner before UI implementation. Correct the documented generated-image inconsistencies; concepts do not override the state contract.
- [ ] When implementing the later UI work, use its existing empty/busy/offline/error/cancel/success, keyboard, Narrator, 100/150/200% DPI, high-contrast and responsiveness gates. This planning deliverable does not claim those tests passed.

Visual selection does not block C or the independent security design gate.

## S1 — First authorised two-vault round trip

This batch is consequential. Read the recorded independent architecture/security review and the accepted access policy before coding after the user resumes implementation. Retain renewed review for material design changes and the final implementation gate. Split into bounded implementation steps if necessary, but do not ship an externally reachable partial bypass.

The [disposable Windows prerequisite](../../verification/2026-10-03-next002-windows-fixture/README.md) passed independent review on 4 October: actual SYSTEM password-free SSPI admission, A/B database and protected-file denials on both loopbacks, complete teardown and unchanged installation. This clears the prerequisite investigation, not the unimplemented product security or normal-installation gates below.

The [bounded pipe transport milestone](../../verification/2026-10-04-next002-pipe/README.md) is implemented and independently approved: connected-handle owner/DACL checks before data, owned effective caller tokens, retained served anchor, bounded framing/admission and joined shutdown. Native standard-user-to-SYSTEM, packaged/remote and vault policy/path/isolation cases remain required. This intermediate branch is not deployable until the authenticated vault dispatcher is composed and the full gates pass.

The [catalogue/admission milestone](../../verification/2026-10-04-next002-authorisation/README.md) uses independently approved PostgreSQL persistence: fixed protected bootstrap, coherent transactional permissions/bindings/configuration, durable operation receipts and bounded summary discovery. Forty real-store contracts pass in the owned SYSTEM SSPI fixture; policy actors are doubles, and no native vault file-access or full S1 result is claimed. The dispatcher remains uncomposed until its protected runtime executor exists. Same-database metadata namespace and caller-authorised source/output work are the immediate blockers to the first safe round trip; the acceptance gates remain unchanged.

**Source map:**

| Boundary | Existing files | New focused contracts/helpers proposed |
| --- | --- | --- |
| Windows caller and framed IPC | `src/FluxVault.Core/Ipc/NamedPipeFluxVaultServer.cs`, `NamedPipeFluxVaultClient.cs`, `IFluxVaultRequestHandler.cs`; `src/FluxVault.Service/Program.cs` | `FluxVaultCallerContext`, `AuthenticatedFluxVaultRequestHandler`, `FluxVaultIpcLimits`; Windows token/path helpers under `src/FluxVault.Windows/Security/` |
| Identity, policy and runtime routing | `src/FluxVault.Abstractions/Configuration/FluxVaultProfileConfiguration.cs`; `src/FluxVault.Core/Service/FluxVaultProfileManager.cs`; profile stores | `VaultId`, `VaultBinding`, service-owned `VaultAccessPolicy` and catalogue under Core Security; bind profile → vault without using display name |
| Metadata and owned storage | `PostgreSqlMetadataSchema.cs`, `PostgreSqlRepositoryMetadataStore.cs`, `PostgreSqlMetadataConnectionFactory.cs`; `StorageOwnership.cs`, `RepositoryLeaseSet.cs`; repository/store construction in `FluxVaultOperations.cs` and `FluxVaultCli.cs` | `VaultMetadataNamespace` derived from validated VaultId; vault binding in primary/mirror markers and exports |
| Source/output access | `FluxVaultOperations.cs`, `FileSystemChunkRepository.cs`, Windows capture adapters | Owned caller-authorised handle/access-grant contract; background revalidation, final-handle containment through publication |
| Client targeting | `FluxVaultIpcRequest.cs`, `FluxVaultIpcResponse.cs`, `MainWindowViewModel.cs`, CLI | Explicit VaultId, binding/configuration revision, operation identity, stable result/error classes |

**Boundary interfaces to establish:** `VaultId` is a validated UUID value, `VaultBinding` contains ID/physical root/metadata namespace/revision, and `FluxVaultCallerContext` owns a verified token and disposes it. An authenticated dispatcher consumes context + request; the old untrusted handler overload cannot be a public bypass. `IVaultAuthorizer.Authorize(context, binding, permission)` returns an authorised operation scope or typed denial. Repository/metadata constructors require a binding for service-managed storage; internal fixture/direct modes are explicit. Exact private helper signatures are implementation choices after the Windows access proof; do not invent an async impersonation shortcut.

- [ ] Establish executable denial tests first: forged claimed SID/elevation, omitted/wrong VaultId, forbidden vault discovery and pipe impersonation failure must invoke no repository handler. Prove the accepted default with real Windows users A and B: A creates a vault and receives its permitted owner operations; ungranted B cannot discover or operate it; an explicit authorised user/group grant allows only its specified permissions and remains effective after service restart. OwnerSid must come from A's verified token, never the request. Exercise the actual pipe security, including packaged-client and remote-client cases. Unit token doubles supplement these tests only.
- [ ] Establish the direct-access control in a disposable installation: service Windows principal can authenticate password-free to its permitted PostgreSQL role; an ordinary user cannot; protected catalogue/configuration/content paths deny direct access. Rehearse SSPI mapping with installed binaries and owned resources. If prerequisites require temporary Windows accounts/elevated fixtures, prepare named resources, teardown and user authorisation before creating them; lack of this evidence keeps S01/S05 incomplete.
- [ ] Add VaultId/binding and scoped namespace DDL/queries. Test two repositories in the same real DB using colliding source paths, version IDs and digests. Validate markers, namespace and root before metadata reads/writes. Include rollback/unknown-acknowledgement cases from the existing integrity suite.
- [ ] Prove source/output permissions through actual ACL and handle tests before exposing privileged file work. Cover newly restricted children, path-component replacement during operation, preview/export destinations and scheduled capture when the principal has logged off. Do not treat a successful directory AccessCheck as authority over later descendants.
- [ ] Carry batch C's captured target through explicit VaultId and revision. Save → capture → list → verified file/folder restore succeeds for authorised A; B cannot inspect, delete or restore A. Add a normal authorised switch between both vaults to prove supported multi-vault behaviour still works.
- [ ] At the effort checkpoint, demonstrate the executable result and document any blocked gate. Retain this isolated build if security is incomplete; do not compensate by disabling second-vault coverage or bypassing auth in the real-service path.

## S2 — Complete the command surface and interruption semantics

**Files:** remaining request dispatch in `FluxVaultOperations.cs` and `FluxVaultProfileManager.cs`; IPC framing/client/server; metadata namespace and scoped operation records; status/diagnostics aggregation; CLI and VM target/result handling. Add focused `VaultAuthorizationTests`, `PostgreSqlVaultIsolationTests`, `VaultMutationIdentityTests`, `VaultPathAccessTests` and Windows identity test-host modes using existing test-project conventions.

- [ ] Maintain a total command-policy table keyed by `FluxVaultIpcCommand`; unknown/new values fail closed. Read/status/activity/performance/health and previews receive authorised filtered data; recovery/preview need Recover; configuration/capture/pause/conflict commands require their explicit mutation permission; retention/purge/drain/delete require destructive rights in addition to ordinary maintenance rights. Profile lifecycle and access management cannot target unowned resources.
- [ ] Complete cross-vault isolation for all metadata tables, folder/lineage references, current entries, repair/rebalance/drain, retention/purge, activity/logs, outbox/journal and per-vault in-memory caches. Source-string SQL assertions are not acceptance proof; use real reads/mutations and byte/row snapshots of the other vault.
- [ ] Complete duplicate/create/delete/rename/switch tests, including attempted metadata/root rebinding, profile deletion during save and concurrent active-vault changes. Allocate independent identity/state/mirror resources; retain the other vault unchanged on failure.
- [ ] Add bounded reader/writer/concurrency policy and client verification. Oversize, deep JSON, slow/stalled peers and queue saturation produce bounded resource use and clean cancellation; legitimate requests still progress. Use the spec's typed defaults and measured fixtures. Response paging preserves complete inventory semantics rather than silently clipping it.
- [ ] Add durable operation identity/result records scoped by initiating identity/vault/command/fingerprint. Test duplicate requests, different payload under the same ID, disconnect after durable save, process death near a destructive commit and status reconciliation after restart. No automatic replay of an uncertain destructive side effect.
- [ ] Bind user source/output access for every remaining file-producing/consuming command. Join accepted IPC work on stop, release all duplicated tokens/handles and verify owned child processes exit. Broader watcher/USN lifecycle changes stay outside this slice.

## S3 — Combined verification and independent closeout

- [ ] Run narrow meaningful families first, then zero-warning Release build and the existing owned PostgreSQL full-suite runner with all new tests included. No database/identity/process case may silently skip. Existing core integrity tests remain required.
- [ ] Run actual Windows identity/packaged-client/direct-DB/ACL/path-race tests in the prepared disposable installation, plus native allowed and denied save/recovery flows. Verify the UI preserves drafts and never treats a permission failure as an empty vault or a successful backup.
- [ ] Compare both vaults' independent recovery hashes and database/content snapshots after destructive-denial and allowed-maintenance tests. Exercise same-database isolation, not just two independent DB endpoints.
- [ ] Record C01–C03, S01–S08, D01 and G01 status with exact evidence. Request one independent Astra/equivalent review of the final coherent implementation, source, tests, migration/rollback and observed gates. Resolve blockers and rerun affected checks only.
- [ ] Verify process cleanup, preserve evidence and state the remaining roadmap items. An authorised deployment is a separate operation requiring concrete target, rollback and live probes; no implicit normal-service restart or PostgreSQL migration.

**Combined commands, when implementation is ready:**

```powershell
dotnet build FluxVault.slnx -c Release --no-incremental --disable-build-servers -p:UseSharedCompilation=false -nodeReuse:false
./eng/test-repository-integrity.ps1 -PostgreSqlBinPath 'D:\Program Files\PostgreSQL\18\bin' -RunFullSuite
git diff --check
```

The existing runner owns its temporary cluster and leaves normal PostgreSQL untouched. Windows access tests need a separately documented identity fixture; the existing current-user private pipe is not proof of LocalSystem-to-standard-user security.

## Delivery checkpoints and scope control

| Checkpoint | Deliverable | What remains open |
| --- | --- | --- |
| Planning now | Source inspection, real red regressions, accepted creator-only access default, independently reviewed spec/sequence/gates and three visual concepts | User's model switch and instruction to resume; runtime fixes, visual selection and all security runtime gates |
| C | Both requested configuration defects fixed and verified independently | Full NEXT-004 and all unproved NEXT-002 gates |
| S1 | Real authorised two-vault round trip with safe identity/path/storage boundaries | Remaining command families and interruption cases |
| S2/S3 | Full NEXT-002 acceptance and regression evidence, reviewed implementation | Later lifecycle, independent disaster recovery, complete UI, professional performance and release gates |

Do not rewrite or reorder the programme to accommodate this batch. If S1 remains too large after a coherent effort checkpoint, deliver C on its own and keep a separately planned security branch with every gate retained. Do not mark NEXT-002 complete because C passed or because the mock-ups exist.

## Model-switch handoff

The requested pause ended when the user approved implementation after the model switch. No model change is claimed or performed by this plan. Continue in this existing worktree using the spec, plan and retained evidence; do not repeat the completed audit, regenerate concepts or reopen the accepted access-default decision without new evidence.

Batch C is complete in this worktree; preserve its red/green and native evidence. The user authorised the [named Windows fixture](../../verification/2026-10-03-next002-windows-fixture/README.md) on 4 October; its reviewed disposable rehearsal passed with complete teardown. The reviewed transport prerequisite is also implemented. S1–S3 product security and their runtime gates remain incomplete. C can be delivered independently without weakening NEXT-002. A visual direction still needs selection before the broader UI implementation, but does not block C or security work. Normal deployment or installed PostgreSQL changes retain their separate concrete authorisation gate.
