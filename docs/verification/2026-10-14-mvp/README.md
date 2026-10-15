# Supplementary version-history MVP acceptance

Status: ready for the bounded supplementary production pilot on this PC. The
installed 1.0.12 candidate passes the applicable gates, including the explicitly
authorised Windows restart and independent final live acceptance. Automatic
protection is off after restoration of the original configuration; select and
save the intended folders, then deliberately enable it in Options. The source
change and evidence are in [PR #37](https://github.com/yagasoft/FluxVault/pull/37).
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
| Desktop-closed capture and verified new-location recovery | Installed 1.0.11: all 44 copied samples captured and independently restored in 90.213 s. Final 1.0.12: all 44 pass in 61.222 s, with the desktop closed and no manual capture. All 44 new versions also restore correctly after rollback and re-upgrade. Original configuration and all 354 earlier records remain preserved; automatic off/pending zero/uncertain zero restored. [Installed evidence](installed-upgrade-rollback.json). |
| Sustained ordinary use and foreground interference | [One-hour result](sustained-use.json): 120 verified saves/recoveries, no pending/uncertain work after each, desktop closed, original configuration/26 earlier versions preserved. [UI observations](ui-observations.json) pass the 2 s shell target and repeated navigation; first-use and startup dispatcher target misses remain below. |
| Delayed database startup | Focused regressions: 15 passed. Native same-endpoint delayed availability, unchanged bootstrap/binding, no pipe during four failed opens, capture/restart/recovery: 23 passed on each of IPv4 and IPv6. Private cluster removed, all jobs joined, normal installation unchanged. |
| Storage failure and existing destination preservation | [Native result](native-startup-storage/result.json): 30 checks pass on each IPv4/IPv6, including delayed startup, durable pending before storage outage, visible failure, recovery without another save, refused unconfirmed overwrite with sentinel preservation/no staging leak, and separately confirmed verified overwrite. [Cleanup](native-startup-storage/cleanup.json) and [wrapper join](native-startup-storage/wrapper-cleanup.json) pass. Existing accepted mirror/offline and interruption checks reused. |
| Pause/resume, stop while retaining history, reviewed deletion | Accepted NEXT-004/005 installed and native evidence reused within unchanged contracts, including automatic-off startup and paused service-restart persistence. The physical boot adds enabled-pending resumption; it is not a separate physical reboot of every control state. |
| Exact upgrade and exercised rollback | Independently reviewed [1.0.12 candidate](candidate.json), product source `216b8c8362047b226865e10c451b68f6c18b65c9`: 208 payload hashes match after upgrade, exercised 1.0.11 rollback and final re-upgrade. Bootstrap/authentication/ACL and the normal PostgreSQL process remain preserved. Both versions independently recover all 44 newly created sample versions. [Transition evidence](installed-upgrade-rollback.json). |
| Windows restart → automatic service → creator sign-in → reconcile | Passed after explicit consent. [Boot evidence](boot-acceptance.json): ten pre-sign-in observations retain one pending item; automatic delayed service startup then captures it after sign-in, with desktop closed and no source write or manual service start. Independent new-location SHA-256 matches. This boot did not observe a running service before sign-in; accepted native session-authority tests establish access gating. |
| Independent final acceptance | [Approved](independent-review.md) for the bounded supplementary pilot after independent inspection of the raw boot, recovery, collection, baseline and sign-in-restoration receipts. No remaining product or live-acceptance blocker. |

Raw private evidence roots: `I:\FluxVault Staging\MVP\be23b1606edf419f9c228728d93becec`
and final installation run `283216a322c04a14bb4a95a03b78485e` in the same parent.
Final native startup/storage run: `C:\ProgramData\FluxVault.Tests\MVPStartup\e7781e20745e4c19b7ab0f104b25988f`.
These retained paths contain identity-bound evidence; do not publish the source map.

### Evidence relocation, 15 October 2026

The owner authorised moving this task's retained files to I:. The two MVP runs
above were copied from `%USERPROFILE%\FluxVault Staging\MVP`: all 673 files,
70 directories and 7,157,257,022 logical bytes matched their recorded SHA-256,
length and last-write time. The destination retains protected access. Historical
receipts, hashes and C: path strings remain unchanged as provenance; the mapping
above locates their current copies. Retained probe binaries are archival and may
still enforce their original C: root. Newly built MVP fixtures require an exact,
non-empty UUID directory directly under `I:\FluxVault Staging\MVP`.

The active task checkout is now
`I:\Codex Workspaces\Worktrees\fluxvault-mvp-finalisation\FluxVault`.
Its 10,897 original non-metadata files, 1,002 directories and 11,900 effective
ACL entries matched before source retirement. A separate Git worktree registration
was created and attached to this chat before Codex archived the old C: checkout.
The original task branch and complete verified private checkout copy remain
retained. The unrelated primary changes and NEXT-006 planning files match their
previous hashes.

Twelve focused checks pass, including 20 actual PowerShell admission cases, and
the TestHost Release build has zero warnings/errors. The actual preparation wrapper
and joined installed snapshot actor pass from a fresh I: fixture. That snapshot
preserves the original configuration: automatic protection off, pending and
uncertain counts zero. This relocation changes fixture tooling only; no installed
payload, source document, service or database configuration was changed. Existing
MVP product acceptance remains applicable.

The first hosted run on `c664cf8` built without warnings/errors but failed the
new wrapper admission test because its runner had no I: PowerShell drive. Its
other 1,695 tests passed. The test now creates a drive only in its isolated child
process when absent; the admission assertions and real staging guards are unchanged.
The missing-drive cause was reproduced locally, and all twelve focused checks pass
after correction. The failed run and correction evidence remain pinned below.

The C: worktree checkout was retired, removing 1,319,096,593 logical bytes. The
two C: MVP run copies still occupy 7,157,257,022 bytes (6.67 GiB): automatic approval
review rejected their removal as “blocked by policy”, including the narrower exact
two-directory submission. Removal attempts have stopped. Their parent and siblings
remain untouched. This is a disk-cleanup blocker, not an MVP product gate.
[Relocation evidence](relocation.json) pins the private verification records.

Focused overwrite regression failed before the fix, then 14 relevant App checks
passed. [Final combined checks](combined-checks.json) pass: 515 App, 173 Windows and
397 Integration checks (389 ordinary and eight prepared-installation checks), with
no failures or skips. Hosted CI also passes all 608 Core checks, giving 1,685
ordinary checks on the complete branch. Builds report
zero warnings/errors. Historical failed fixture
runs remain private and are not counted as passes: `d312cef4a86c444fbadad14022f6489e`
injected storage failure before confirmed work admission;
`fadcebf674264bd08bb3c2c0c807fa13` reproduced the unconfirmed-overwrite defect.
Both joined all jobs, removed their private clusters and preserved the installation.

The first hosted CI run on `216b8c8` passed Core 608, Integration 389 and Windows
173 checks, but failed one of 515 App checks during fixture disposal. The Options
test had not joined its asynchronous close/reload. The bounded test correction
joins `OpenOptionsAsync`, including cleanup, without changing product code or
behaviour assertions. The three affected checks and all 515 App checks pass
locally; [correction evidence](ci-correction.json) retains the failed hosted run.
Fresh hosted CI on `6fbc229` passes all 1,685 ordinary checks with zero build
warnings/errors; [CI on pre-boot head `02f6a014`](https://github.com/yagasoft/FluxVault/actions/runs/38543027425)
also passes. This fixture correction does not change the
frozen `216b8c8` product candidate or invalidate its installed acceptance evidence.

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

## Live restart acceptance

The owner explicitly authorised one restart and temporary automatic-sign-in
suspension after saving work and confirming manual sign-in. The reviewed wrapper
submitted it once and joined `shutdown.exe` with exit code zero. The new Windows
boot began at 10:35:51.500 UTC on 14 October 2026. Ten observations from
10:36:29.380 to 10:36:38.464 UTC recorded no creator session, one durable pending
item and zero uncertain items. The creator was first observed at 10:36:39.490 UTC.

The service is configured for **Automatic (Delayed Start)**. It was still stopped
during the pre-sign-in observations and started automatically at 10:38:11.940 UTC.
The pending version was captured at 10:38:43.388 UTC: 171.888 seconds after boot
and 123.898 seconds after the creator was first observed. The observer made no
post-boot source write or manual service start, and the desktop remained closed.
Recovery to a new file independently matched SHA-256
`9DE8C81A750499DAE6AB27EC2A279666577A9714E6F209B01BB92B2F919316CA`.
This proves automatic startup and retained-work resumption, without claiming
immediate capture at sign-in or a running service waiting before this sign-in.
The previously accepted native session tests establish authority gating.

Automatic sign-in was restored to its original string value `1` at
10:38:19.068 UTC and independently rechecked. No account or password values were
read or changed. Collection joined all ten recorded observer/preparation actors,
removed the exact task, then joined its baseline-restoration helper. Independent
review found no live matches across fourteen recorded process identities. The
original configuration, four saved scopes and 442 earlier version records remain
preserved; automatic protection is off, with pending and uncertain counts zero.
All 208 installed payload hashes still match the frozen candidate. All 44 sampled
originals retain their recorded length and last-write timestamp. Normal PostgreSQL
restarted naturally with Windows; it was never separately restarted by the tests.
[Boot acceptance](boot-acceptance.json) pins the raw receipts and observations.

### Retained preparation and failure-boundary evidence

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
The user left the sign-in screen available before signing in for this trial.

The [exact-candidate preparation](exact-candidate-boot-preparation.json) passes
the read-only SYSTEM rehearsal on installed 1.0.12. Its five recorded processes
have exited and its rehearsal task is removed. This records preparation before
the later authorised boot; its observer has now been collected. The source and
candidate remain frozen; the later CI correction changes only a test fixture.

Before the trial, Windows had `AutoAdminLogon` set to the string `1`. The owner
consented to temporarily changing only that value to string `0`,
then restoring the recorded string `1` after manual creator sign-in, even if boot
acceptance or collection fails. No account/password values are needed or changed. The exact
value/type and before/after receipts will be retained under the protected boot
root. If preparation fails before submission, or Windows definitely refuses it,
collect the owned observer, restore the original FluxVault configuration and
restore that value. An ambiguous submission retains the observer and suspended
sign-in until the new boot or cancellation is established; never retry it or
prematurely roll back preparation.
Do not perform any of these sign-in changes without the owner's explicit consent.

The concrete boot run is `ae4b8f3536d043e0a688e1f4d2d4bc44`.
[`restart-mvp.ps1`](restart-mvp.ps1) pins this PC, creator, candidate and boot run.
Its read-only Check passes. Start additionally requires the recorded explicit
consent switch; it records and suspends the single sign-in value, invokes the
sealed Arm helper, verifies joined preparation and the exact observer task, then
submits `shutdown.exe /r /t 0` once without force-closing applications. A positive
timeout is deliberately avoided because [Windows would imply force-close](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown). The owner
must save work, confirm manual sign-in is available, wait approximately 60 seconds
at sign-in, sign in normally, keep FluxVault closed and return to this chat.
The observer verifies capture/recovery without Codex or FluxVault being open.
Collect its exact task/processes and invoke RestoreSignIn in a finally block,
independently of acceptance; it checks the current value before restoring it.
A blocked shutdown or missed pre-sign-in observation is not a pass and needs
reassessment. Never repeat an ambiguous restart submission.

Independent pre-boot review required corrections for ambiguous shutdown submission
and incomplete Arm cleanup. [Sixteen isolated boundary checks](restart-boundary-checks.json)
now exercise cleanup/join control flow with simulated tasks and processes,
including lost receipts, known refusal, partial Arm before task registration,
collection/restoration failure and unreceipted task identity. A live-actor
regression failed before the final correction: independent baseline mutation is
now refused until recorded preparation processes and the owned observer job have
exited. Windows sign-in restoration remains independent of that refusal. These
checks invoke no real shutdown or registry writes. The
[missing-consent check](restart-consent-guard.json) also confirms that Start refuses
without consent, preserving the real sign-in value and leaving no action intent
or armed test. These are harness checks, not evidence of a Windows restart.

The [owned recovery-output cleanup](recovery-reclaim.json) individually verifies
and removes 132 already accepted output files, reclaiming 3,256,641,999 bytes.
The corpus, working copies, raw receipts and repository history remain retained.
The copied original working documents remain outside all mutations.

The [observer rehearsal](boot-observer-rehearsal.json) passes actual SYSTEM
catalogue/session access, early failure before child launch, and resumable
collection after injected interruptions immediately after task removal and
baseline restoration. The last collection verifies the restored baseline
read-only. Tasks are removed and every recorded helper is joined. These checks
do not establish a Windows restart or authorise one.

Previously accepted native sign-out/re-entry, automatic-off startup and paused
service-restart persistence remain relevant independent evidence. The real
Windows restart gate adds automatic SCM startup, pending persistence before sign-in
and enabled resumption with retained pending work. This does not claim that every
control state was exercised through a separate physical Windows reboot.
