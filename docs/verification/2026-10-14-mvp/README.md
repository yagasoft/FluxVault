# Supplementary version-history MVP acceptance

Status: in progress. Production pilot readiness remains blocked until every
applicable gate below passes, including an explicitly authorised Windows restart.
This is continuation of NEXT-005 with bounded NEXT-009 measurement, not a new
roadmap programme or a restart of the completed NEXT-002/004/005 audits.

## Boundary and workload

One logical vault per Windows installation protects multiple selected local
folders while the creator is signed in. Saved automatic preference and Pause
survive restart. The service starts automatically, waits for authorised source
access, then reconciles retained work without opening the desktop. Automatic off
and Pause never become enabled by startup. Saved files must be locally available,
readable and stable for capture; this is not a promise to retain every keystroke
or every intermediate save within the cadence window.

The owner authorised read-only sampling of a working Analysis folder. Originals
must not be modified. Private source names and contents remain outside Git.
`prepare-mvp-corpus.ps1` copied 44 samples across 24 extensions, totalling
1,085,547,333 bytes; the largest is 441,612,566 bytes. The envelope contains
DOCX, XLSX/XLSM, PPTX, PDF, VSDX, MPP, MSG, Draw.io, CSV, JSON/JSONL/NDJSON,
Markdown/text/HTML/SVG/VTT, JPG/PNG, MP4 and ZIP/RAR/7Z. Source/copy SHA-256,
size and modification-time observations are retained privately. These checks
prove exact saved bytes, not application-level consistency or project reopening.
The representative trial uses locally hydrated ordinary files, Balanced cadence,
compression Off and a local NTFS primary repository. Wider sizes, linked/open
professional projects and other storage/performance profiles are not certified.

Use FluxVault alongside an independently tested backup. This task has not
verified that such a backup exists. NEXT-006 independent disaster recovery keeps
its design and backlog position outside this MVP's critical path. Logged-off
capture, standalone disaster protection, VSS, advanced professional consistency,
full shell redesign, theme/accessibility and optional expansion remain deferred.

## Smallest remaining change

The installed 1.0.11 payload matches all 208 retained manifest entries. Its
automatic preference was off, pending/uncertain work zero, with four saved scopes
and 26 earlier versions. PR #36 is merged at `aa0b81d`; the primary checkout and
the separate dirty NEXT-006 planning worktree are preserved.

The concrete reliability defect is service initialisation before joining the
Windows service host. A slow PostgreSQL start could exhaust SCM restart attempts.
Initialisation now runs inside the supervised host, retrying recognised temporary
availability failures with a cancellable defaulted policy. Authentication,
binding, ownership, schema and integrity refusals remain fatal. No service pipe
is admitted before verified initialisation. There is no catalogue/schema change.
Options copy explains selection, deliberate enablement, startup and support limits.

The native storage trial also reproduced a recovery defect: single-version IPC
ignored the existing `OverwriteConfirmed` field. The installed executor now
requires a new destination unless that field is true, and WPF carries true only
after affirmative overwrite confirmation. Existing non-replacing publication
also protects a destination appearing after the initial check. A denied restore
preserves a distinct sentinel; a separate confirmed operation restores verified
bytes. This changes no schema or durable request format. Rolling back to 1.0.11
reintroduces the older behaviour, so fallback recovery must use a new destination.

Sequence: reuse baseline evidence; add focused startup regressions and native
failure/recovery proof; run copied workload and sustained use; inspect actual WPF
rendering and NEXT-009 timings; independently review the complete change; freeze
an exact candidate; check and exercise upgrade/rollback; validate installed
operation; prepare and request consent for the live Windows restart; obtain
independent final acceptance. Reassess after the startup/failure and workload/UI
batches; do not expand into NEXT-006 or a shell rewrite.

Risks are startup retries admitting an invalid vault, loss/false acknowledgement
of pending work, restoring over existing files, accidental source/installation
changes, foreground stalls and unjoined helpers. Native identity checks, durable
state, new-location recovery hashes, exact target guards, timing evidence and
joined process receipts address these risks. Immediate rollback is retained
1.0.11, with no schema downgrade or consent invalidation required; deliberate
history deletion remains irreversible. The older checked 1.0.9 rollback stays
retained but is not the immediate fallback for this change.

## Acceptance ledger

| Gate | Evidence and current result |
| --- | --- |
| Deliberate selection/enable; clear pending/attention | Existing NEXT-004/005 accepted controls reused; actual Options view-model enables the owned copied corpus. Actual WPF Protection, File browser, Repository and Options screens inspected; clear automatic-off and explicit setup/support wording. No mouse/keyboard input; windows remained inactive. |
| Desktop-closed capture and verified new-location recovery | Installed 1.0.11: all 44 copied samples captured and independently restored in 90.213 s; no manual capture. Indexed history p95 15.402 ms, maximum 27.438 ms over 30 observations. Earlier 26 versions remain readable and original configuration was restored. |
| Sustained ordinary use and foreground interference | [One-hour result](sustained-use.json): 120 verified saves/recoveries, no pending/uncertain work after each, desktop closed, original configuration/26 earlier versions preserved. [UI observations](ui-observations.json) pass the 2 s shell target and repeated navigation; first-use and startup dispatcher target misses remain below. |
| Delayed database startup | Focused regressions: 15 passed. Native same-endpoint delayed availability, unchanged bootstrap/binding, no pipe during four failed opens, capture/restart/recovery: 23 passed on each of IPv4 and IPv6. Private cluster removed, all jobs joined, normal installation unchanged. |
| Storage failure and existing destination preservation | [Native result](native-startup-storage/result.json): 30 checks pass on each IPv4/IPv6, including delayed startup, durable pending before storage outage, visible failure, recovery without another save, refused unconfirmed overwrite with sentinel preservation/no staging leak, and separately confirmed verified overwrite. [Cleanup](native-startup-storage/cleanup.json) and [wrapper join](native-startup-storage/wrapper-cleanup.json) pass. Existing accepted mirror/offline and interruption checks reused. |
| Pause/resume, stop while retaining history, reviewed deletion | Accepted NEXT-004/005 installed and native evidence reused within unchanged contracts; startup persistence remains part of live restart acceptance. |
| Exact upgrade and exercised rollback | Pending candidate review/build/install; retained 1.0.11 payload and installers verified. |
| Windows restart → automatic service → creator sign-in → reconcile | Open. Isolated retry proof and earlier sign-out/re-entry evidence do not substitute for this live gate. No reboot consent yet. |
| Independent final acceptance | Open. Independent startup design/implementation review accepts the focused fix; candidate and final live review remain required. |

