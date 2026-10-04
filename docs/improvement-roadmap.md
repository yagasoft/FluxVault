# FluxVault improvement roadmap

**Decision, 1 October 2026:** rebuild the desktop experience and selectively redesign the parts of the engine that establish trust. Retain .NET, the Windows service boundary, useful WPF infrastructure, content-addressed storage, chunking/hash/codec primitives and the Windows adapters where their contracts can be proved. A wholesale rewrite would discard useful work without resolving the underlying correctness problems by itself.

This roadmap serves professionals protecting Office, CAD/BIM, Adobe and other large working files. The owner has confirmed that FluxVault is in staging, backward compatibility may be dropped, the product is Windows-only for now, and basic protection/recovery must work locally. Cloud-dependent features are acceptable when explicit. The priorities are easier use, a substantially more modern interface, correctness the owner can trust, and serious performance work. There is no fixed delivery deadline.

The [review](reviews/2026-10-01/review.md), [captured UX assessment](reviews/2026-10-01/ux-review.md) and [verification record](reviews/2026-10-01/evidence/README.md) explain the evidence. This programme takes precedence over the order of the older R2–R7 expansion tracks in [roadmap.md](roadmap.md). The [tracker](roadmap-tracker.md) retains historical implementation records and adds the `NEXT-*` delivery packages below. **No package is complete. The first integrity slice is being implemented; its evidence and remaining gates are recorded separately.**

The selected first implementation slice now has a [verified local recovery design](superpowers/specs/2026-10-01-verified-local-recovery-design.md) and [six-batch plan](superpowers/plans/2026-10-01-verified-local-recovery.md). It covers the reproduced content-integrity failures and necessary storage coordination on disposable data. The owner authorised implementation after the model-switch stop. The bounded slice is implemented, deployed and live-validated as staging v1.0.4, with 687 passing tests, real PostgreSQL/private IPC and process-boundary evidence, and installed service/CLI/UI recovery with independent hashes. The user subsequently authorised squash integration and task worktree cleanup. See the [implementation evidence](verification/2026-10-01-verified-recovery/README.md). The broader security, configuration and independent-recovery gates remain open.

## What success means

The product should answer three questions immediately: **What is protected? How much recent saved work could I lose? Can I recover it?** A successful backup means a durable, attributable version with validated references to recoverable content. A successful restore means verified content published to the intended destination, with no damage to an existing destination on failure. A green mirror indicator means the required independently recoverable copies have been verified to the stated level and time.

Local-first means local capture, history browsing, restore, diagnostics and recovery have no account, internet or cloud control-plane requirement. A local PostgreSQL process is compatible with that promise, but its installation, health, maintenance and recovery become FluxVault's responsibility. An internet outage must not block a healthy local backup or local restore.

The first release contract is file/project recovery, not a bare-metal Windows image. Unsaved application memory is outside the backup contract. Distinguish a stable saved file, best-effort live capture, crash-consistent snapshot, writer-covered application-consistent snapshot and a composed folder history. Do not imply that VSS guarantees every Office/CAD/Adobe project is coherent.

## Rebuild boundary and architecture choices

| Area | Recommended decision | Reason and exit condition |
| --- | --- | --- |
| Desktop shell and workflows | Rebuild screen composition, navigation, state handling and interaction patterns in WPF; replace the monolithic view models in coherent slices | Current task flow and layout are the problem. A different framework alone does not fix them. First slice must render the real backup-to-restore journey and pass keyboard/DPI checks. |
| WPF versus WinUI 3 | Keep WPF as the implementation default; allow one bounded WinUI comparison only if a concrete required interaction cannot be delivered well | Windows-only does not require web/Electron or a cross-platform rewrite. Compare deployment, accessibility, native interop, memory and implementation effort before changing framework. |
| Service and IPC | Preserve the process boundary; replace the unauthorised command protocol with explicit caller, vault, operation and result contracts | LocalSystem work cannot inherit authority merely because a process can connect to a pipe. |
| Repository operations | Keep proven algorithms; redesign validated object publication, verified reads, destructive maintenance and recovery ownership | Reproduced corruption and mirror-loss failures are invariant failures, not cosmetic defects. |
| Vault identity and metadata | One logical vault per Windows installation; retain a durable repository identity bound to its chunk root, metadata namespace and recovery bundle | Wrong-repository bindings must fail before read/write/delete. Multiple protected folders, file types and mirrors remain supported; no profile lifecycle or switching. |
| PostgreSQL | Keep provisionally, with real integration tests, indexed bounded queries and managed lifecycle | Live transaction and staging recovery evidence now exists for the verified-recovery slice; scale, lifecycle and independent recovery remain unproved. Do not add a second production metadata engine pre-emptively. Reconsider an embedded store only if measured install/footprint/recovery costs fail the product contract. Record the decision before freezing the new storage format. |
| Configuration | One validated, revisioned configuration owner; explicit updates against the selected vault | Whole-object UI reconstruction currently resets settings. Updates must reject stale revisions and preserve unrelated fields. |
| Background work | One owned lifecycle per vault; one journal reader per volume; durable work admission and explicit cancellation semantics | Avoid a distributed queue or microservices. A local durable work table and structured supervisor are sufficient unless measurements prove otherwise. |
| Cloud/native expansion | Move dormant provider dependencies and execution tracks outside the essential local path | Current cloud SDK payload adds substantial package weight despite deferred runtime execution. |

