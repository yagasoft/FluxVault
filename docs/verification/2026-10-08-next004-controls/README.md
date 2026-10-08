# NEXT-004 protection controls execution

The owner approved/resumed after the model-switch planning pause on 8 October 2026. The [agreed plan](../../superpowers/plans/2026-10-08-next004-protection-controls.md) and its independent design review govern implementation, installation and live acceptance. NEXT-005 is a separate following release. Normal PostgreSQL and unrelated data/changes must remain untouched.

## Installed acceptance and bounded corrective release

**Current state:** corrective 1.0.9 is installed and its bounded live acceptance
is independently accepted; Git integration remains open.
The update exited 0 and was joined. The independent
concrete-candidate gate approved candidate `3da720718ab04b99afdf43455222e0cf`,
its sealed procedure and guarded fallbacks with no blocking finding.
`corrective-update-verified.json` observes its exact MSI registration, 208 matching
installed payload hashes and FluxVault service PID 26864 running as LocalSystem.
`corrective-preservation.json` compares complete authenticated before/after
snapshots: vault `67539278-9292-4870-93cd-a011419228dc`, revision **13**, full
configuration and history are unchanged. PostgreSQL retains PID 10660 and its
6 October lifetime; bootstrap, authentication and ACLs are preserved. This
section supersedes the historical intermediate gate statements below without
discarding their evidence. NEXT-005 product implementation has not begun.

Independent GPT-6-Astra/high final assessment (`review_protection_design`,
8 October) **approves bounded NEXT-004 installed acceptance and the complete-branch
technical gate, with no remaining requirement-backed blocker**. It accepts the
installed cancellation/deletion/recovery, original configuration/history,
PostgreSQL/authentication/ACL/unrelated-change preservation, exact 1.0.9 payload,
changed layouts and joined cleanup. Prior design/code/compatibility/candidate and
guarded rollback approvals remain applicable. NEXT-009's measured limitations
remain open; NEXT-005 is unimplemented and separate. This is technical assessment;
the owner's existing post-resume standing authority covers the in-scope rollout.

Installed WPF review of the stopped synthetic Source retains its exact two-version,
119-byte preview across more than nine seconds of periodic status refresh.
The separate confirmation names the exact scope, count, size and irreversible
effect. Cancel admits nothing: `installed-cancel-preservation.json` compares
complete authenticated snapshots and confirms equal vault, revision,
configuration and history, with no pending operation. Confirmed deletion removes
only the two reviewed Source versions; all 18 other versions, configuration and
revision remain equal and the live source hash is unchanged
(`installed-deletion-preservation.json`). Protected Neighbour review refuses
deletion with its overlap reason and disabled Delete control, surviving more
than 22 seconds of refresh (`installed-protected-neighbour.jpg`).

After deletion, installed WPF restores the retained Neighbour file to a new
destination. Independent SHA-256 matches the 1 MiB original
(`installed-post-deletion-recovery.json`). Stop protecting Neighbour, then Save,
retains its history and returns the **complete original four-scope configuration**
with protection enabled at revision 14; no draft/save/backup remains pending.
Fresh `installed-final-status.json` and `installed-final-preservation.json`
confirm all 16 original versions and the two retained Neighbour versions,
unchanged unrelated dirty files and original PostgreSQL lifetime.

Changed Pause/Resume, Stop/Save and reviewed-deletion layouts were inspected in
the actual installed desktop at 175% and 200% scaling. The controls remain
visible, explanatory text wraps and the Stop/Save toolbar wraps without clipping.
`installed-*-controls-200.jpg`, `installed-history-controls-175.jpg` and
`display-restored-175.jpg` retain the observations. Keyboard Tab/Return saves the
stopped selection. Theme/accessibility work remains deferred. The original 175%
display setting is restored; owned Settings exited and the idle desktop was
terminated and joined after checking no pending records. No graceful desktop
shutdown is claimed. `installed-ui-cleanup.json` and the final joined read-only
snapshot process record retain cleanup evidence. The production service remains
running intentionally; `installed-final-readonly-check.json` freshly verifies
1.0.9 registration, all 208 payload hashes and bootstrap/authentication/ACL pins.