Raw private evidence root: `%USERPROFILE%\FluxVault Staging\MVP\be23b1606edf419f9c228728d93becec`.
Native delayed-start run: `C:\ProgramData\FluxVault.Tests\MVPStartup\b790fe7a2c614fd5b63bf309e428c572`.
These retained paths contain identity-bound evidence; do not publish the source map.

Focused overwrite regression failed before the fix, then 14 relevant App checks
passed. [Final combined checks](combined-checks.json) pass: 515 App, 173 Windows and
397 Integration checks (389 ordinary and eight prepared-installation checks), with
no failures or skips. The unchanged 608 Core checks are reused from the accepted
baseline, giving 1,685 ordinary checks across fresh and reused evidence. Builds report
zero warnings/errors. Historical failed fixture
runs remain private and are not counted as passes: `d312cef4a86c444fbadad14022f6489e`
injected storage failure before confirmed work admission;
`fadcebf674264bd08bb3c2c0c807fa13` reproduced the unconfirmed-overwrite defect.
Both joined all jobs, removed their private clusters and preserved the installation.

## Performance and usability limits

The [sample envelope](supported-corpus.json) records actual copied sizes by type.
The hour-long trial alternated DOCX, XLSX, PPTX, CSV, PDF and VSDX saves every
30 seconds. Protected saves measured median 4.58 ms, p95 16.41 ms and maximum
20.07 ms. Save through independently verified recovery measured p95 15.35 s and
maximum 27.65 s; this includes cadence, capture, restore and hash verification,
not capture execution alone. Combined history/status measured p95 83.04 ms.
The immediately preceding copy control had p95 17.80 ms; cache/order effects mean
these observations do not establish a causal overhead comparison.

The service used 2.65% average machine CPU over the measured 2,502 s loaded
interval on 24 logical processors, with a 226,660,352-byte process peak working
set. This is a loaded measurement, not the idle target. The accepted ten-minute
1.0.11 idle evidence remains relevant to unchanged steady-state capture code.
Concurrent workstation, build and test activity was present.

Two fresh-process WPF observations rendered in 0.822–0.836 s. Repeated navigation
took 2.08–25.49 ms. First Repository navigation took 118–148 ms and Diagnostics
191–258 ms; Options load/render took approximately 116 ms. Startup's maximum
dispatcher operation was 439–453 ms, consistent with the accepted 450–466 ms
1.0.11 limitation. These exceed NEXT-009's proposed 100 ms limits; do not claim
all performance targets are met. No MVP-blocking responsiveness regression was
observed. Cache state was uncontrolled; these are not certified cold/warm trials,
large-vault scale tests or full DB/IO resource attribution. Wider NEXT-009 work
remains deferred. The existing UI is functional within this envelope; its full
redesign remains separate.

## Prepared live restart gate

`eng/test-mvp-boot.ps1` prepares a sealed SYSTEM observer from the reviewed
candidate, rehearses read-only catalogue/session access, and records exact
payload/task/process identities. It never reboots or starts either service.
Only after explicit consent may Arm select a fresh owned test source, capture
an initial version, retain a second saved version as durable pending work and
resume the saved automatic preference. All preparation helpers exit before the
separate reboot submission. The one-minute fixture same-file interval leaves
time to submit the restart; capture timestamps must prove the pending version
was actually captured after the new boot.

At startup the observer verifies Windows boot identity and automatic service
binding, records retained work while no creator session exists, then uses the
creator's real ordinary token to find and restore the post-boot version. It makes
no source writes or manual service starts. Recovery hashes, absence of the desktop,
process exit and task removal are checked. Collection restores the original
automatic-off configuration and verifies earlier history. If the observer misses
pre-sign-in waiting, that gate remains open even if subsequent capture passes.
The user will need to leave the sign-in screen visible briefly before signing in.

The [observer rehearsal](boot-observer-rehearsal.json) passes actual SYSTEM
catalogue/session access, early failure before child launch, and resumable
collection after injected interruptions immediately after task removal and
baseline restoration. The last collection verifies the restored baseline
read-only. Tasks are removed and every recorded helper is joined. These checks
do not establish a Windows restart or authorise one.

Previously accepted native sign-out/re-entry, automatic-off startup and paused
service-restart persistence remain relevant independent evidence. The real
Windows restart gate specifically adds automatic SCM startup, pre-sign-in waiting
and enabled resumption with retained pending work. This does not claim that every
control state was exercised through a separate physical Windows reboot.