The governing invariants are: authorised actions only; vault isolation; no acknowledged version with missing/invalid content; no loss of acknowledged change ranges; no deletion of the last required healthy copy; no unverified restore publication; no invisible background failure; and recovery without the original source or primary machine.

Backward compatibility is not an early gate. Freeze a new explicit format/schema version once these contracts are clear. Any later reset of staging repositories remains a separate, deliberate operation; this roadmap does not authorise deleting existing files.

## Delivery sequence

The phases below are ordered by dependencies rather than dates. Design exploration and baseline measurement can run alongside safety work. The rebuilt UI reaches a usable local round trip early; it must not wait for cloud, sync, every advanced setting or every optimisation.

### Phase 0 — Define the protection contract and establish the baseline

**Deliverable:** a disposable corpus and repeatable evidence that make trustworthy behaviour observable (`NEXT-001`). Bring the existing three corruption/mirror probes and the configuration/failed-save probes into meaningful regression tests before changing their behaviour. Add real PostgreSQL integration coverage and a disposable Windows service/IPC harness with no access to the user's data.

Record the representative file sizes/counts, destinations, acceptable saved-work loss and recovery time. Those workload-specific targets are not yet supplied; the performance matrix below is a proposed envelope, not an assertion about the owner's dataset. Define the supported NTFS/ReFS/removable/network/cloud-sync source and destination matrix explicitly, including unsupported paths and file attributes. Define the minimum file metadata fidelity: content, names, hierarchy, timestamps and attributes; decide ACL/ADS/hard-link/sparse/reparse requirements from professional workloads.

**Gate:** every serious finding has a reproducible regression or a specific integration scenario; a healthy disposable file can travel through the existing real interfaces and be hash-checked after restore. Record source revision, hardware, storage, DB and settings with each run. Collect baseline timing before optimising. Work on the repair slice can begin as soon as its relevant failing tests exist; do not require a complete conversion of every fixture first.

### Phase 1 — Prove the first verified local round trip

**Deliverable:** authorised selection → service-owned durable capture → visible version → verified restore (`NEXT-002`, `NEXT-003`, `NEXT-004`). Limit this first slice to one local vault and one file if needed, while proving denial of unauthorised and cross-vault operations separately.

Keep this slice on disposable data until the interruption, coordination and independent recovery gates in Phase 2 pass. One verified round trip is an early executable result, not release readiness.

- Authorise caller, vault and operation at the service boundary. Validate source/destination access, final path containment and reparse behaviour. Bound IPC frames, deadlines and concurrent clients. Use operation IDs for mutations and explicit status after reconnect.
- Publish a validated chunk object whose bytes and metadata cannot disagree. Verify existing content before treating a dedup hit as valid; repair safely from the verified input when appropriate. Establish flush/publication ordering and recover orphaned/unpublished objects.
- Share one bounded verified reader across restore, preview, rehearsal, repair and future sync. Validate digests, decoded lengths, offsets, total length and manifest structure. Stage a destination and publish only after verification; retain an existing destination on error or cancellation.
- Preserve healthy copy counts through mirror copy, rebalance and drain, including corrupt existing targets, capacity exhaustion and interrupted copies. A file's existence is not evidence of health.
- Bind the metadata namespace to `VaultId`; reject a mismatched chunk root. Profile duplication creates a settings draft with newly allocated resources, not a second runtime pointing at the original metadata.
- Save validated configuration patches/revisions, preserve untouched fields, and stop dependent work after a cancelled or failed save. Make pause/resume explicit state commands.