`corrective-startup-observations.json` records three fresh finite observer
processes, each exited 0 and joined, using 63 hash-verified frozen candidate App
files overlaid onto the existing TestHost runtime. Readers and draft writers were
joined. On the stated 24-logical-processor workstation with 20 retained versions,
first content rendered in **1565.39, 1482.31 and 1489.96 ms**. Caches were
uncontrolled after prior product/build activity; these are not cold-start trials.
Each observes one startup dispatcher task of **539–549 ms**, missing NEXT-009's
proposed 100 ms target. Independent Astra/high assessment treats this as a
documented NEXT-009 limitation, not an added NEXT-004 blocker on this evidence.
It establishes neither a baseline regression nor full performance acceptance.
The fresh installed App observation records **620.37 seconds** with no Sky
window-state captures: App **1.540%**, service **0.0169%** machine CPU, normalised
over 24 logical processors (`idle-fresh-observation.json`). App working set rises
from 214.4 to 225.3 MB and handles fall from 605 to 594. External automation
isolation is **not proved**; `NoUiAutomationAttached` in the initial observation
means no task capture was attached, not a census of external clients. A subsequent
15-second joined trace shows automation-provider stack activity but does not
establish its CPU contribution or excuse the measured target miss. Independent
Astra/high assessment leaves NEXT-009 performance acceptance open and finds no
additional NEXT-004 blocker on this bounded evidence. These observations are
neither a predecessor regression comparison nor a full idle/performance pass.

The authorised 1.0.8 update exited 0 and was joined. Its protected `controls-update-1.0.8/Update-verified.json` observes exact MSI registration, 208 matching installed payload hashes, FluxVault service PID 27768 and unchanged bootstrap/authentication/ACLs. Normal PostgreSQL remains PID 10660, started 6 October 09:31:44 UTC. Complete authenticated `installed-before.json` / `installed-after.json` configurations are equal; vault identity and revision 8 are unchanged by installation.

Actual installed WPF acceptance observes Pause/Resume retaining an unsaved source-selection draft, disabled backup while paused, then a non-purging save of two owned synthetic folders at revision 11. Every configuration field outside the intentionally changed selection lists remains equal to the baseline. Manual backup reports two captured and eight unchanged files, no failure. `installed-neighbour-recovery.json` independently matches the 1 MiB source/recovery SHA-256. The named Stop action stages removal, Save keeps history, and `installed-stopped-source-recovery.json` independently matches the 119-byte source/recovery SHA-256 after stopping protection. Saved protected neighbour history refuses deletion with a visible overlap reason. No deletion has been admitted yet.

Live validation found an unchanged periodic status refresh clearing the bound history grid and invalidating its reviewed deletion preview. The actual rendered WPF regression `red/history-refresh-red.trx` reproduces that failure (one failed unchanged-inventory case; changed-inventory invalidation passed). The correction retains equal immutable rows for the same vault/storage identity; changed inventories and identities still rebuild and require fresh review. `green/history-refresh-green.trx` passes all 217 affected protection-save/refresh tests, zero skipped; TestHost builds with zero warnings/errors. Independent GPT-6-Astra/high review approves this bounded correction with no blocking finding. The existing native actual-VM probe now checks unchanged authenticated refresh with both protected and deletable reviews; its fresh run and a frozen 1.0.9 candidate gate remain required. 1.0.8 is deployed, **not accepted**; NEXT-005 implementation remains unstarted.

### Corrective candidate and current gate

Fresh native fixture `a142d7b261b24fbfbd3d0a964bd338d9` (`native-history-refresh`) exited 0 and was joined: 184 catalogue checks on each protocol, 163 native creator checks, 190 SYSTEM checks and 21 denied native B commands. Both actual rendered protected and deletable previews survive unchanged authenticated refresh. All three cleanup/preservation predicates are true.

Source `0a0246bcbc10966ab8e9e7981564bebb61705164` freezes corrective candidate `3da720718ab04b99afdf43455222e0cf`, **1.0.9.0**, ProductCode `{67CE8AD2-72FD-49A7-89B3-44A670060428}`. Five build/publish processes exited 0 and joined with zero warnings; 208 payload hashes and packages are retained outside the task worktree beside the earlier releases. Manifest SHA-256 `DA9887839FA9B1DE158747E3FF575BE34DFB03C22A66BDB14F4ED62D037E456B`; MSI `0C34E9D82E729F16345B88974E328F1BD0E1F3F9761CA391DB44F2550D028CF4`; setup `DE561816FD509ED6EC4A68FDDA084F4E46551C539EDB3C9B5F80DECC07C66C49`.

