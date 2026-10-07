# Protection failure visibility and accessible names

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