**Gate:** a byte-flipped chunk is rejected; a missing dedup payload cannot produce false success; a healthy mirror can repair the corruption; draining it is refused if it is the last valid required copy; a failed restore leaves the destination byte-identical; a second vault cannot read/delete the first's metadata. Exercise these through real IPC and PostgreSQL, not only repository fakes. Obtain independent security/integrity review of the complete slice.

**Effort checkpoint:** after two coherent implementation batches, require this executable round trip. If it is still missing, narrow to one vault, one local destination and verified restore. Stop expanding UI scaffolding or general abstractions until the blocker is resolved.

### Phase 2 — Survive interruptions and recover without the original machine

**Deliverable:** protection remains accountable through crashes, disconnection and maintenance, and an independent recovery package restores files on a clean environment (`NEXT-005`, `NEXT-006`).

- Advance USN checkpoints only after durable work admission or completed covered capture. Keep retryable work and baseline progress durable. Normalise watcher/USN hints through one cadence policy; retain rename old/new identities and reconcile affected directories.
- Recover native watcher overflow, missing/reappearing roots, journal reset/wrap and unavailable volumes. Display an explicit degraded state until reconciliation finishes.
- Supervise the first unexpected loop exit/fault; cancel and join siblings. Dispose every watcher and join accepted IPC work. Disable/re-enable must never overlap two generations or write after acknowledged stop.
- Enforce one repository mutation owner across service/CLI/processes. Pin versions during restore and during retained recovery checkpoints; coordinate retention, scrub, repair, rebalance and garbage collection against the installation's verified repository binding.
- Drain the metadata outbox after transient export failure, even without a new capture. Validate existing exports and replay idempotently.
- Create an independently stored, versioned recovery checkpoint with vault identity, metadata, required content inventory and deletion semantics. Either pair a consistent DB backup with retained chunks, or implement portable snapshot/journal replay; document which is authoritative. An old DB dump must not reference chunks subsequently reclaimed by retention.
- Provide a local recovery tool capable of locating/importing the recovery material and restoring without the normal service installation. Mirror health must state whether metadata and content suffice for independent recovery.

**Gate:** controlled interruption at publication, checkpoint, replay, copy and delete boundaries; restart produces no false acknowledgements, orphaned active work or last-copy loss. Destroy only disposable source/primary/DB copies and restore from the independent bundle on a fresh environment. Repeat replay twice and test recovery after retention. State the actual recovery point and verification scope. Independent review covers concurrency, garbage collection and recovery.

### Phase 3 — Rebuild the everyday desktop experience

**Deliverable:** a modern, calm native workspace where a professional can protect work, understand risk and recover a version without knowing repository internals (`NEXT-007`, `NEXT-008`). Start visual exploration during Phase 1, using captured current screens and a working real-data vertical slice.

Select a coherent visual direction using the same three tasks in each concept: adding a protected project, answering whether its latest save is safe, and recovering an earlier version. Compare a compact professional workspace, a guided task workspace and a timeline-led recovery workspace. Choose based on task clarity and rendered accessibility, not isolated mock-up appeal. Then implement one consistent system.

| Main destination | What the user sees and does |
| --- | --- |
| Overview | Protection state with last successful and last verified recovery times; saved-work exposure; active work; actionable issues; prominent **Protect a folder** and **Recover files**. |
| Protected work | Projects/folders/files with scope, workload preset, exclusions and last protected save. Add/edit wizard; effective coverage preview. Advanced rules appear only when needed. |
| Recovery | Search and filter by file/project/date; timeline and versions; preview; restore plan and destination; progress and verified result. Deleted/renamed items remain discoverable. |
| Storage | Primary vault and recovery copies, usable capacity, placement coverage, last verification, offline state and safe remove/drain workflow. Distinguish a sync-folder copy from cloud-upload completion. |
| Activity | Durable jobs with phase, progress, retry/cancel rules and actionable failures. Notifications explain impact and next action; successful routine work stays quiet. |
| Settings | Integrated settings with search, clear units, validation, pending changes and save/discard. Useful defaults first; maintenance/cadence/DB/diagnostics in advanced sections. |

Diagnostics and detailed performance remain available beneath health/activity rather than competing with routine navigation. Keep vault switching visible but move create/rename/duplicate/delete into a labelled menu. Use **Stop protecting** to retain existing history by default; place **Delete backup history** in a separate destructive flow with scope/version/capacity preview. This is an intentional revision of the current removal-purges-history requirement, not a wording-only patch.