`update-controls-refresh.ps1` seals 1.0.8 → 1.0.9 and reuses the independently reviewed scoped/joined service, MSI receipt, preservation and guarded rollback functions. Its immediate frozen 1.0.8 fallback keeps manual recovery but reintroduces the known preview refresh defect; the accepted 1.0.7 fallback remains available via `update-controls.ps1` and its original reconciliation guards. Neither direction restores a database or reverses history deletion. The first Check refused a running desktop without any installer/service/DB action (`corrective-candidate-check-refused.txt`). After owned App PID 38228 exited and was joined with no pending draft/save/backup, `corrective-candidate-readonly-check.json` passes exact installed payload, both actual MSI identities/tables and original PostgreSQL/bootstrap/authentication/ACL preservation. Predeployment independent review remains required before update.

The 652.93-second attached-automation observation records 2.584% machine CPU for App and 0.0058% for service, normalised over 24 logical processors. It is explicitly **not an idle pass**: a bounded, joined CPU trace observes automation-provider/dispatcher/finalizer work after repeated UI captures. A fresh process without UI Automation capture is needed to distinguish product idle from observer effects. Raw trace stays local under ignored `artifacts`, outside durable public evidence. The initial calculation mixed PowerShell's automatic JSON date conversion with timestamp strings; `idle-automation-observation.json` corrects it from the preserved UTC strings. Existing normal-startup observation is extended only with timing/dispatcher/CPU counters for NEXT-009 measurement; these TestHost-only edits do not change the frozen candidate.

## Batch 1: explicit controls and retained history

In progress. Test-first evidence: `red/explicit-state-behaviour-red.trx` reproduces explicit Pause/Resume being treated as toggles, plus an unreceipted legacy toggle being accepted. `red/stop-retains-history-red.trx` reproduces selection removal prompting to purge history. All four regressions fail before the corresponding implementation; test processes returned exit 1 and were joined. The earlier `explicit-state-red.trx` exposed a missing intermediate-state assertion, corrected before the recorded behaviour run.

The former removal-confirmation test now asserts the deliberately changed contract: deselection saves without a purge prompt or purge request. Legacy pending-save/purge reconciliation tests remain required, together with independent deletion-cancellation coverage in Batch 2. No deployed capability is claimed yet.

### Observed focused checks and milestone review

- `green/explicit-state-green.trx`: 3 core cases passed; explicit desired-state repetition and omitted legacy rejection, using the real file configuration store.
- `red/controls-ui-red.trx`: the three Pause/Resume UI cases failed because the commands did not exist; the changed stop-protecting case passed. `green/controls-ui-green.trx`: all four passed after implementation.
- `green/controls-preservation-first.trx`: 173 passed, three old save/purge expectations failed. `green/controls-preservation.trx`: 204 passed, three further old File browser confirmation/purge expectations failed. These assertions are being revised for the explicitly agreed non-purging save contract; old pending purge records and uncertain receipt cases remain covered. The real local-draft restart cases passed for both confirmed and lost pause acknowledgements.
- Native preflight initially refused the retired `C:\ProgramData\FluxVault\config.json` assumption before creating resources. `red/current-bootstrap-snapshot-red.trx` reproduces it. The runner now hashes the current `installation.json`, preserves explicit absence of either product file, and still fails when neither exists. `green/current-bootstrap-snapshot-green.trx`: both focused snapshot/trust cases passed.
- `native-batch1` fixture `d24d4973880a482b8a6c2863b4457b12` failed at historical receipt replay because the new **test fixture** used numeric enum encoding rather than the catalogue's historical string encoding. The independent reviewer identified the mismatch and the real store confirmed `OperationConflict`. Corrected to `JsonStringEnumConverter`; a fresh run is required. `cleanup.json` records `OwnedJobsJoined`, `InstallationUnchanged` and `RootRemoved` all true; owner state is Complete with no remaining resources. Runner exit 1 was observed and joined.
- Independent GPT-6-Astra/high milestone review (`review_protection_design`, 8 October): **approved approach**, no remaining blocking defect in the reviewed Batch 1 code. It assessed explicit state, atomic revisions/receipts, historical request shape compatibility, non-purging saves, local editing draft retention, the mutation gate and capture-before-enumeration guard. Corrected native and broader App evidence, complete-branch review, concrete candidate/rollback review and installed acceptance remain open. This is not deployment approval.

