# Protection failure visibility and accessible names

Current status, 7 October: native capture now supplies usable pixels and the
agent has completed the keyboard, failed-save, pending-edit, navigation,
non-empty inventory and actual DPI observations below. The user has explicitly
deferred accessibility and theme testing, including Narrator and contrast
remediation. Other UI/UX checks remain in scope; successful installed
save → backup → history → verified recovery and its visible states still require
the separately approved rollout. Earlier capture/manual-session dependencies
below are historical, not a request for further user testing assistance.

The actual desktop WPF window exposed two focused presentation defects: the
Protection page showed the full save-failure explanation only through a trimmed
header/tooltip, and primary composite buttons plus the repository field lacked
useful native automation names. The existing failed-save logic already retained
the edit and prevented dependent backup. This correction changes presentation,
not command, ownership, binding or receipt contracts.

Protection now displays the complete save and local-draft messages below the
mirror summary, with wrapping and polite live-region metadata. Seven primary
actions have explicit semantic names; the service action remains bound to its
current label, and the repository field is named. File-browser messages and
existing controls remain in place. No broader navigation redesign, new setting,
scheduler or multi-vault concept is added.

Three rendered-control regressions failed before the XAML correction and pass
after it. They inspect actual WPF automation peers, drive the actual view-model
save-before-backup flow, reopen the real file configuration store and measure
the full message within the visible Protection viewport at widths 1080/1440.
They do not inspect source strings. The initial test diagnostic is retained:
record equality compared collection references, so the store invariant was
corrected to compare complete serialised values before obtaining the genuine
missing-message reds. The empty native control name was already a genuine red.

All 478 App tests pass without skips. The final rendered subset passes 3/3,
and the complete final Release build has zero warnings/errors. The actual WPF
renders [at minimum width](protect-failure-1080.png) and
[at wider width](protect-failure-1440.png) show the full failure and local
retention explanations. These are logical WPF renders, not changed monitor DPI
or proof of high-contrast/Narrator behaviour.

The default-off TestHost interactive mode extends the existing disposable
single-vault draft fixture. It uses the actual window/view-model and local
stores with an explicitly rejecting client; it never connects to a service or
database. Its eight-minute dispatcher lifetime, joined close/flush and bounded
parent deadline prevent unattended windows. Both native observations prove one
save, zero backups, a retained editable repository path and the matching local
draft, then exit with code zero and remove their exact owned scratch roots.
The initial run used keyboard selection/text entry and closure; the corrected
run verified values through the native accessibility control after injected
typing did not appear. This is not a claim of complete keyboard validation.

[Before](before-accessibility.txt) and [after](after-accessibility.txt) native
trees confirm the corrected primary names, named editable field and complete
Protection messages. The second desktop capture returned stale/blank pixels
and inconsistent fragments after resizing despite updated native controls.
[That capture](capture-inconsistent.png) is retained as diagnostic evidence,
not visual acceptance. Human observation was requested to distinguish a capture
limitation from an actual rendering fault; this ambiguity remains open unless
that observation resolves it. The fixture closed at its finite lifetime and
was joined; it is no longer left running for the question.

Both native cleanup records and final census confirm the two owned WPF process
identities and roots are absent. Installed service identities and PostgreSQL
authentication/configuration hashes match before/after. Build/test workers are
joined, and unrelated processes are preserved. Self-review covers the focused
presentation change, real controls/state flow and fixture isolation. Final
whole-branch independent G01 review remains required.

This supplies bounded implemented-screen evidence. Real-service successful
save/backup/history/verified recovery retains its separate native proof; the
rejecting UI client does not substitute for it. Complete D01/S3 keyboard,
Narrator, DPI, high contrast, larger inventory responsiveness, packaged identity,
remaining source/background checks, final G01 and normal rollout remain open.

The completion-mode retry initialises the restored `node_repl`/Computer Use
interface, but native enumeration repeatedly returns handle 4461090 while capture
rejects it and reports handle 198320. The documented single selection retry fails.
No UI input is issued after that failure. The new disposable fixture process 66128
is identified by its native start time/image, stopped and joined; its root is
removed and normal services/authentication/configuration are unchanged. The
parent exits with the expected forced-stop diagnostic, which is not acceptance.
Actual keyboard/Narrator/DPI/high-contrast/responsiveness and visual ambiguity
remain for guided manual observation or repaired capture tooling. The user has
been asked to choose that concrete path. No normal runtime is launched.

The user's 5 October approval selected guided manual observation. Fresh attempt
`8cc8ee6adf884f2da24c76e272b7e260` opened the real disposable WPF window and bound
its native control tree. Pixel capture remained blank. The user received the
keyboard/save-failure and Narrator/actual-scale/contrast instructions, but no
observations arrived during the eight-minute lifetime. Its [result](manual-attempt-8cc8ee6adf884f2da24c76e272b7e260/ui-result.json)
records zero saves/backups and no pending edit; it does not establish those
workflows or visual/accessibility acceptance. The [cleanup](manual-attempt-8cc8ee6adf884f2da24c76e272b7e260/cleanup.json)
records joined exit zero, exact root removal and unchanged normal installation.
The earlier records/renders remain unchanged. No new unattended UI is left
running; another bounded session awaits the user's readiness. Larger-inventory
responsiveness and the successful installed workflow still require their real
interfaces; the empty rejecting-client window cannot prove them.