Essential interaction changes:

- Replace oversized header buttons and emoji/glyph navigation with a compact command bar, a consistent Windows icon set, clear primary/secondary actions and a command overflow menu.
- Replace the tri-state cycle as the primary selection interaction with explicit **Folder and subfolders**, **Files in this folder**, **Selected files**, and understandable include/exclude rules. Keep a live “what will be protected” preview and plain-language rule explanations; offer regex in an advanced editor with examples and match preview.
- Replace always-visible empty pending-change panes with a contextual review drawer. Separate **Save protection changes** from **Back up now**; if combined explicitly, a failed save prevents the backup.
- Integrate settings instead of a tab that opens a modal. Keep unsaved changes through refresh and offer an intentional save/discard choice on navigation. Never clear legacy/custom exclusions silently.
- Show unknown, stale, stopped, disconnected, empty and loading as distinct states. “Checking” must end in a result or an actionable failure. Disable unavailable commands with an explanation and a repair/install route.
- Recovery uses a read-only plan before writing: selected versions, coverage/consistency, destination, collisions, space estimate and overwrite policy. Default to another location; support pause/cancel and a verified completion report. Avoid previewing unsafe file types automatically.
- Use reusable spacing, typography, colour, focus, status and density tokens; support system/light/dark and high contrast. Preserve Yagasoft/FluxVault identity. No fixed-height text that clips with scaling; allow useful window resizing and progressive toolbar overflow.
- Add explicit UI Automation names for composite/icon buttons, labelled inputs, keyboard shortcuts, predictable focus and accessible status changes. Validate keyboard-only flows, Narrator, 100/150/200% DPI, high contrast, long paths, large folders and multiple monitors.

**Gate:** a first-time professional can complete protection → first backup → earlier-version restore unaided in observed sessions; define the small pilot cohort when recruiting. Every core workflow has tested empty/busy/offline/error/cancel/success states. The service-unavailable Options crash and default-size hidden Diagnostics are fixed. UI remains responsive during backup and large-history browsing. Compare rendered screens, not XAML substrings alone.

### Phase 4 — Meet measured professional performance and capture guarantees

**Deliverable:** an evidence-backed workload envelope and foreground-friendly capture (`NEXT-009`, `NEXT-010`). Start baseline measurement in Phase 0; ship measured improvements alongside earlier phases when their integrity gates already pass.

Remove repeated whole-mirror scans per chunk, unbounded manifest loading, per-reference SQL round trips and UI-thread folder enumeration. Batch capacity accounting and DB writes; use indexed paging, asynchronous cancellable discovery, virtualised rows and bounded observable updates. Pool chunk buffers where allocation traces justify it. Read each volume journal once and route events to scopes; avoid unbounded per-event bookkeeping. Isolate deferred cloud SDK dependencies from essential binaries.