No installation or normal PostgreSQL operation has occurred. The fixture's bootstrap preservation change is necessary to run the existing acceptance machinery against the commissioned 1.0.7 installation; it does not relax authentication, process ownership or teardown checks.

### Batch 1 end-to-end checkpoint

`green/controls-preservation-green.trx`: all 207 focused App cases passed, zero skipped. Release TestHost builds returned zero warnings/errors. `red/legacy-receipt-codec-red.trx` reproduces the remaining JSON timestamp-escaping defect in the historical **fixture**, independently of PostgreSQL; `green/legacy-receipt-codec-green.trx` proves exact request bytes and the catalogue's actual fingerprint for ordinary save, legacy save/purge, legacy toggle and backup. The second native attempt `native-batch1-corrected` was also safely torn down (`cleanup.json`: three true postconditions, owner Complete, no resources remaining).

Fresh fixture `4ef9422b6c0c4622aa6be2604b4a1102`, retained in `native-batch1-final`, returned exit 0 and was joined. Catalogue contracts passed **133 checks on each of IPv4 and IPv6**. Native creator workflow passed **149 checks**, and the SYSTEM composition/provisioning workflow passed **164 checks**. The creator proof includes actual rendered WPF Pause/Resume bindings → authenticated private pipe → real catalogue, dirty draft retention without configuration save, desktop/VM restart, explicit save → capture → history → independently SHA-256-verified recovery. It also proves stop-protecting retains CAD history and independently recovers it; pause performs no source enumeration/capture; resume captures the pending bytes. The WPF dispatcher thread and draft writers were joined.

`cleanup.json` confirms `InstallationUnchanged=true`, `OwnedJobsJoined=true`, `RootRemoved=true`; the before/after snapshots retain the same normal PostgreSQL and installed FluxVault service identities, authentication files and installation bootstrap. This establishes the first safe observable result in the private fixture. Installed acceptance remains pending. Later small UI availability changes and Batch 2 require their affected checks before release.

## Batch 2: deliberate, reviewed history deletion

The existing repository lease, graph validation, purge engine and catalogue receipts now support a separate preview and confirmed deletion. The server derives preserved scopes from saved protection, binds a complete bounded preview to the vault/revision/manifest graph and refuses protected or externally referenced history. Cancellation has no admission. Partial effects retain the original uncertain receipt; neither desktop restart nor receipt checking replays deletion. Reads and recovery remain available. Fresh destructive work is refused while a deletion is admitted but unresolved.

`red/history-ui-red.trx` records the missing controls; `red/history-mutation-guard-red.trx` reproduces the desktop maintenance bypass found at independent review. The catalogue gate was added and tested through real PostgreSQL: another deletion, retention and a retention-enabling save have no fresh receipt while deletion is admitted. Original receipt lookup and read-only preview still work. Completed replay, operation collision, malformed requests, stale revision, wrong binding and all permission combinations are covered by `VaultHistoryDeletionProbe`.

`green/reviewed-deletion-complete.trx` passes 23 repository/lineage cases, including changed-preview refusal, inherited outside references, protected overlap, the 257-version incomplete-preview bound and independently recovered shared chunks. The ordinary full Core suite covers the existing purge/retention cases as well. New controls preserve the real configuration-store and actual-view-model regression suites.

Native fixture `bfc3ac1fe12246f0b64cc60e47bac1cd` (`native-deletion-guard-red`) failed as intended before the server guard. Fixture `d7e3c9062d214d81a37018b3c4186146` (`native-deletion-green`) passed product checks but the aggregate still expected 19 denied commands rather than the new 21. That fixture assertion was corrected without weakening a product assertion. Both runs exited 1, were joined and recorded all three cleanup/preservation postconditions true.