On 7 October the current Computer Use `sky.list_windows` interface successfully
enumerates the unlocked desktop's windows. There is no active FluxVault fixture
window; this read-only check proves tool availability, not repaired pixels or
desktop acceptance. The readiness question remains pending. Another bounded
session must supply actual keyboard/focus/failure-state, Narrator, monitor DPI,
contrast/rendering and non-empty inventory responsiveness observations. No
unattended UI session was launched and no unrelated window received input.

The same fixture now accepts a bounded synthetic working-file inventory (default
zero in TestHost, 10,000 in the guided wrapper, maximum 50,000). This addresses
D01's non-empty inventory observation gap using the existing actual WPF window,
view-model and grid; metadata is synthetic and no large working files, service
or database are created. Each wrapper run preserves its evidence in a fresh
`desktop-attempt-<fixture-id>` directory instead of overwriting earlier records.

The [five focused regressions](inventory-final.trx) pass: the unchanged empty
default, 10,000 loaded files, rejected save with zero dependent backups, visible
failure/draft state, bounded realised rows and final-row reachability, plus three
invalid inventory arguments refused before UI state creation. The retained
[red](inventory-red.trx) precedes implementation. The 10,000-item run realised
17 rows, refreshed/layouted in 679 ms and realised the final row in 23 ms, with
189 MiB peak working set. These are single-run fixture layout measurements,
not input-latency thresholds or physical-file/service/database benchmarks.

The controlled native attempt
[`f6c76c0f09254834ab17f1d689e05776`](desktop-attempt-f6c76c0f09254834ab17f1d689e05776/ui-result.json)
also loads 10,000 files, realises 17 rows and reaches the last row. Native
enumeration and accessibility expose the real inventory and controls, but two
captures (with one fresh selection retry) both return blank client pixels.
A checkbox input produces no confirmed selection change. Alt+F4 closes the
owned window; its [cleanup](desktop-attempt-f6c76c0f09254834ab17f1d689e05776/cleanup.json)
confirms successful joined exit, removed root and unchanged normal installation.
The run records zero saves/backups, so it supplies no new native command-flow
acceptance. No temporary UI/build/test process remains running.

D01 still needs a ready human session to distinguish blank capture from an
actual rendering defect and observe keyboard/focus, failure explanations,
Narrator, actual monitor scaling, high contrast and native responsiveness.
The readiness question is pending; no further timed session is left unattended.
Candidate `51b103b3c75645dfaa36600e2cbe07a5` and product source are unchanged.

## Native desktop observations, 7 October

The agent operated two bounded instances of the existing actual WPF fixture
through the native Computer Use interface. These are agent observations, not
human test reports. Fresh window selection/capture supplied usable pixels;
one later stale capture was retried before further input. No product, fixture
or installer code changed.

In [attempt 22adaf...](desktop-attempt-22adaf2210cd47a6b234694e56818794/ui-result.json),
selecting the first synthetic file and activating Run backup now produced the
full persistent explanation: changes are kept and backup has not started.
The selected checkbox and pending Added row remained visible. The result and
real local stores record one rejected save, zero backups and a retained draft.
Tab/Shift+Tab moved visible focus between controls; Tab into the file checkbox
and Space toggled its pending selection without invoking a service command.
Ctrl+End reached the final row of the 10,000-file grid, Ctrl+Home returned to the
first row and the earlier selection remained. File browser → Protection
navigation retained the complete failure/draft messages without overlap.
Options correctly explained why pending changes must be saved or discarded
before opening it. No freeze or lost pending edit was observed during these
interactions; this is a bounded observation, not a general latency benchmark.

In [attempt 808cde...](desktop-attempt-808cdedaca1a4609b94d62c2201237b2/ui-result.json),
the same rejected-save contract remained visible while Windows Settings changed
the actual monitor scale from 175% to 200%. File browser and Protection retained
legible primary controls, complete messages and pending edits without overlap.
Scale was restored to 175% and verified in Settings. Both runs loaded 10,000
synthetic entries, realised 17 rows and reached the final row. Their respective
refresh/layout measurements were 660/1,003 ms, final-row layout 20/120 ms and
peak working set approximately 220/227 MiB. These single-run fixture metrics
do not measure physical-file capture, service/database performance or isolated
desktop input latency. The earlier minimum-width real-control renders and
focused virtualisation regressions remain applicable.

Before the user's deferral, temporary Aquatic contrast exposed a presentation
defect: some fixed light surfaces combined with system light foregrounds made
primary button labels and the Mirrors summary unreadable. Record this as
deferred accessibility/theme work, not a pass. No correction was started.
The original contrast setting None was restored and verified. Narrator was
never launched. The user subsequently confirmed that all other UI and UX work
must continue; this deferral does not remove command-state clarity, navigation,
layout, responsiveness or installed recovery observations from D01.

Both [first cleanup](desktop-attempt-22adaf2210cd47a6b234694e56818794/cleanup.json)
and [second cleanup](desktop-attempt-808cdedaca1a4609b94d62c2201237b2/cleanup.json)
confirm joined exit zero, exact scratch-root removal and unchanged normal
configuration/authentication/service identities. Windows Settings was closed
and its captured process identity exited. A final census found no Settings,
Narrator or TestHost process. No temporary UI process remains running.