WPF rendering virtualisation does not provide data paging by itself; this distinction is documented in [Microsoft's control performance guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls). Validate both query/data volume and visual-tree cost.

Deduplication is a storage saving, not proof of low read cost. Prove unchanged-file skipping with bytes-read counters. For a small edit inside a large file, measure source bytes read, CPU and foreground save delay before considering range journals or a write-path driver. Introduce no WinFsp dependency merely because full-file scanning is expensive; first establish whether cadence, stable capture, batching and ordinary storage improvements satisfy the target.

For open files, add source identity/size/write-time stability checks around live reads, bounded retry and an honest “changed during capture” result. Bound VSS waits/cancellation and prove cleanup at each phase. For linked CAD/Adobe projects, define capture sets with explicit dependencies and one consistency point where feasible. A folder composed from versions captured at different times must never inherit the strongest child's consistency label. Verify metadata fidelity and actually reopen representative restored documents/projects in their applications.

| Test family | Proposed corpus and conditions | Required evidence |
| --- | --- | --- |
| Office | DOCX/XLSX/PPTX; save, autosave, Save As, replace/rename, locked/open files | Semantic reopen check; saved-work exposure; capture interference; exclusion correctness. |
| CAD/BIM | 1–10 GB individual assets and 10–100 GB linked projects, adjusted to the owner's actual tools | Reopen restored project with dependencies; stable capture-set semantics; read amplification and memory. |
| Adobe/video | Layered/image/project files plus compressed media; small edits and appended content | Reopen/content checks; project versus media policy; dedup/compression value and save impact. |
| Scale | 1/10/100 GB file patterns; 10k/100k/1m paths/versions as staged benchmark tiers | List/search/status/retention/scrub latency; bounded memory; query plans; DB and filesystem operation counts. |
| Contention and recovery | Cold/warm cache; multiple protected folders; slow/offline mirrors; nearly full disk; interrupted DB; watcher storms; idle | p50/p95/p99 where sample size permits, queue delay, CPU, IO, working/private/GC memory, handles, outbox lag, correctness. |

Proposed initial UX budgets, to ratify against the agreed workstation/corpus: shell interactive within 2 seconds on a warm start; ordinary navigation/feedback within 100 ms; first page of indexed search/history within 500 ms p95; no individual UI-thread task longer than 100 ms; cancellation acknowledgement within 1 second while safe finalisation may take longer; idle CPU under 1% average over ten minutes on the stated reference machine, with no repeating whole-vault scan. These are design targets, not measured current results or universal hardware guarantees.

Do not invent an absolute GB/s target. Set capture throughput, recovery time and saved-work exposure from the owner's media and acceptable foreground interference. Report capture queue time separately from capture execution and replication delay. Establish memory budgets by job count and chunk size; increasing asset size must not create proportional process-memory growth. Every optimisation run must still pass content and failure invariants.

**Gate:** published reproducible reports using the real service, dashboard and disposable PostgreSQL; multiple trials with cold/warm results separated; byte/application validation alongside performance; agreed budgets met for the stated support envelope. No claims extrapolated from 256 MiB CLI tests to 100 GB professional projects.

### Phase 5 — Deliver a dependable local product

**Deliverable:** a clean Windows installation reaches verified first protection, recovery and support without developer setup (`NEXT-011`).

Choose and implement a complete .NET distribution approach. Provision or detect the local metadata dependency, constrain access, apply schema bootstrap transactionally, and expose failures before claiming protection is active. Either make DB backup settings operational or remove/rename them until implemented; distinguish DB pool limits from actual capture worker limits. Ship a guided storage preflight, service connection repair, disk-space warnings and the local recovery tool.

Validate installer, service identity/ACLs/recovery, normal-user app use, restart, uninstall preservation, update/rollback for the new supported format, and recovery after a partially installed dependency. No legacy-version migration programme is required for staging. Before public distribution, establish signing/release identity, dependency/security review, SBOMs, reproducible release artefacts and observed clean-machine smoke tests. Avoid presenting unsigned historical bundles as proof of current deployability.

Align README, requirements, architecture, storage format, quickstart, Options help, About and the tracker with actual capabilities. Separate implemented code, verified runtime, tested workload coverage and release readiness. Replace wording-enforcement tests as acceptance evidence with real capability checks while retaining useful structural guards. Keep local logs bounded and support bundles redactable, previewable and explicitly exported; no hidden telemetry.

**Gate:** clean disposable Windows environment → install → choose a project and destination → capture → edit → recover → verify → restart → catch up → uninstall with recoverable history preserved. Reinstall/recovery must work locally with internet blocked after prerequisites are available. A small professional pilot records failures, task completion and foreground interference before broader release.

### Phase 6 — Add differentiated capability after the trust gates

**Deliverable:** useful professional advantages without diluting the core (`NEXT-012` and later feature-specific packages).

Prioritise: fast timeline/search recovery; explainable version differences and storage savings; project dependency/capture-set support; automatic verified recovery drills; safe removable/offline copy rotation; capacity/retention forecasting; an actionable explanation of why each file is or is not protected. A useful differentiator is “this project was restored and reopened successfully at this time”, not a decorative health score without evidence.

Keep richer semantic comparison format-specific and optional. Treat churn/ransomware detection as an advisory signal initially; it must not automatically delete, rewrite or silently disable protection. Local immutable naming does not make a writable mirror ransomware-proof. Stronger protection requires independently secured/offline/retained copies and a tested recovery workflow.

Direct cloud execution comes only with explicit credentials, client-side encryption and a tested recovery-key story where required, resumable transfer, remote integrity checks, quotas, cost visibility and honest local-versus-uploaded states. Ship one demanded provider end to end before five partial integrations. Two-way sync needs trusted identity, a known common base, durable idempotent operations, streaming hydration, conflict actions that affect actual content, replay/crash tests and explicit user intent. Backup and sync remain distinct promises.

WinFsp, Cloud Files/ProjFS, fleet management and server/non-Windows support are deferred until a demonstrated requirement outweighs their installation, security and recovery cost. Their existing foundations do not count as end-user features. Never let a driver, cloud account or fleet service become necessary for basic local protection.

## Prioritised implementation packages

Each package is a coherent capability, not an individual test or file edit. `P0` blocks trust in real data; `P1` is required for a dependable professional product; `P2` differentiates after those gates.

| ID | Priority | Deliverable | Depends on | Acceptance evidence |
| --- | --- | --- | --- | --- |
| NEXT-001 | P0 | Protection contract and executable regression/performance baseline | None | Disposable real-interface round trip; fault probes; live PostgreSQL harness; documented support envelope. |
| NEXT-002 | P0 | Authorised, bounded IPC and single-vault repository binding | NEXT-001 | Standard-user/packaged-client denial where appropriate; creator ownership and explicit grants; authorised success; malicious paths and wrong-repository bindings rejected. |
| NEXT-003 | P0 | Verified repository publication, restore and safe mirror maintenance | NEXT-001; NEXT-002 for full IPC gate | Bit-flip/missing-chunk/last-good-copy cases fixed; destination preservation; bounded decode and validated manifests. |
| NEXT-004 | P0 | Revisioned configuration and correct user commands | NEXT-001 | No silent reset/stale overwrite; failed-save cancellation; explicit pause; separate stop-protecting/delete-history; unavailable service remains usable. |
| NEXT-005 | P0 | Durable scheduling, supervised lifecycle and repository coordination | NEXT-002–004 | Kill/restart, journal/rename/overflow/reappearance, cross-process GC/restore and quiescent-stop tests. |
| NEXT-006 | P0 | Independent local disaster recovery | NEXT-003; NEXT-005 | Fresh-environment verified restore without source/primary/DB; replay and retained checkpoint/chunk consistency. |
| NEXT-007 | P1 | Rebuilt shell, overview and protection setup | NEXT-004; design can start with NEXT-001 | Real core flow, truthful states, explicit scopes, accessible/modern adaptive layout, observed first-use completion. |
| NEXT-008 | P1 | Searchable recovery, storage, activity and settings workflows | NEXT-003–007 as relevant | Safe restore plan/result, meaningful health, usable storage management, actionable failures and no dormant settings. |
| NEXT-009 | P1 | Bounded measured performance and foreground resource policy | Baseline in NEXT-001; changes gated by NEXT-003–005 | Real DB/service/UI benchmarks; bounded memory, indexed paging, measured IO/CPU/interference within agreed budgets. |
| NEXT-010 | P1 | Professional capture sets, consistency and Windows file fidelity | NEXT-003; NEXT-005; paired with NEXT-009 | Real Office/CAD/Adobe reopen checks; stable live reads; bounded VSS cleanup; metadata and linked-file contract. |
| NEXT-011 | P1 | Complete installer, dependency/recovery lifecycle and pilot release | NEXT-002–010 | Clean-VM install-to-recovery, offline core, release security/SBOM evidence, current docs and pilot results. |
| NEXT-012 | P2 | Professional differentiation and explicitly optional expansion | NEXT-006; NEXT-008–011 | One validated user outcome per feature; no loss of local-first contract or trust/performance gates. |

## Implementation and review discipline

Use one owner per coherent implementation. Keep a short specification and acceptance matrix alongside each package; test new behaviour/invariants first. Use independent security/integrity/concurrency review at the relevant consequential gates and review the complete final change before closeout. Reviewer acceptance never authorises a live deployment, service restart, migration, driver install or deletion of staging data.

Roll forward in small runnable slices. Preserve a known-good executable and fixture repository for comparison; make format/schema changes explicit and back up any valued staging data before an authorised reset. Where rollback cannot read a new format, say so before adoption and retain a separate recoverable export. Do not dual-write two production storage engines merely to make a staging transition appear reversible.

After two substantial batches, check delivered runtime capability, proven invariants, UI visibility and remaining release gaps. If an invariant fails, stop that affected approach and repair or reduce scope. Do not broaden the programme with speculative abstractions, extra settings, another cloud SDK or driver work while the local recovery path remains unproved.

Completion of the local product requires all P0/P1 acceptance evidence for its declared support envelope, resolution of blocking findings, current documentation and successful pilot recovery. Passing the existing test count alone is insufficient. The selected next implementation slice is **verified local recovery**, covering the content-integrity part of NEXT-003, its NEXT-001 regression harness and necessary repository coordination from NEXT-005. Its private disposable harness does not complete NEXT-002 authorisation or the full Phase 1 round trip, which still requires NEXT-002–004 together.