Fresh fixture `5bf980ea78a34f4ba92c21c292e1b8bf` (`native-deletion-final`) returned exit 0 and was joined: **184 catalogue checks on each of IPv4 and IPv6, 161 native creator checks, 186 SYSTEM checks and 21 denied native B commands**. The actual WPF Stop action stages a non-purging save; saved history remains independently recoverable. Deletion preview refuses protected history; cancellation admits nothing; confirmed deletion loses its acknowledgement only after the real service has completed. Restarted actual view model checks the original receipt, preserves independent dirty edits, removes exactly the reviewed synthetic history and independently recovers neighbouring content. The live synthetic source remains unchanged. WPF threads/writers are joined. `cleanup.json` again records installation unchanged, owned jobs joined and root removed.

The reviewer subsequently found that an explicit file beneath an inherited protected folder needed an exclusion when stopped. `red/stop-inherited-file-red.trx` reproduces it; the correction is covered by the real saved matcher, including continued neighbour eligibility. The capture description now honestly describes best-effort reads and does not promise automatic capture or VSS.

### Combined checks and final code review

The ordinary solution command excludes only `RequiresPostgreSql` and `RequiresPreparedWindowsInstallation`: observed exit 0, **App 502, Core 578, Integration 337 and Windows 154 passed**, zero failures/skips. The shared TRX filename was overwritten by each assembly: `combined/ordinary-core.trx` retains Core and `combined/ordinary-windows.trx` retains Windows. App/Integration totals are observed terminal output, not retained separate TRX files; do not attribute those totals to the overwritten file. This bookkeeping limitation does not justify repeating unchanged suites.

After the final file-overlap/status changes, `green/final-control-corrections-green.trx` passes all 184 affected App cases, including the newly added independent recovery-preview case during uncertain deletion. The first narrow invocation (`final-control-corrections.trx`) matched no tests because these files extend `ProtectionSaveContractTests`; it is not a passing test claim. `tooling/rollback-context-red.trx` reproduces lost frozen product identity; `tooling/rollback-context-green.trx` passes nine presentation rollback checks after forwarding the context. The existing builder now accepts a validated MSI version while preserving its 1.0.7.0 default.

Fresh integrity fixture `d2bdbed4788544a1a0eb599dc856bc1e` (`native-integrity`) returned exit 0 and was joined: **40 real PostgreSQL integrity cases passed, zero skipped**. All three cleanup/preservation postconditions are true. No normal PostgreSQL or unrelated database operation occurred.

Independent GPT-6-Astra/high final code review (`review_protection_design`, 8 October): **NEXT-004 code gate approved, no remaining requirement-backed blocker**. It closed the destructive-work admission, inherited-file Stop and capture-wording findings and accepted the recovery-preview regression and pending messages. The evidence bookkeeping correction above is non-blocking. Exact candidate/MSI/rollback technical review and installed live acceptance remain separate open gates. NEXT-004 is not yet deployed; NEXT-005 implementation has not begun.

## Frozen 1.0.8 candidate and rollback preparation

Product source commit `11f9a915f83f4f56b2d0b152947145491d290e7b` built candidate `ddc02916cbfa431a9360f07be9ca3eaa`, MSI version **1.0.8.0**, ProductCode `{890BF985-71FD-44FA-A11D-349285CDEF96}`. All five publish/build processes exited 0 and were joined, without build warnings; the manifest contains 208 payload hashes. The manifest and packages are retained under `C:\ProgramData\FluxVault.Commission.7871ff7f8d1b404db20771f2e742364f\retained-candidates\ddc02916cbfa431a9360f07be9ca3eaa`, outside the task worktree.

- Manifest SHA-256: `ED218743B8566822405EE0C9447ADB246A6B43A00352B0E73895E6016717D37E`.
- MSI SHA-256: `F2E9791DD0BE2A5E5E407D2A9E6DBF5940F8AE2FBA07456DE2244FDC1266A7AD`.
- Setup SHA-256: `38DA0A538A1BFFFDE03E5B6B12B97AD28357560074366666EF961B0A3A5A04F7`.

`candidate-readonly-check.json` verifies the still-installed accepted 1.0.7 payload, both retained package identities/hashes, actual MSI tables, original PostgreSQL lifetime and bootstrap/authentication/ACL pins. The reviewed service boundary remains LocalSystem/manual start during installation, with no MSI custom actions. `update-controls.ps1` reuses the accepted operator functions with frozen 1.0.7/1.0.8 identities and explicitly joins the stopped FluxVault process. The immediate rollback is accepted 1.0.7 candidate `c4e807860b1c49e798b2380652ca5926`, not 1.0.6. No database/history rollback or PostgreSQL restart is part of either direction.

The initial candidate review accepted the pins and MSI boundaries but **required a rollback correction before deployment**: the predecessor's strict client reader rejects new pending pause/deletion records, including a completed deletion whose acknowledgement was lost. The procedure now refuses downgrade until all pending client save/control/deletion records are reconciled through 1.0.8, preserving every record unchanged. It also stops/joins FluxVault before a read-only authenticated SYSTEM catalogue check and refuses admitted unresolved deletions. This fixed normal-installation rollback is deliberately limited to its verified sole creator; additional grants or a different owner refuse downgrade pending checked reconciliation of the relevant client profiles.

`tooling/controls-client-rollback-gate.trx` passes 14 focused rollback cases. The real `rollback-readonly-inspection-corrected.json` observes zero admitted deletions, the intended creator and zero additional grants. Its worker and psql exited and joined; its task was removed. The earlier generic psql-path trust check refused before any privileged actor launch. The correction reuses the existing, independently accepted native PostgreSQL volume-root path verifier without changing an ACL, authentication file or service. No actual normal-vault rollback is claimed.

The opt-in predecessor-read extension to the existing owned catalogue fixture closes the required reader check. The 63 predecessor App payload files were copied from the still-installed accepted 1.0.7 binary set into its protected retained-candidate directory; every file matched the frozen manifest. The extension loads those actual predecessor types in an isolated assembly context and exercises real catalogue row readers, local pending/draft readers and repository history/recovery. It does not launch or install the predecessor service.

The first fixture `6b93c206b3b244f995c78be501b3c517` (`native-predecessor-read`) correctly refused stale TestHost evidence because its required completion flag was absent. Exit 1 was observed/joined; all three cleanup/preservation postconditions are true. After a fresh TestHost build with zero warnings/errors, fixture `1ac1a02e41f34c1fa71a3cfbe9cc41eb` (`native-predecessor-read-current`) returned exit 0 and was joined. **193 catalogue checks passed on each of IPv4 and IPv6, with `PredecessorReadVerified=true`.** Nine actual predecessor checks prove complete strict base-configuration equality from real PostgreSQL, ordinary pending save and independent local draft readability, refusal of pause/deletion pending formats with original bytes preserved, history discovery and independently SHA-256-matching recovery of newly published content. All three cleanup/preservation postconditions are true. Candidate bytes are unchanged; final technical acceptance of the correction remains pending.

The targeted review accepted compatibility but required bounded task-exit cleanup when an inspection worker times out before job admission. `tooling/controls-cleanup-red.trx` records four failing new cleanup cases. The correction waits for the ownership-checked task to leave Running/Queued before removal, always joins/disposes its job through nested finally blocks, and covers context creation immediately after job creation. Failed cleanup cannot emit a success receipt. `tooling/controls-cleanup-green.trx` passes all **19** combined cleanup/client/previous rollback cases, including a stuck task, scheduler failure, ownership mismatch and failure before task creation. A fresh real `rollback-readonly-inspection-final.json` passes with joined psql, zero admitted deletions, the intended sole creator and zero grants; worker 38424 and its task exited, and owned-job/task cleanup is verified.

**Independent GPT-6-Astra/high concrete candidate gate: approved approach, no remaining requirement-backed blocker.** The reviewer explicitly accepts frozen 1.0.8 candidate `ddc02916cbfa431a9360f07be9ca3eaa` and guarded rollback to accepted 1.0.7. No actual normal-vault rollback rehearsal is claimed. Installed deployment/workflow acceptance remains required. A read-only TestHost mode now uses the existing authenticated installed pipe to retain complete before/after status snapshots; its bounded creator check cannot save or mutate. `installed-before.json` observes vault `67539278-9292-4870-93cd-a011419228dc`, revision 8, protection enabled, before any update. The snapshot host build passed with zero warnings/errors and exited successfully.
